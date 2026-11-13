using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.CLI.Licensing;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared;
using P2L.Shared.Enums;
using P2L.Shared.Models;

static void Check(bool condition, string message) {if (!condition) throw new Exception(message);}
static async Task Reject(Func<Task> action) {try {await action();} catch (InvalidOperationException) {return;} throw new Exception("Expected conflict rejection");}
static PickingInstruction Light(string block, string session, string address, int quantity = 1) => new() {BlockNo = block, CorrelationId = session, Address = address, Quantity = quantity};
static string[] FrameAddresses(string frame) => Enumerable.Range(0, (frame.Length - 4) / 9).Select(i => frame.Substring(4 + i * 9, 4)).ToArray();
static async Task Until(Func<bool> condition) {using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, limit.Token);}

foreach (var json in new[] {"{\"blockNo\":\"01\",\"blockNos\":[\"01\",\"02\"]}", "{\"blockNo\":\"00\",\"blockNos\":[\"00\"]}"})
    Check(RemoteBlocks.Read(JsonDocument.Parse(json).RootElement)!.Length > 0, "Valid licensed blocks rejected");
Check(RemoteBlocks.Read(JsonDocument.Parse("{\"blockNo\":\"01\"}").RootElement) is null, "Legacy API must remain readable");
foreach (var list in new[] {"[]", "[\"00\",\"01\"]", "[\"01\",\"01\"]", "[\"02\"]", "[\"1\"]"})
{
    try {RemoteBlocks.Read(JsonDocument.Parse("{\"blockNo\":\"01\",\"blockNos\":" + list + "}").RootElement); throw new Exception("Bad allowlist accepted");}
    catch (InvalidDataException) { }
}
Console.WriteLine("PASS: licensed block list and legacy API compatibility");

var directory = Path.Combine(Path.GetTempPath(), "p2l-multi-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
await using var controller = new FakeController();
await using var gateway = new AioiGateway("127.0.0.1", controller.Port, NullLogger.Instance);
var registry = new LightModuleRegistry();
using var engine = new PickingEngine(gateway, registry, NullLogger<PickingEngine>.Instance, new BlockPolicy(new[] {"01", "02"}));
var labels = new DeviceLabelStore(Path.Combine(directory, "devices.db"), NullLogger<DeviceLabelStore>.Instance);
var telemetry = new Capture();
var mqtt = new MqttBridgeService(new MqttSettings {Host = "127.0.0.1", Port = 1, Tls = new MqttTlsSettings {Enabled = false}, OfflineBufferPath = Path.Combine(directory, "offline.jsonl")},
    gateway, engine, registry, new PreemptionGate(), null, labels, new DeviceScanService(gateway, registry, NullLogger<DeviceScanService>.Instance), NullLoggerFactory.Instance, telemetry: telemetry);
await mqtt.StartAsync(); await gateway.StartAsync();
var confirmed = new ConcurrentQueue<(string Address, string? Block, string? Session)>();
var completed = new ConcurrentQueue<(string Block, string? Session)>();
engine.ItemConfirmed += (e,s) => confirmed.Enqueue((e.Address, s?.BlockNo, s?.CorrelationId));
engine.BlockBatchCompleted += (b,s) => completed.Enqueue((b,s));
await engine.StartBatchAsync(new[] {Light("01", "SESSION-A", "0001"), Light("01", "SESSION-A", "0002")});
await engine.StartBatchAsync(new[] {Light("02", "SESSION-B", "0021"), Light("02", "SESSION-B", "0022")});
var sent = controller.Commands.Count;
await engine.StartBatchAsync(new[] {Light("01", "SESSION-A", "0001"), Light("01", "SESSION-A", "0002")});
Check(controller.Commands.Count == sent, "Duplicate batch relit lights");
await Reject(() => engine.StartBatchAsync(new[] {Light("01", "OTHER", "0003")}));
await Reject(() => engine.QuickLightAsync("0002", 3, correlationId: "OTHER", blockNo: "01"));
await Reject(() => engine.QuickLightAsync("0001", 3, correlationId: "SESSION-B", blockNo: "02"));
await Reject(() => engine.QuickLightAsync("0040", 1, correlationId: "X", blockNo: "03"));
Check(controller.Commands.Count == sent, "Rejected ownership command reached hardware");
Console.WriteLine("PASS: two sessions active; duplicate batch, busy block, duplicate address and unlicensed block protected before hardware write");

await engine.QuickLightAsync("0003", 3, correlationId: "SESSION-A", blockNo: "01");
var frame = controller.Commands.Last(c => c.StartsWith("P1"));
Check(frame.StartsWith("P101") && FrameAddresses(frame).SequenceEqual(new[] {"0001", "0002", "0003"}), "QuickLight mixed two blocks");
controller.NackNextP1 = true;
await engine.QuickLightAsync("0023", 4, correlationId: "SESSION-B", blockNo: "02");
var overwrite = controller.Commands.Last(c => c.StartsWith("P5"));
Check(overwrite.StartsWith("P502") && FrameAddresses(overwrite).SequenceEqual(new[] {"0021", "0022", "0023"}), "P5 fallback crossed blocks");
var beforeMixed = controller.Commands.Count;
await Reject(() => gateway.InstructBatchAsync(new[] {Light("01", "A", "0008"), Light("02", "B", "0040")}));
Check(controller.Commands.Count == beforeMixed, "Mixed block rejected after hardware side effects");
Console.WriteLine("PASS: P1 and P5 isolate each block; low-level mixed block input fails before Am1");

await controller.Confirm("0001"); await controller.Confirm("0021"); await controller.Confirm("0002");
await Until(() => confirmed.Count == 3);
Check(completed.IsEmpty, "Incremental light was not tracked in its session");
await controller.Confirm("0003"); await Until(() => completed.Count == 1);
Check(completed.Single() == ("01", "SESSION-A") && registry.Get("0022")!.Status == ModuleStatus.Operating, "Completion affected other block");
await controller.Confirm("0003"); await Task.Delay(50); Check(confirmed.Count == 4, "Duplicate confirmation was forwarded");
Check(confirmed.All(e => e.Session == (e.Block == "01" ? "SESSION-A" : "SESSION-B")), "Confirmation assigned wrong session");
await Until(() => telemetry.Entries.Any(e => e.Contains("batch_completed")));
var completion = telemetry.Entries.Select(s => JsonDocument.Parse(s)).Select(d => d.RootElement).First(e => e.TryGetProperty("type",out var t) && t.GetString() == "batch_completed");
Check(completion.GetProperty("blockNo").GetString() == "01" && completion.GetProperty("correlationId").GetString() == "SESSION-A", "MQTT completion lost routing keys");
Console.WriteLine("PASS: interleaved hardware confirmations, incremental pending tracking, duplicate suppression and block/session event metadata");

await engine.StartBatchAsync(new[] {Light("01", "SESSION-C", "0008")});
await Reject(() => engine.FinishBatchAsync("01", correlationId: "SESSION-A"));
await engine.FinishBatchAsync("01", correlationId: "SESSION-C");
Check(controller.Commands.Last() == "D01" && registry.Get("0022")!.Status == ModuleStatus.Operating && registry.Get("0023")!.Status == ModuleStatus.Operating, "Scoped finish affected other row");
await controller.Confirm("0022"); await controller.Confirm("0023"); await Until(() => completed.Count == 2);
Check(completed.Last() == ("02", "SESSION-B"), "Other session failed to complete independently");
Console.WriteLine("PASS: scoped cancellation ignores stale session and leaves the other row working");

await Task.WhenAll(engine.QuickLightAsync("0010", 1, correlationId: "RACE-1", blockNo: "01"), engine.QuickLightAsync("0030", 1, correlationId: "RACE-2", blockNo: "02"));
var beforeStop = controller.Commands.Count;
await engine.FinishBatchAsync("00");
Check(!engine.IsBatchActive && registry.ActiveCount == 0 && controller.Commands.Skip(beforeStop).Order().SequenceEqual(new[] {"D01", "D02"}), "Global stop must target each configured block");
Console.WriteLine("PASS: concurrent callers serialize writes and explicit global stop clears both blocks");

controller.FailBlock = "01";
try {await engine.StartBatchAsync(new[] {Light("01", "UNCERTAIN", "0011")}); throw new Exception("Hardware rejection expected");} catch (AioiNackException) { }
await Reject(() => engine.StartBatchAsync(new[] {Light("01", "REPLACEMENT", "0012")}));
await engine.StartBatchAsync(new[] {Light("02", "HEALTHY", "0031")});
Check(registry.Get("0031")!.CorrelationId == "HEALTHY", "Failure in A blocked B");
controller.FailBlock = null;
await engine.FinishBatchAsync("00");
Console.WriteLine("PASS: uncertain dispatch keeps its block reserved without blocking the other block");
registry.SetInstructed(Light("01", "RESTORED", "0090"));
await Reject(() => engine.StartBatchAsync(new[] {Light("01", "NEW", "0091")}));
await engine.FinishBatchAsync("01");
Console.WriteLine("PASS: restored operating lights cannot be overwritten by a new session");

await engine.StartBatchAsync(new[] {Light("02", "WMS-SESSION", "0040")});
var csvPath = Path.Combine(directory, "tasks.csv");
await File.WriteAllTextAsync(csvPath, "Address,Quantity\n0050,2\n");
await using (var csv = new CsvPickTaskRunner(engine, new PreemptionGate(), NullLogger<CsvPickTaskRunner>.Instance, "01"))
{
    var csvDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    csv.TaskEvent += e => {if (e.EventType == "csv_completed") csvDone.TrySetResult();};
    csv.Start(csvPath, LightColor.Green);
    await Until(() => registry.Get("0050")?.Status == ModuleStatus.Operating);
    await controller.Confirm("0050");
    await csvDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(registry.Get("0040")!.Status == ModuleStatus.Operating && controller.Commands.Last() == "D01", "CSV cleanup cancelled the WMS session in another block");
}
await engine.FinishBatchAsync("00");
Console.WriteLine("PASS: CSV completion cleans only its configured block while the WMS session keeps running");
var dispatcher = typeof(MqttBridgeService).GetField("_dispatcher", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mqtt)!;
async Task Dispatch(string json) => await (Task)dispatcher.GetType().GetMethod("HandleAsync")!.Invoke(dispatcher, new object[] {Encoding.UTF8.GetBytes(json), CancellationToken.None})!;
await Dispatch("""{"type":"batch_start","blockNo":"01","correlationId":"JSON-A","items":[{"address":"0060","quantity":2}]}""");
await Dispatch("""{"type":"batch_start","blockNo":"02","correlationId":"JSON-B","items":[{"address":"0070","quantity":3}]}""");
await Dispatch("""{"type":"batch_finish","blockNo":"01","correlationId":"JSON-A"}""");
Check(registry.Get("0060")!.Status == ModuleStatus.Standby && registry.Get("0070")!.Status == ModuleStatus.Operating, "JSON dispatch did not isolate blocks");
await controller.Confirm("0070");
await Until(() => telemetry.Entries.Any(e => e.Contains("JSON-B") && e.Contains("confirmed")));
var jsonEvent = telemetry.Entries.Select(s => JsonDocument.Parse(s).RootElement).First(e => e.TryGetProperty("type",out var t) && t.GetString() == "confirmed" && e.GetProperty("correlationId").GetString() == "JSON-B");
Check(jsonEvent.GetProperty("blockNo").GetString() == "02", "Confirmed MQTT event lost block/session scope");
Console.WriteLine("PASS: Rocket-compatible MQTT JSON dispatch to one TCP controller and scoped confirmation events");
await engine.FinishBatchAsync("00");
await engine.StartBatchAsync(new[] {Light("01", "INTERLEAVED-A", "0008", 3), Light("01", "INTERLEAVED-A", "0014", 2)});
await engine.StartBatchAsync(new[] {Light("02", "INTERLEAVED-B", "0017", 2)});
await controller.Confirm("0014");
await Until(() => registry.Get("0014")!.Status == ModuleStatus.Completed);
await controller.Confirm("0017");
await Until(() => registry.Get("0017")!.Status == ModuleStatus.Completed);
await engine.StartBatchAsync(new[] {Light("02", "INTERLEAVED-C", "0017", 3)});
Check(registry.Get("0008")!.Status == ModuleStatus.Operating && registry.Get("0008")!.CorrelationId == "INTERLEAVED-A", "Next session on block 02 lost block 01 ownership");
await controller.Confirm("0008");
await Until(() => registry.Get("0008")!.Status == ModuleStatus.Completed);
Check(registry.Get("0017")!.Status == ModuleStatus.Operating && registry.Get("0017")!.CorrelationId == "INTERLEAVED-C", "Last Confirm on block 01 affected the next session on block 02");
Console.WriteLine("PASS: 0014 Confirm, 0017 Confirm, next session on block 02, then 0008 Confirm retains both block identities");
await engine.FinishBatchAsync("00");
Console.WriteLine("All multiblock checks passed. One loopback controller, no real hardware/broker/license API.");
await gateway.DisposeAsync(); await mqtt.DisposeAsync();
Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);

sealed class Capture : IP2LTelemetry
{
    public ConcurrentQueue<string> Entries = new();
    public void Record(string kind, string? topic, string payload) {if (kind == "event") Entries.Enqueue(payload);}
}

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
