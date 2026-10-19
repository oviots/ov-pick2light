using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Formatter;
using P2L.Mqtt;
using P2L.Core;
using P2L.Hardware;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Models;

namespace P2L.UnitTests;

public sealed class MqttTests
{
    [Fact]
    public void RestoredItemConfirmationBeforeStartIsDurableWithExactIdentity()
    {
        using var f = new Fixture();
        f.Registry.SetInstructed(new PickingInstruction
        {
            Address = "0001", Quantity = 2, BlockNo = "01", CorrelationId = "restored-session",
            CommandItemId = "restored-item", PickingDetailId = "restored-detail",
        });
        var handlers = (Action<ConfirmationEvent>?)typeof(AioiGateway)
            .GetField("Confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Gateway);
        handlers!.Invoke(new ConfirmationEvent { Address = "0001" });
        var entry = DurableEventBuffer.Parse(new DurableEventBuffer(f.Path).ReadBatch().Single());
        using var payload = JsonDocument.Parse(Convert.FromBase64String(entry.Payload));
        Assert.Equal("confirmed", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("restored-session", payload.RootElement.GetProperty("correlationId").GetString());
        Assert.Equal("restored-item", payload.RootElement.GetProperty("commandItemId").GetString());
        Assert.Equal("restored-detail", payload.RootElement.GetProperty("pickingDetailId").GetString());
        Assert.Equal(P2L.Shared.Enums.ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public void CommandResultIsDurableBeforePublisherStarts()
    {
        using var f = new Fixture();
        f.Enqueue("one");
        Assert.Equal("one", f.Ids().Single());
    }

    [Fact]
    public async Task TransientReplayExceptionPreservesEveryValidEntry()
    {
        using var f = new Fixture();
        f.Enqueue("one"); f.Enqueue("two");
        f.Client.Handler = (_, _) => throw new IOException("connection lost");
        Assert.False(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(1, f.Client.Attempts);
        Assert.Equal(new[] { "one", "two" }, f.Ids());
        Assert.False(File.Exists(f.Path + ".invalid"));
    }

    [Fact]
    public async Task RejectedPubackPreservesEntryForRetry()
    {
        using var f = new Fixture();
        f.Enqueue("rejected");
        f.Client.Handler = (_, _) => Task.FromResult(new MqttClientPublishResult(
            1, MqttClientPublishReasonCode.NotAuthorized, "denied", []));
        Assert.False(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal("rejected", f.Ids().Single());
        f.Client.Handler = Success;
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Empty(f.Ids());
    }

    [Fact]
    public async Task MissingSubscriberKeepsBusinessEventForDeliveryAfterReconnect()
    {
        using var f = new Fixture();
        f.Enqueue("no-subscriber");
        f.Client.Handler = (_, _) => Task.FromResult(new MqttClientPublishResult(
            1, MqttClientPublishReasonCode.NoMatchingSubscribers, null, []));
        Assert.False(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal("no-subscriber", f.Ids().Single());
    }

    [Fact]
    public async Task CancellationDuringPublishPreservesInflightAndQueuedEntries()
    {
        using var f = new Fixture();
        f.Enqueue("inflight"); f.Enqueue("waiting");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Handler = async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };
        using var cts = new CancellationTokenSource();
        var replay = f.Publisher.ReplayBufferAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        Assert.False(await replay);
        Assert.Equal(new[] { "inflight", "waiting" }, f.Ids());
    }

    [Fact]
    public async Task PartialReplayKeepsTailAndConcurrentAppends()
    {
        using var f = new Fixture();
        f.Enqueue("one"); f.Enqueue("two");
        f.Client.Handler = (message, ct) =>
        {
            if (f.Client.Attempts == 1)
            {
                f.Enqueue("three");
                return Success(message, ct);
            }
            throw new IOException("second publish failed");
        };
        Assert.False(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(new[] { "two", "three" }, f.Ids());
        Assert.Empty(Directory.GetFiles(f.Directory, "*.tmp"));
    }

    [Fact]
    public async Task CorruptLegacyLineIsQuarantinedWithoutDroppingValidTail()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, "{broken-json\n");
        f.Enqueue("valid");
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(1, f.Client.Attempts);
        Assert.Empty(f.Ids());
        Assert.Equal("{broken-json", File.ReadAllLines(f.Path + ".invalid").Single());
    }

    [Fact]
    public async Task TornLegacyLastLineDoesNotCorruptNextConfirmation()
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, "{torn");
        f.Enqueue("valid");
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(1, f.Client.Attempts);
        Assert.Empty(f.Ids());
        Assert.Equal("{torn", File.ReadAllLines(f.Path + ".invalid").Single());
    }

    [Fact]
    public void TelemetryOverflowCannotEvictDurableConfirmation()
    {
        using var f = new Fixture();
        var state = new LightModuleState
        {
            Address = "0001", CorrelationId = "session", CommandItemId = "item", PickingDetailId = "detail",
            AdjustedQuantity = 2, DisplayData = "00002",
        };
        typeof(MqttEventPublisher).GetMethod("OnItemConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Publisher, [new ConfirmationEvent { Address = "0001" }, state]);
        for (var i = 0; i < 1500; i++) f.Publisher.EnqueueStatus(true, true);
        var line = new DurableEventBuffer(f.Path).ReadBatch().Single();
        using var payload = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(line).Payload));
        Assert.Equal("confirmed", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal("item", payload.RootElement.GetProperty("commandItemId").GetString());
        Assert.Equal("detail", payload.RootElement.GetProperty("pickingDetailId").GetString());
    }

    [Fact]
    public async Task ShutdownDuringOfflineWaitKeepsAcceptedBusinessEvents()
    {
        using var f = new Fixture();
        f.Client.Connected = false;
        using var cts = new CancellationTokenSource();
        var drain = f.Publisher.DrainAsync(cts.Token);
        f.Enqueue("accepted-while-offline");
        cts.Cancel();
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("accepted-while-offline", f.Ids().Single());
    }

    [Fact]
    public async Task ConcurrentBufferAppendsAndPrefixAckPreserveNewTail()
    {
        using var f = new Fixture();
        f.Enqueue("original");
        var outbox = new DurableEventBuffer(f.Path);
        var first = outbox.ReadBatch();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => f.Enqueue($"next-{i}"))));
        outbox.AcknowledgePrefix(first, 1);
        Assert.Equal(32, f.Ids().Length);
        Assert.Equal(32, f.Ids().Distinct().Count());
        Assert.DoesNotContain("original", f.Ids());
    }

    [Fact]
    public void PersistenceFailureIsReportedBeforeAcceptingBusinessEvent()
    {
        using var f = new Fixture(bufferIsDirectory: true);
        var error = Record.Exception(() => f.Enqueue("cannot-save"));
        Assert.NotNull(error);
        Assert.True(error is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void Mqtt5SessionHasExplicitBoundedExpiry()
    {
        var options = MqttBridgeService.BuildOptions(new MqttSettings { Tls = new() { Enabled = false } });
        Assert.Equal(MqttProtocolVersion.V500, options.ProtocolVersion);
        Assert.False(options.CleanSession);
        Assert.Equal(600u, options.SessionExpiryInterval);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(86401u)]
    public void InvalidSessionExpiryIsRejected(uint expiry)
        => Assert.Throws<ArgumentOutOfRangeException>(() => MqttBridgeService.BuildOptions(
            new MqttSettings { SessionExpirySeconds = expiry, Tls = new() { Enabled = false } }));

    [Fact]
    public void OfflineOnlyConfirmationUsesSameQuantityAndItemIdentity()
    {
        using var f = new Fixture();
        var offline = new OfflineBufferService(new MqttSettings { OfflineBufferPath = f.Path }, null!, null!,
            NullLogger<OfflineBufferService>.Instance);
        var state = new LightModuleState
        {
            Address = "0001", CorrelationId = "session", CommandItemId = "item", PickingDetailId = "detail",
            AdjustedQuantity = 2, DisplayData = "00002",
        };
        typeof(OfflineBufferService).GetMethod("OnItemConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(offline, [new ConfirmationEvent { Address = "0001" }, state]);
        var entry = DurableEventBuffer.Parse(new DurableEventBuffer(f.Path).ReadBatch().Single());
        using var payload = JsonDocument.Parse(Convert.FromBase64String(entry.Payload));
        Assert.Equal(2, payload.RootElement.GetProperty("adjustedQuantity").GetInt32());
        Assert.Equal("00002", payload.RootElement.GetProperty("displayData").GetString());
        Assert.Equal("item", payload.RootElement.GetProperty("commandItemId").GetString());
        Assert.Equal("detail", payload.RootElement.GetProperty("pickingDetailId").GetString());
    }

    private static Task<MqttClientPublishResult> Success(MqttApplicationMessage _, CancellationToken __)
        => Task.FromResult(new MqttClientPublishResult(1, MqttClientPublishReasonCode.Success, null, []));

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "p2l-mqtt-unit-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public FakeClient Client { get; }
        public MqttEventPublisher Publisher { get; }
        public AioiGateway Gateway { get; } = new("unused.invalid", 1, NullLogger.Instance);
        public LightModuleRegistry Registry { get; } = new();
        public PickingEngine Engine { get; }
        public Fixture(bool bufferIsDirectory = false)
        {
            System.IO.Directory.CreateDirectory(Directory);
            Path = bufferIsDirectory ? Directory : System.IO.Path.Combine(Directory, "outbox.jsonl");
            Client = FakeClient.New();
            Client.Handler = Success;
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance);
            Publisher = new MqttEventPublisher(Client.Client, new MqttSettings { OfflineBufferPath = Path }, Engine, Registry,
                new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance), NullLogger<MqttEventPublisher>.Instance);
        }
        public void Enqueue(string id) => Publisher.EnqueueCommandResult(new CommandResultEvt
        {
            Type = "command_result", CommandId = id, CorrelationId = "session", Status = "applied",
        });
        public string[] Ids() => new DurableEventBuffer(Path).ReadBatch().Select(line =>
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(line).Payload));
            return json.RootElement.GetProperty("commandId").GetString()!;
        }).ToArray();
        public void Dispose()
        {
            Publisher.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Engine.Dispose();
            Gateway.DisposeAsync().AsTask().GetAwaiter().GetResult();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    public class FakeClient : DispatchProxy
    {
        public bool Connected = true;
        public int Attempts;
        public Func<MqttApplicationMessage, CancellationToken, Task<MqttClientPublishResult>> Handler = null!;
        public IMqttClient Client = null!;
        public static FakeClient New()
        {
            var client = Create<IMqttClient, FakeClient>();
            var fake = (FakeClient)(object)client;
            fake.Client = client;
            return fake;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_IsConnected") return Connected;
            if (method.Name == "PublishAsync")
            {
                Interlocked.Increment(ref Attempts);
                return Handler((MqttApplicationMessage)args![0]!, (CancellationToken)args[1]!);
            }
            if (method.Name == "Dispose" || method.Name.StartsWith("add_") || method.Name.StartsWith("remove_")) return null;
            throw new NotSupportedException(method.Name);
        }
    }
}
