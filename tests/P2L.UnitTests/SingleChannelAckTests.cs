using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Mqtt.Transport;

namespace P2L.UnitTests;

public sealed class SingleChannelAckTests
{
    private static MqttSettings Settings(string? path = null, bool require = true) => new()
    {
        RequireApplicationAck = require, OfflineBufferPath = path ?? "unused.jsonl",
        ProbeIntervalMs = 50, ProbeTimeoutMs = 300, FailbackStableMs = 0,
    };
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(3000);
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    [Fact]
    public async Task SingleWmsChannelRequiresPeerHealthAndMatchingNonretainedApplicationAck()
    {
        var channel = new FakeChannel { Pong = false };
        await using var transport = new BridgeTransportCoordinator([channel], Settings(), NullLogger.Instance);
        await transport.StartAsync();
        Assert.True(transport.RequiresApplicationAck);
        Assert.True(channel.IsConnected);
        Assert.False(transport.IsConnected); // Broker subscription is not WMS ingress health.
        channel.Pong = true;
        await Until(() => transport.IsConnected);
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"confirmed\",\"eventId\":\"exact-event\"}");
        var send = transport.SendAsync("p2l/events", payload, false, CancellationToken.None);
        await Until(() => channel.Events.Count == 1);
        await channel.Receive("{\"type\":\"event_ack\",\"eventId\":\"wrong-event\"}");
        await channel.Receive("{\"type\":\"event_ack\",\"eventId\":\"exact-event\"}", retain: true);
        Assert.False(send.IsCompleted);
        await channel.Receive("{\"type\":\"event_ack\",\"eventId\":\"exact-event\"}");
        await send.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SingleBrokerPubackWithoutWmsAckSurvivesPublisherReopenWithSameEventIdentity()
    {
        await using var fixture = new Fixture();
        var publisher = fixture.CreatePublisher();
        publisher.EnqueueCommandResult(new CommandResultEvt { Type = "command_result", CommandId = "one", Status = "applied" });
        var original = Assert.Single(fixture.Rows());
        Assert.False(await publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(original, Assert.Single(fixture.Rows()));
        var delivered = Assert.Single(fixture.Channel.Events);
        await publisher.DisposeAsync();
        fixture.Channel.AckEvents = true;
        await Until(() => fixture.Transport.IsConnected);
        await using var reopened = fixture.CreatePublisher();
        Assert.True(await reopened.ReplayBufferAsync(CancellationToken.None));
        Assert.Empty(fixture.Rows());
        Assert.Equal(delivered, fixture.Channel.Events.Last());
        using var json = JsonDocument.Parse(delivered);
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("eventId").GetString()));
    }

    [Fact]
    public async Task SingleChannelOnlyRemovesDurablyAcknowledgedPrefix()
    {
        await using var fixture = new Fixture();
        await using var publisher = fixture.CreatePublisher();
        publisher.EnqueueCommandResult(new CommandResultEvt { Type = "command_result", CommandId = "one", Status = "applied" });
        publisher.EnqueueCommandResult(new CommandResultEvt { Type = "command_result", CommandId = "two", Status = "applied" });
        fixture.Channel.AckCommandId = "one";
        Assert.False(await publisher.ReplayBufferAsync(CancellationToken.None));
        using (var tail = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(Assert.Single(fixture.Rows())).Payload)))
            Assert.Equal("two", tail.RootElement.GetProperty("commandId").GetString());
        fixture.Channel.AckEvents = true;
        await Until(() => fixture.Transport.IsConnected);
        Assert.True(await publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Empty(fixture.Rows());
    }

    [Fact]
    public async Task DefaultStandaloneClientStillPublishesWithoutNewProbeOrEventAckProtocol()
    {
        Assert.False(new MqttSettings().RequireApplicationAck);
        var channel = new FakeChannel { Pong = false, AckEvents = false };
        await using var transport = new BridgeTransportCoordinator([channel], Settings(require: false), NullLogger.Instance);
        await transport.StartAsync();
        Assert.True(transport.IsConnected);
        Assert.False(transport.RequiresApplicationAck);
        await transport.SendAsync("p2l/events", Encoding.UTF8.GetBytes("{\"type\":\"confirmed\"}"), false, CancellationToken.None);
        Assert.Single(channel.Events);
        Assert.Equal(0, channel.Probes);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "p2l-single-ack-" + Guid.NewGuid().ToString("N"));
        private readonly AioiGateway _gateway = new("never-start.invalid", 1, NullLogger.Instance);
        private readonly LightModuleRegistry _registry = new();
        private readonly PickingEngine _engine;
        private readonly MqttSettings _settings;
        public readonly FakeChannel Channel = new();
        public readonly BridgeTransportCoordinator Transport;
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            _settings = Settings(Path.Combine(_directory, "outbox.jsonl"));
            _engine = new PickingEngine(_gateway, _registry, NullLogger<PickingEngine>.Instance);
            Transport = new BridgeTransportCoordinator([Channel], _settings, NullLogger.Instance);
            Transport.StartAsync().GetAwaiter().GetResult();
            Until(() => Transport.IsConnected).GetAwaiter().GetResult();
        }
        public MqttEventPublisher CreatePublisher() => new(Transport, _settings, _engine, _registry,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance), NullLogger<MqttEventPublisher>.Instance);
        public string[] Rows() => new DurableEventBuffer(_settings.OfflineBufferPath).ReadBatch();
        public async ValueTask DisposeAsync()
        {
            await Transport.DisposeAsync(); _engine.Dispose(); await _gateway.DisposeAsync();
            Directory.Delete(_directory, true);
        }
    }

    private sealed class FakeChannel : IBridgeChannel
    {
        public string Name => "primary";
        public bool IsConnected { get; private set; }
        public bool Pong = true;
        public bool AckEvents;
        public string? AckCommandId;
        public int Probes;
        public readonly ConcurrentQueue<string> Events = new();
        public event Func<ChannelMessage, Task>? MessageReceived;
        public event Action<bool>? ConnectionChanged;
        public Task StartAsync(CancellationToken ct = default) { IsConnected = true; ConnectionChanged?.Invoke(true); return Task.CompletedTask; }
        public Task Receive(string json, bool retain = false) => MessageReceived?.Invoke(new("p2l/commands", Encoding.UTF8.GetBytes(json), retain)) ?? Task.CompletedTask;
        public async Task SendAsync(string topic, byte[] payload, bool retain, CancellationToken ct = default)
        {
            using var document = JsonDocument.Parse(payload);
            var json = document.RootElement;
            if (json.GetProperty("type").GetString() == "transport_ping")
            {
                Interlocked.Increment(ref Probes);
                if (Pong) await Receive($"{{\"type\":\"transport_pong\",\"probeId\":\"{json.GetProperty("probeId").GetString()}\"}}");
                return;
            }
            Events.Enqueue(Encoding.UTF8.GetString(payload)); // Successful broker receipt alone.
            bool matches = json.TryGetProperty("commandId", out var command) && command.GetString() == AckCommandId;
            if (!retain && (AckEvents || matches) && json.TryGetProperty("eventId", out var id))
                await Receive($"{{\"type\":\"event_ack\",\"eventId\":\"{id.GetString()}\"}}");
        }
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }
}
