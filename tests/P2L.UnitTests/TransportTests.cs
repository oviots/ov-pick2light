using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Transport;

namespace P2L.UnitTests;

public sealed class TransportTests
{
    private static MqttSettings Settings(int failback = 10000) => new()
    {
        ProbeIntervalMs = 50, ProbeTimeoutMs = 150, FailbackStableMs = failback,
    };
    private static byte[] Event(string id) => Encoding.UTF8.GetBytes($"{{\"type\":\"confirmed\",\"eventId\":\"{id}\",\"commandItemId\":\"detail:1\"}}");

    [Fact]
    public async Task WarmLinksPreferPrimaryAndBusinessWaitsForApplicationAck()
    {
        var primary = new FakeChannel("primary") { AckEvents = false };
        var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync();
        await Until(() => coordinator.IsConnected);
        var send = coordinator.SendAsync("p2l/events", Event("one"), false, CancellationToken.None);
        await Until(() => primary.Events.Count == 1);
        Assert.False(send.IsCompleted);
        await primary.Receive("{\"type\":\"event_ack\",\"eventId\":\"one\"}");
        await send;
        Assert.Empty(backup.Events);
        Assert.Equal("primary", coordinator.ActiveChannel);
        Assert.True(primary.Started && backup.Started);
    }

    [Fact]
    public async Task PrimaryBrokerReceiptWithoutIngressAckUsesSameEventOnBackup()
    {
        var primary = new FakeChannel("primary") { AckEvents = false };
        var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await coordinator.SendAsync("p2l/events", Event("preserved"), false, CancellationToken.None);
        Assert.Equal(primary.Events.Single(), backup.Events.Single());
        Assert.Equal("backup", coordinator.ActiveChannel);
    }

    [Fact]
    public async Task KnownDisconnectImmediatelyUsesWarmBackup()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await coordinator.SendAsync("p2l/events", Event("first"), false, CancellationToken.None);
        primary.Disconnect();
        await coordinator.SendAsync("p2l/events", Event("second"), false, CancellationToken.None);
        Assert.Single(primary.Events); Assert.Single(backup.Events);
        Assert.Equal("backup", coordinator.ActiveChannel);
    }

    [Fact]
    public async Task SilentPrimaryFaultIsDetectedByApplicationProbeEvenIfBrokerConnected()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await coordinator.SendAsync("p2l/events", Event("first"), false, CancellationToken.None);
        primary.Pong = false;
        await Task.Delay(350);
        await coordinator.SendAsync("p2l/events", Event("second"), false, CancellationToken.None);
        Assert.True(primary.IsConnected);
        Assert.Single(primary.Events); Assert.Single(backup.Events);
    }

    [Fact]
    public async Task BothMqttFailuresLeaveBusinessEventUnacknowledgedWithSameIdentity()
    {
        var primary = new FakeChannel("primary") { FailEvents = true };
        var backup = new FakeChannel("backup") { FailEvents = true };
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await Assert.ThrowsAsync<IOException>(() =>
            coordinator.SendAsync("p2l/events", Event("same"), false, CancellationToken.None));
        Assert.Equal(primary.Events.Single(), backup.Events.Single());
        Assert.Contains("\"eventId\":\"same\"", backup.Events.Single());
    }

    [Fact]
    public async Task ControlMessagesNeverInvokeHardwareDispatcherAndRawCommandIsPreserved()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        var received = new List<byte[]>();
        coordinator.CommandReceived += packet => { received.Add(packet.Payload); return Task.CompletedTask; };
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await primary.Receive("{\"type\":\"transport_ping\",\"probeId\":\"server\"}");
        await primary.Receive("{\"type\":\"event_ack\",\"eventId\":\"unknown\"}");
        Assert.Empty(received);
        const string raw = "{ \"type\":\"light_on\", \"commandId\":\"unchanged\",\"quantity\":2 }";
        await primary.Receive(raw); await backup.Receive(raw);
        Assert.Equal(2, received.Count);
        Assert.All(received, bytes => Assert.Equal(raw, Encoding.UTF8.GetString(bytes)));
    }

    [Fact]
    public async Task MalformedPrimitiveAndUnknownCommandsReachExistingDispatcherUnchanged()
    {
        var primary = new FakeChannel("primary");
        await using var coordinator = new BridgeTransportCoordinator([primary], Settings(), NullLogger.Instance);
        var received = new List<string>();
        coordinator.CommandReceived += message => { received.Add(Encoding.UTF8.GetString(message.Payload)); return Task.CompletedTask; };
        await coordinator.StartAsync();
        string[] payloads = ["{", "[]", "123", "null", "{\"type\":7}", "{\"type\":\"unknown\"}"];
        foreach (var payload in payloads) await primary.Receive(payload);
        Assert.Equal(payloads, received);
        Assert.True(coordinator.IsConnected);
    }

    [Fact]
    public async Task MalformedRecognizedControlIsIgnoredWithoutHardwareDispatchOrChannelFault()
    {
        var primary = new FakeChannel("primary");
        await using var coordinator = new BridgeTransportCoordinator([primary], Settings(), NullLogger.Instance);
        var calls = 0;
        coordinator.CommandReceived += _ => { calls++; return Task.CompletedTask; };
        await coordinator.StartAsync();
        foreach (var payload in new[] { "{\"type\":\"transport_ping\"}", "{\"type\":\"transport_pong\",\"probeId\":42}", "{\"type\":\"event_ack\",\"eventId\":{}}" })
            await primary.Receive(payload);
        Assert.Equal(0, calls);
        Assert.True(coordinator.IsConnected);
    }

    [Fact]
    public async Task AllOfflineNeverAcknowledgesBusinessEvent()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        primary.Disconnect(); backup.Disconnect();
        await Assert.ThrowsAsync<IOException>(() => coordinator.SendAsync("p2l/events", Event("pending"), false, CancellationToken.None));
    }

    [Fact]
    public async Task RecoveryNeedsStableWindowBeforeFailback()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(400), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        primary.FailEvents = true;
        await coordinator.SendAsync("p2l/events", Event("fallback"), false, CancellationToken.None);
        primary.FailEvents = false;
        await Task.Delay(200);
        await coordinator.SendAsync("p2l/events", Event("sticky"), false, CancellationToken.None);
        Assert.Equal("backup", coordinator.ActiveChannel);
        await Task.Delay(500);
        await coordinator.SendAsync("p2l/events", Event("recovered"), false, CancellationToken.None);
        Assert.Equal("primary", coordinator.ActiveChannel);
    }

    [Fact]
    public async Task LegacySingleMqttModeDoesNotRequireNewApplicationAckProtocol()
    {
        var primary = new FakeChannel("primary") { Pong = false, AckEvents = false };
        await using var coordinator = new BridgeTransportCoordinator([primary], Settings(), NullLogger.Instance);
        await coordinator.StartAsync();
        await coordinator.SendAsync("p2l/events", Encoding.UTF8.GetBytes("{\"type\":\"confirmed\"}"), false, CancellationToken.None);
        Assert.False(coordinator.RequiresApplicationAck);
    }

    [Fact]
    public void EventIdIsPersistedOnceAndLegacyIdentityIsStable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "p2l-transport-id-" + Guid.NewGuid().ToString("N"));
        try
        {
            var buffer = new DurableEventBuffer(Path.Combine(directory, "outbox.jsonl"));
            buffer.Append("p2l/events", false, Encoding.UTF8.GetBytes("{\"type\":\"confirmed\"}"));
            var persisted = DurableEventBuffer.Parse(buffer.ReadBatch().Single());
            Assert.Equal(EventIdentity.FromBuffered(persisted), EventIdentity.FromBuffered(persisted));
            using var doc = JsonDocument.Parse(Convert.FromBase64String(persisted.Payload));
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("eventId").GetString()));
            var legacy = new BufferedEntry("p2l/events", false, Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"type\":\"confirmed\",\"timestamp\":\"past\"}")));
            Assert.Equal(EventIdentity.FromBuffered(legacy), EventIdentity.FromBuffered(legacy));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task FullCommandQueueDoesNotBlockControlRoundTripsOrBusinessAcks()
    {
        var primary = new FakeChannel("primary"); var backup = new FakeChannel("backup");
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CommandAdmissionQueue(1, async (_, _) => { entered.TrySetResult(); await release.Task; }, CancellationToken.None, _ => { });
        coordinator.CommandReceived += message => { queue.TryEnqueue(message.Payload); return Task.CompletedTask; };
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await primary.Receive("{\"type\":\"get_snapshot\",\"commandId\":\"one\"}");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await primary.Receive("{\"type\":\"transport_ping\",\"probeId\":\"still-responsive\"}").WaitAsync(TimeSpan.FromSeconds(1));
        await coordinator.SendAsync("p2l/events", Event("while-command-blocked"), false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, queue.Count);
        release.SetResult(); await queue.StopAsync();
    }

    [Fact]
    public async Task RetainedTopicRejectionDoesNotFenceHealthyBusinessChannels()
    {
        var primary = new FakeChannel("primary") { FailRetained = true };
        var backup = new FakeChannel("backup") { FailRetained = true };
        await using var coordinator = new BridgeTransportCoordinator([primary, backup], Settings(), NullLogger.Instance);
        await coordinator.StartAsync(); await Until(() => coordinator.IsConnected);
        await Assert.ThrowsAsync<IOException>(() => coordinator.SendAsync("p2l/status", Encoding.UTF8.GetBytes("{\"type\":\"bridge_status\"}"), true, CancellationToken.None));
        Assert.True(coordinator.IsConnected);
        await coordinator.SendAsync("p2l/events", Event("business-after-denied-optional"), false, CancellationToken.None);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) { ct.Token.ThrowIfCancellationRequested(); await Task.Delay(5, ct.Token); }
        await Task.Delay(10, ct.Token);
    }

    private sealed class FakeChannel(string name) : IBridgeChannel
    {
        public string Name => name;
        public bool IsConnected { get; private set; }
        public bool Started;
        public bool Pong = true;
        public bool AckEvents = true;
        public bool FailEvents;
        public bool FailRetained;
        public List<string> Events { get; } = [];
        public event Func<ChannelMessage, Task>? MessageReceived;
        public event Action<bool>? ConnectionChanged;
        public Task StartAsync(CancellationToken ct = default) { Started = true; IsConnected = true; ConnectionChanged?.Invoke(true); return Task.CompletedTask; }
        public void Disconnect() { IsConnected = false; ConnectionChanged?.Invoke(false); }
        public Task Receive(string json) => MessageReceived?.Invoke(new("p2l/commands", Encoding.UTF8.GetBytes(json))) ?? Task.CompletedTask;
        public async Task SendAsync(string topic, byte[] payload, bool retain, CancellationToken ct = default)
        {
            if (!IsConnected) throw new IOException("offline");
            using var doc = JsonDocument.Parse(payload); var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            if (type == "transport_ping") { if (Pong) await Receive($"{{\"type\":\"transport_pong\",\"probeId\":\"{root.GetProperty("probeId").GetString()}\"}}"); return; }
            if (type == "transport_pong") return;
            Events.Add(Encoding.UTF8.GetString(payload));
            if (FailEvents || (retain && FailRetained)) throw new IOException("send failed");
            if (AckEvents && !retain && root.TryGetProperty("eventId", out var id))
                await Receive($"{{\"type\":\"event_ack\",\"eventId\":\"{id.GetString()}\"}}");
        }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
