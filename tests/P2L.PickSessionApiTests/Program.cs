using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared.Enums;

static void Check(bool value, string message) {if (!value) throw new Exception(message);}
static async Task Until(Func<bool> ready) {using var stop = new CancellationTokenSource(10000); while (!ready()) await Task.Delay(20, stop.Token);}
const string token = "test-pick-session-token-0123456789abcdef";
var configJson = JsonDocument.Parse("""{"pickSessionApi":{"enabled":true,"url":"https://gateway.example/poll","token":"test-pick-session-token-0123456789abcdef"}}""");
Check(PickSessionApiSettings.FromRemote(configJson.RootElement, ["01","02"]).BlockNos.Length == 2, "licensed blocks missing");
Check(!PickSessionApiSettings.FromRemote(JsonDocument.Parse("{}").RootElement, ["01"]).Enabled, "optional config enabled by default");
var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
await using var server = builder.Build();
var sync = new object(); object[] commands = []; string[] inactive = []; var calls = 0; var unauthorized = false;
object Batch(string block, string session, bool relight, params (string address, string id)[] items) => new {
    type = "batch_start", blockNo = block, correlationId = session, relight,
    items = items.Select(i => new {address = i.address, quantity = 2, pickingDetailId = i.id, commandItemId = "detail:"+i.id, color = "Green", ledMode = "On", buzMode = "Off"}).ToArray()
};
server.MapPost("/poll", async (HttpContext ctx) => {
    Check(ctx.Request.Headers.Authorization == "Bearer "+token, "Bearer token missing");
    using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
    Check(body.RootElement.EnumerateObject().Count() == 1, "poll must only send controller blockNos");
    Check(body.RootElement.GetProperty("blockNos").GetArrayLength() == 2, "block scope missing");
    Interlocked.Increment(ref calls);
    if (unauthorized) {ctx.Response.StatusCode=401; return;}
    object payload;
    lock(sync) {
        payload = new {commands, inactiveBlocks = inactive};
        // Relight is consumed once, just like the gateway API.
        if (commands.Length > 0) commands = [Batch("01","S1",false,("0008","D1"),("0014","D2")), Batch("02","S2",false,("0017","D4"))];
    }
    await ctx.Response.WriteAsJsonAsync(payload);
});
await server.StartAsync();
var dir = Path.Combine(Path.GetTempPath(), "p2l-poll-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
await using var controller = new FakeController();
await using var gateway = new AioiGateway("127.0.0.1",controller.Port,NullLogger.Instance);
var registry = new LightModuleRegistry();
var outbox = new ConfirmationOutbox(Path.Combine(dir,"confirms.db"));
using var engine = new PickingEngine(gateway,registry,NullLogger<PickingEngine>.Instance,new BlockPolicy(["01","02"]),outbox);
var labels = new DeviceLabelStore(Path.Combine(dir,"devices.db"),NullLogger<DeviceLabelStore>.Instance);
await gateway.StartAsync();
lock(sync) commands = [Batch("01","S1",false,("0008","D1"),("0014","D2")), Batch("02","S2",false,("0017","D4"))];
var bridge = new MqttBridgeService(new MqttSettings {
    Host="127.0.0.1",Port=1,Tls=new(){Enabled=false},OfflineBufferPath=Path.Combine(dir,"offline.jsonl"),
    PickSessionApi=new(){Enabled=true,Url=server.Urls.Single()+"/poll",Token=token,BlockNos=["01","02"],PollIntervalMs=100}},
    gateway,engine,registry,new PreemptionGate(),null,labels,new DeviceScanService(gateway,registry,NullLogger<DeviceScanService>.Instance),NullLoggerFactory.Instance,confirmations:outbox);
await bridge.StartAsync();
await Until(() => registry.Get("0017")?.Status == ModuleStatus.Operating && registry.Get("0014")?.Status == ModuleStatus.Operating);
Check(!bridge.IsConnected,"test requires MQTT offline");
await engine.StartBatchAsync(new[] {("0008","D1"),("0014","D2")}.Select(i => new P2L.Shared.Models.PickingInstruction {
    Address=i.Item1, PickingDetailId=i.Item2, CommandItemId="detail:"+i.Item2, Quantity=2,
    BlockNo="01", CorrelationId="S1", Color=LightColor.Green, LedMode=LightState.On, BuzMode=LightState.Off}).ToArray());
var before = controller.Commands.Count;
var polled = calls; await Until(() => calls >= polled+3);
Check(controller.Commands.Count == before,"poll repeats relit the same session");
Console.WriteLine("PASS polling lights two blocks while MQTT is offline; repeated snapshots do not relight");
controller.NackNextP1 = true;
lock(sync) commands = [Batch("01","S1",true,("0008","D1"),("0014","D2")),Batch("02","S2",false,("0017","D4"))];
await Until(() => controller.Commands.Any(c => c.StartsWith("P501")));
var afterRelight = controller.Commands.Count;
polled = calls; await Until(() => calls >= polled+3);
Check(controller.Commands.Count == afterRelight,"one relight repeated");
Console.WriteLine("PASS one-shot relight applies once");
await controller.Confirm("0008"); await Until(() => registry.Get("0008")?.Status == ModuleStatus.Completed);
before = controller.Commands.Count; polled = calls; await Until(() => calls >= polled+3);
Check(controller.Commands.Count == before,"stale snapshot relit a locally confirmed detail");
Console.WriteLine("PASS Confirm retained locally prevents stale API replay");
lock(sync) {commands=[]; inactive=["02"];}
await Until(() => registry.Get("0017")?.Status != ModuleStatus.Operating);
Check(registry.Get("0014")?.Status == ModuleStatus.Operating,"Deactive affected another block");
Console.WriteLine("PASS API Deactive terminates only its block");
unauthorized=true; before=controller.Commands.Count; polled=calls; await Until(() => calls >= polled+2);
Check(controller.Commands.Count==before,"API failure changed lights");
await bridge.DisposeAsync(); // Dispose is tested separately below by stopping polling.
var stoppedCalls=calls; await Task.Delay(250); Check(calls==stoppedCalls,"polling survived shutdown");
Console.WriteLine("PASS API errors preserve existing work; shutdown stops polling");
await server.StopAsync();

sealed class FakeController : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _send = new(1,1);
    private readonly Task _loop;
    private NetworkStream? _stream;
    private TcpClient? _client;
    public ConcurrentQueue<string> Commands {get;} = new();
    public bool NackNextP1;
    public string? FailBlock;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public FakeController() {_listener.Start(); _loop = Task.Run(Run);}
    public Task Confirm(string address) => Send(new AioiPacket {SequenceNo = 0, Command = "t" + address + "00"});
    private async Task Send(AioiPacket packet)
    {
        await _send.WaitAsync(_stop.Token);
        try {await _stream!.WriteAsync(packet.ToBytes(), _stop.Token);} finally {_send.Release();}
    }
    private async Task Run()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(_stop.Token); _stream = _client.GetStream();
            while (!_stop.IsCancellationRequested)
            {
                var header = new byte[8]; await _stream.ReadExactlyAsync(header, _stop.Token);
                var tail = new byte[int.Parse(Encoding.ASCII.GetString(header,4,4)) + 1]; await _stream.ReadExactlyAsync(tail, _stop.Token);
                var packet = AioiPacket.TryParse(header.Concat(tail).ToArray(), out _)!;
                if (packet.Command == "O") continue; // ack for controller interrupt
                Commands.Enqueue(packet.Command);
                var nack = (NackNextP1 && packet.Command.StartsWith("P1")) || (FailBlock is not null && packet.Command.StartsWith("P1" + FailBlock));
                if (nack) NackNextP1 = false;
                await Send(new AioiPacket {SequenceNo = packet.SequenceNo, Command = nack ? "n" : "o"});
            }
        }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or SocketException) { }
    }
    public async ValueTask DisposeAsync() {await _stop.CancelAsync(); _client?.Dispose(); _listener.Stop(); await _loop; _stop.Dispose();}
}
