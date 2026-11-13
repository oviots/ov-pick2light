using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Shared;
using P2L.Shared.Models;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Until(Func<bool> test) { using var timeout = new CancellationTokenSource(20000); while (!test()) await Task.Delay(20, timeout.Token); }
static void Blocked(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Admission should have been blocked"); }
var dir = Path.Combine(Path.GetTempPath(), "p2l-confirm-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
var path = Path.Combine(dir, "confirms.db");
var outbox = new ConfirmationOutbox(path, "TEST-SCOPE");
var state = new LightModuleState { Address = "0001", BlockNo = "01", CorrelationId = "SESSION-A", CommandItemId = "ITEM-A", PickingDetailId = "123456789012345678901234" };
var physical = new ConfirmationEvent { Address = "0001", AdjustedDisplay = "00002" };
outbox.Record(physical, state); outbox.Record(physical, state);
var saved = outbox.ReadPending().Single();
var restored = new ConfirmationOutbox(path, "TEST-SCOPE");
Check(restored.ReadPending().Single() == saved, "Reopening changed or lost the Confirm");
Blocked(() => new ConfirmationOutbox(path, "OTHER-ENDPOINT"));
Check(!restored.Acknowledge(state.AssignmentId,"02","SESSION-A","0001","applied"), "Wrong block ACK erased receipt");
Check(!restored.Acknowledge(state.AssignmentId,"01","OTHER","0001","applied"), "Wrong session ACK erased receipt");
Check(restored.Acknowledge(state.AssignmentId,"01","SESSION-A","0001","reconcile_required"), "Missing reconcile receipt");
Check(restored.ReadPending().Count == 1, "Reconcile erased the receipt");
Check(restored.Acknowledge(state.AssignmentId,"01","SESSION-A","0001","applied"), "Applied ACK did not erase receipt");
Console.WriteLine("PASS: FULL SQLite receipt survives reopen; duplicate, endpoint binding, scoped ACK checks");

await using var controller = new FakeController();
await using var gateway = new AioiGateway("127.0.0.1", controller.Port, NullLogger.Instance);
var registry = new LightModuleRegistry();
var journal = new FailingJournal(outbox);
using var engine = new PickingEngine(gateway, registry, NullLogger<PickingEngine>.Instance, new BlockPolicy(new[] {"01", "02"}), journal);
await gateway.StartAsync();
await engine.QuickLightAsync("0001", 2, blockNo:"01", correlationId:"SESSION-A", commandItemId:"ITEM-A", pickingDetailId:state.PickingDetailId);
journal.Fail = true;
await controller.Confirm("0001"); await Until(() => journal.Attempts == 1); await Task.Delay(100);
Check(controller.Acks == 0, "Controller ACK sent before disk commit");
Check(registry.Get("0001")!.Status == P2L.Shared.Enums.ModuleStatus.Operating, "Failed write cleared operating state");
journal.Fail = false;
await controller.Confirm("0001"); await Until(() => controller.Acks == 1);
Check(outbox.ReadPending().Count == 1, "Controller Confirm not durably stored");
var receipt = outbox.ReadPending().Single();
await engine.QuickLightAsync("0002", 1, blockNo:"01", correlationId:"NEXT-WHILE-ACK-PENDING", commandItemId:"ITEM-A2", pickingDetailId:"223456789012345678901234");
Check(outbox.ReadPending().Single()==receipt, "A new session changed the earlier receipt");
await engine.FinishBatchAsync("01", correlationId:"NEXT-WHILE-ACK-PENDING");
var mixedBatch = new[] {
    new PickingInstruction {Address="0001",Quantity=2,BlockNo="01",CorrelationId="BATCH-WITH-REPLAY",PickingDetailId=state.PickingDetailId},
    new PickingInstruction {Address="0002",Quantity=1,BlockNo="01",CorrelationId="BATCH-WITH-REPLAY",PickingDetailId="223456789012345678901234"},
};
await engine.StartBatchAsync(mixedBatch);
var mixedSent = controller.Commands.Count;
await engine.StartBatchAsync(mixedBatch);
Check(controller.Commands.Count==mixedSent,"Retry of a filtered mixed batch reached hardware twice");
Check(registry.Get("0001")!.Status==P2L.Shared.Enums.ModuleStatus.Standby && registry.Get("0002")!.Status==P2L.Shared.Enums.ModuleStatus.Operating,
    "A mixed batch relit its already-confirmed item or skipped its new item");
await engine.FinishBatchAsync("01",correlationId:"BATCH-WITH-REPLAY");
Console.WriteLine("PASS: pending ACK does not own the block; next WMS session proceeds while old receipt remains durable");
var count = controller.Commands.Count;
await engine.QuickLightAsync("0001", 2, blockNo:"01", correlationId:"SESSION-A", commandItemId:"ITEM-A", pickingDetailId:state.PickingDetailId);
Check(controller.Commands.Count == count, "MQTT command retry relit confirmed item");
await engine.QuickLightAsync("0021", 3, blockNo:"02", correlationId:"SESSION-B");
Console.WriteLine("PASS: failed disk write withholds hardware ACK and keeps Operating; retry commits, healthy block continues");

var portProbe = new TcpListener(IPAddress.Loopback, 0); portProbe.Start(); var port = ((IPEndPoint)portProbe.LocalEndpoint).Port; portProbe.Stop();
var settings = new MqttSettings { Host="127.0.0.1", Port=port, Tls=new() {Enabled=false}, OfflineBufferPath=Path.Combine(dir,"offline.jsonl") };
var labels = new DeviceLabelStore(Path.Combine(dir,"devices.db"), NullLogger<DeviceLabelStore>.Instance);
var bridge = new MqttBridgeService(settings, gateway, engine, registry, new PreemptionGate(), null, labels,
    new DeviceScanService(gateway, registry, NullLogger<DeviceScanService>.Instance), NullLoggerFactory.Instance, confirmations:outbox);
await bridge.StartAsync(); await Task.Delay(100);
Check(outbox.ReadPending().Count == 1, "Offline start erased receipt");
using var broker = Process.Start(new ProcessStartInfo("/usr/local/sbin/mosquitto", $"-p {port}") { UseShellExecute=false, RedirectStandardError=true, RedirectStandardOutput=true })!;
_ = broker.StandardError.ReadToEndAsync();
using var wms = new MqttClientFactory().CreateMqttClient();
var received = new ConcurrentQueue<string>();
var manualReceived = new ConcurrentQueue<string>();
wms.ApplicationMessageReceivedAsync += args => { var payload=Encoding.UTF8.GetString(args.ApplicationMessage.Payload.ToArray()); if (payload.Contains("confirmProtocol")) received.Enqueue(payload); else if (payload.Contains("confirmed")) manualReceived.Enqueue(payload); return Task.CompletedTask; };
var options = new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1",port).WithClientId("mock-wms").Build();
try {
    await Until(() => { try { wms.ConnectAsync(options).GetAwaiter().GetResult(); return true; } catch { return false; } });
    await wms.SubscribeAsync(new MqttClientSubscribeOptionsBuilder().WithTopicFilter(f=>f.WithTopic(settings.EventTopic).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)).Build());
    await Until(() => received.Count >= 1);
    Check(outbox.ReadPending().Count == 1, "Broker PUBACK erased Confirm before WMS stored it");
    await Until(() => received.Count >= 2);
    Check(received.All(p=>p == receipt), "Retry changed event identity or payload");
    var evt = JsonDocument.Parse(receipt).RootElement;
    async Task Ack(string outcome, bool retain=false, string? correlation=null) {
        var payload = JsonSerializer.Serialize(new {type="confirm_ack",eventId=evt.GetProperty("eventId").GetString(),blockNo="01",correlationId=correlation??"SESSION-A",address="0001",outcome});
        await wms.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(settings.CommandTopic).WithPayload(payload).WithRetainFlag(retain).WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build());
        await Task.Delay(150);
    }
    // MQTT clears RETAIN for live delivery. Test an actual retained replay at SUBSCRIBE.
    await bridge.DisposeAsync();
    await Ack("stored",retain:true);
    bridge = new MqttBridgeService(settings, gateway, engine, registry, new PreemptionGate(), null, labels,
        new DeviceScanService(gateway, registry, NullLogger<DeviceScanService>.Instance), NullLoggerFactory.Instance, confirmations:outbox);
    await bridge.StartAsync(); await Until(()=>bridge.IsConnected); await Task.Delay(200);
    Check(outbox.ReadPending().Count == 1, "Retained replay erased Confirm");
    await Ack("stored",correlation:"WRONG"); Check(outbox.ReadPending().Count == 1, "Wrong session erased Confirm over MQTT");
    await Ack("reconcile_required"); Check(outbox.ReadPending().Count == 1, "Reconcile ACK erased Confirm over MQTT");
    await engine.QuickLightAsync("0002", 1, blockNo:"01", correlationId:"WMS-AUTHORIZED-NEXT");
    await engine.FinishBatchAsync("01", correlationId:"WMS-AUTHORIZED-NEXT");
    Check(outbox.ReadPending().Single()==receipt, "Reconciliation of old work was lost when the block continued");
    await Ack("stored"); await Until(()=>outbox.ReadPending().Count==0);
    await bridge.HandleLocalCommandAsync(Encoding.UTF8.GetBytes("""
        {"type":"batch_start","blockNo":"01","correlationId":"NEXT","items":[{"address":"0002","quantity":1,"commandItemId":"ITEM-NEXT","pickingDetailId":"223456789012345678901234"}]}
        """));
    var deliveryTime = Stopwatch.StartNew();
    await controller.Confirm("0002"); await Until(()=>outbox.ReadPending().Count==1);
    await Until(()=>received.Any(p=>p.Contains("ITEM-NEXT")));
    Check(deliveryTime.Elapsed < TimeSpan.FromMilliseconds(750), $"New Confirm waited on a polling timer: {deliveryTime.ElapsedMilliseconds}ms");
    Console.WriteLine($"PASS: new Confirm wakes delivery immediately ({deliveryTime.ElapsedMilliseconds}ms, local loopback)");
    var next = JsonDocument.Parse(outbox.ReadPending().Single()).RootElement;
    Check(next.GetProperty("commandItemId").GetString()=="ITEM-NEXT" && next.GetProperty("pickingDetailId").GetString()=="223456789012345678901234", "WMS item IDs were lost between JSON command and durable receipt");
    await controller.Confirm("0021"); await Until(()=>manualReceived.Count>0);
    Check(outbox.ReadPending().Count==1, "Manual command without WMS detail became stuck waiting for business ACK");
    Console.WriteLine("PASS: real loopback broker reconnect, repeated identical Confirm after lost business ACK, retained/wrong ACK rejected, stored ACK removes only its accepted receipt");
    Console.WriteLine("PASS: WMS IDs survive JSON dispatch; manual commands retain legacy confirmation flow");
    for (var i=1;i<=201;i++) outbox.Record(new ConfirmationEvent {Address="0050"},
        new LightModuleState {Address="0050",BlockNo="02",CorrelationId="BACKLOG",PickingDetailId=i.ToString("x24")});
    await Until(()=>received.Where(p=>p.Contains("\"correlationId\":\"BACKLOG\""))
        .Select(p=>JsonDocument.Parse(p).RootElement.GetProperty("eventId").GetString()).Distinct().Count()==201);
    Check(outbox.ReadPending().Count==202,"Backlog delivery dropped a receipt without business ACK");
    Console.WriteLine("PASS: all 201 backlog receipts cross batch boundaries and remain durable until business ACK");
} finally {
    await bridge.DisposeAsync();
    if (wms.IsConnected) await wms.DisconnectAsync();
    if (!broker.HasExited) {broker.Kill(); await broker.WaitForExitAsync();}
    await gateway.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(dir, true);
}

sealed class FailingJournal(ConfirmationOutbox inner) : IConfirmationJournal {
    public bool Fail;
    public int Attempts;
    public bool HasRecorded(string pickingDetailId) => inner.HasRecorded(pickingDetailId);
    public void Record(ConfirmationEvent evt, LightModuleState state) { Attempts++; if(Fail) throw new IOException("Simulated disk unavailable"); inner.Record(evt,state); }
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
    public int Acks;
    public bool NackNextP1;
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
                if (packet.Command == "O") { Interlocked.Increment(ref Acks); continue; } // ack for controller interrupt
                Commands.Enqueue(packet.Command);
                var nack = (NackNextP1 && packet.Command.StartsWith("P1"));
                if (nack) NackNextP1 = false;
                await Send(new AioiPacket {SequenceNo = packet.SequenceNo, Command = nack ? "n" : "o"});
            }
        }
        catch (Exception e) when (e is OperationCanceledException or EndOfStreamException or IOException or SocketException) { }
    }
    public async ValueTask DisposeAsync() {await _stop.CancelAsync(); _client?.Dispose(); _listener.Stop(); await _loop; _stop.Dispose();}
}
