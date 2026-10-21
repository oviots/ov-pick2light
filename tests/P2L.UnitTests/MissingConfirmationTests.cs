using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.UnitTests;

public sealed class MissingConfirmationTests
{
    [Fact]
    public async Task ObservationDoesNotPickAndImplicitBatchExcludesUnknownAddress()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe();
        var missing = Assert.Single(f.Missing);
        Assert.Equal("0001", missing.Address);
        Assert.NotNull(f.Registry.Get("0001")!.ConfirmationMissingAt);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.Empty(f.Confirmations);
        await f.Light("0002");
        var frame = f.Commands.Last(c => c.StartsWith("P1", StringComparison.Ordinal));
        Assert.DoesNotContain("000100005", frame);
        Assert.Contains("000200005", frame);
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Light("0001"));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ExtinguishAsync("0001"));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.FinishBatchAsync("00"));
        Assert.True(f.Engine.HasActiveWork);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("manual:legacy")]
    public async Task LegacyInstructionWithoutRecoverableIdentityIsNotFlagged(string? owner)
    {
        await using var f = new Fixture();
        await f.Engine.QuickLightAsync("0001", 1, blockNo: "01", correlationId: owner);
        Assert.Equal(0, await f.Probe());
        Assert.Empty(f.Missing);
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
    }

    [Fact]
    public async Task TwoConsistentObservationsWaitForGraceAndRealConfirmationWins()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe(grace: 10000);
        Assert.Empty(f.Missing);
        f.Clock.Advance(TimeSpan.FromSeconds(9));
        await f.Probe(grace: 10000);
        Assert.Empty(f.Missing);
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        await f.Probe(grace: 10000);
        Assert.Single(f.Missing);
        f.Confirm("0001");
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
        Assert.Single(f.Confirmations);
        Assert.Null(f.Confirmations[0].Source);
    }

    [Fact]
    public async Task HealthyObservationResetsMismatchGrace()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe(grace: 10000);
        f.Clock.Advance(TimeSpan.FromSeconds(20));
        f.Status = '2';
        await f.Probe(grace: 10000);
        f.Status = '1';
        await f.Probe(grace: 10000);
        Assert.Empty(f.Missing);
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        await f.Probe(grace: 10000);
        Assert.Single(f.Missing);
    }

    [Fact]
    public async Task RoundRobinIsBoundedAndTimeoutDoesNotProveMissingConfirmation()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Light("0002");
        await f.Light("0003");
        Assert.Equal(2, await f.Probe(max: 2));
        Assert.Equal(2, f.Missing.Count);
        f.AutoReply = false;
        Assert.Equal(1, await f.Probe(max: 1, timeout: 25));
        Assert.Null(f.Registry.Get("0003")!.ConfirmationMissingAt);
        f.AutoReply = true;
        Assert.Equal(1, await f.Probe(max: 1));
        Assert.Equal(3, f.Missing.Count);
    }

    [Fact]
    public async Task ProbeDoesNotWaitForOrInterleaveWithHardwareSetup()
    {
        await using var f = new Fixture();
        f.AutoReply = false;
        var light = f.Light("0001");
        await f.Until(() => f.HasQueuedPacket());
        Assert.Equal(0, await f.Probe());
        Assert.DoesNotContain(f.Commands, c => c.StartsWith("Az", StringComparison.Ordinal));
        f.AutoReply = true;
        await light.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task MissingFlagAndUndeliveredEventSurviveRestartWithStableIdentity()
    {
        string snapshot, eventId;
        await using (var first = new Fixture(persist: true))
        {
            await first.Light("0001");
            first.FailMissing = true;
            await first.Probe();
            eventId = first.Registry.Get("0001")!.ConfirmationMissingEventId!;
            snapshot = File.ReadAllText(first.StatePath);
            Assert.Empty(first.Missing);
        }
        await using var restored = new Fixture(snapshot: snapshot);
        restored.Engine.ReplayPendingConfirmations();
        Assert.Equal(eventId, Assert.Single(restored.Missing).EventId);
        Assert.Equal(eventId, restored.Registry.Get("0001")!.ConfirmationMissingEventId);
        Assert.Empty(restored.Confirmations);
        Assert.Equal("CONFIRMATION_RECOVERY_REQUIRED",
            (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => restored.Light("0001"))).Code);
    }

    [Fact]
    public async Task PendingMissingDeliveryRetriesEvenWhenAllActiveItemsAreAlreadyFlagged()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        f.FailMissing = true;
        await f.Probe();
        var eventId = f.Registry.Get("0001")!.ConfirmationMissingEventId;
        Assert.Empty(f.Missing);
        var probes = f.Commands.Count(c => c.StartsWith("Az", StringComparison.Ordinal));
        f.FailMissing = false;
        Assert.Equal(0, await f.Probe());
        Assert.Equal(eventId, Assert.Single(f.Missing).EventId);
        Assert.Equal(probes, f.Commands.Count(c => c.StartsWith("Az", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ExplicitActualQuantityIsDurableAndNeverUsesAValidRawSequence(int quantity)
    {
        await using var f = new Fixture(persist: true);
        await f.Light("0001");
        await f.Probe();
        await f.Resolve("0001", "confirm", quantity, "resolution-one");
        var evt = Assert.Single(f.Confirmations);
        Assert.Equal(-1, evt.SequenceNo);
        Assert.Equal("manual_recovery", evt.Source);
        Assert.Equal("resolution-one", evt.EventId);
        Assert.Equal(quantity.ToString("D5"), evt.AdjustedDisplay);
        Assert.Equal(quantity, f.Registry.Get("0001")!.AdjustedQuantity);
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
        using var state = JsonDocument.Parse(File.ReadAllText(f.StatePath));
        Assert.Contains(state.RootElement.GetProperty("interrupts").EnumerateArray(),
            i => i.GetProperty("sequenceNo").GetInt32() == -1 && i.GetProperty("source").GetString() == "manual_recovery");
    }

    [Fact]
    public async Task RetryOnlyRelightsSelectedUnknownItemAndFailureRetainsFence()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Light("0002");
        await f.Probe(max: 2);
        f.Reject = command => command.StartsWith("PP50500", StringComparison.Ordinal);
        await Assert.ThrowsAsync<AioiNackException>(() => f.Resolve("0001", "retry"));
        Assert.NotNull(f.Registry.Get("0001")!.ConfirmationMissingAt);
        f.Reject = null;
        await f.Resolve("0001", "retry");
        var frame = f.Commands.Last(c => c.StartsWith("PP50500", StringComparison.Ordinal));
        Assert.Contains("000100005", frame);
        Assert.DoesNotContain("000200005", frame);
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
        Assert.NotNull(f.Registry.Get("0002")!.ConfirmationMissingAt);
        Assert.Empty(f.Confirmations);
    }

    [Fact]
    public async Task RealRawConfirmationDuringDrainWinsWithoutManualDoublePick()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe();
        f.BeforeReply = packet =>
        {
            if (packet.Command == "D0001") { f.BeforeReply = null; f.Confirm("0001"); }
        };
        await f.Resolve("0001", "confirm", 2);
        var actual = Assert.Single(f.Confirmations);
        Assert.Null(actual.Source);
        Assert.True(actual.SequenceNo >= 0);
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
    }

    [Fact]
    public async Task InvalidOwnerStaleObservationQuantityAndWmsCancelHaveNoHardwareIo()
    {
        await using var f = new Fixture();
        await f.Light("0001", "wms-session");
        await f.Probe();
        int count = f.Commands.Count;
        var state = f.Registry.Get("0001")!;
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ResolveConfirmationAsync("10001", "01", "wms-session",
            state.CommandItemId, state.PickingDetailId, state.ConfirmationMissingEventId!, "confirm", 1, "resolution"));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ResolveConfirmationAsync("0001", "01", "wrong-owner",
            state.CommandItemId, state.PickingDetailId, state.ConfirmationMissingEventId!, "confirm", 1, "resolution"));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ResolveConfirmationAsync("0001", "01", "wms-session",
            state.CommandItemId, state.PickingDetailId, "stale", "confirm", 1, "resolution"));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Resolve("0001", "confirm", 6));
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Resolve("0001", "cancel"));
        Assert.Equal(count, f.Commands.Count);
    }

    [Fact]
    public async Task ManualCancelReleasesOnlyItsOwnerWithoutSyntheticPickedEvents()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe();
        await f.Resolve("0001", "cancel");
        Assert.Empty(f.Confirmations);
        Assert.Empty(f.Completions);
        Assert.False(f.Engine.HasActiveWork);
        await f.Light("0001", "manual:next");
        Assert.Equal("manual:next", f.Registry.Get("0001")!.CorrelationId);
    }

    [Fact]
    public async Task EmergencyStopSendsD00ButPreservesUnknownOwnership()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Probe();
        await f.Light("0002");
        await f.Engine.EmergencyStopAsync();
        Assert.Contains("D00", f.Commands);
        Assert.NotNull(f.Registry.Get("0001")!.ConfirmationMissingAt);
        Assert.Equal("manual:test", f.Registry.Get("0001")!.CorrelationId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.Equal(ModuleStatus.Standby, f.Registry.Get("0002")!.Status);
        Assert.Empty(f.Confirmations);
        Assert.Empty(f.Completions);
        Assert.True(f.Engine.HasActiveWork);
    }

    [Fact]
    public async Task TwoManualPendingConfirmationsReplayByEventIdAcrossRestart()
    {
        string snapshot;
        await using (var first = new Fixture(persist: true))
        {
            await first.Light("0001");
            await first.Light("0002");
            await first.Probe(max: 2);
            first.FailConfirmation = true;
            await Assert.ThrowsAsync<IOException>(() => first.Resolve("0001", "confirm", 1, "resolution-A"));
            await Assert.ThrowsAsync<PickingCommandRejectedException>(() => first.Resolve("0002", "confirm", 2, "resolution-A"));
            await Assert.ThrowsAsync<IOException>(() => first.Resolve("0002", "confirm", 2, "resolution-B"));
            snapshot = File.ReadAllText(first.StatePath);
        }
        await using var restored = new Fixture(snapshot: snapshot);
        restored.Engine.ReplayPendingConfirmations();
        Assert.Equal(new[] { "resolution-A", "resolution-B" }, restored.Confirmations.Select(e => e.EventId));
        Assert.All(restored.Confirmations, e => Assert.Equal("manual_recovery", e.Source));
        Assert.False(restored.Engine.HasActiveWork);
    }

    [Fact]
    public async Task FailedMissingStateSaveStillExcludesAddressUntilStorageRecovers()
    {
        await using var f = new Fixture(persist: true);
        await f.Light("0001");
        Directory.CreateDirectory(f.StatePath + ".tmp");
        await f.Probe();
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Light("0001"));
        Directory.Delete(f.StatePath + ".tmp");
        f.Status = '2'; // A later healthy read must not strand the failed durable fence.
        await f.Light("0002");
        Assert.DoesNotContain("000100005", f.Commands.Last(c => c.StartsWith("P1", StringComparison.Ordinal)));
        await f.Probe(max: 2);
        Assert.NotNull(f.Registry.Get("0001")!.ConfirmationMissingAt);
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddHours(1);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        public readonly AioiGateway Gateway = new("unused.invalid", 1, NullLogger.Instance);
        public readonly LightModuleRegistry Registry = new();
        public readonly PickingEngine Engine;
        public readonly Clock Clock = new();
        public readonly ConcurrentQueue<string> Commands = new();
        public readonly List<ConfirmationMissingEvent> Missing = [];
        public readonly List<ConfirmationEvent> Confirmations = [];
        public readonly List<BatchCompletionEvent> Completions = [];
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "p2l-missing-tests-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(DirectoryPath, "state.json");
        public volatile bool AutoReply = true;
        public bool FailMissing, FailConfirmation;
        public char Status = '1';
        public Func<string, bool>? Reject;
        public Action<AioiPacket>? BeforeReply;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly RegistryPersistenceService? _persistence;
        private int _seq;

        public Fixture(bool persist = false, string? snapshot = null)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (snapshot is not null) File.WriteAllText(StatePath, snapshot);
            if (persist || snapshot is not null)
            {
                _persistence = new RegistryPersistenceService(Registry, NullLogger<RegistryPersistenceService>.Instance, StatePath);
                _persistence.Load();
            }
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance, clock: Clock);
            Engine.RequireDurableConfirmationConsumer();
            Engine.DurableConfirmationMissing += evt => { if (FailMissing) throw new IOException("missing outbox failed"); Missing.Add(evt); };
            Engine.DurableConfirmation += (evt, _) => { if (FailConfirmation) throw new IOException("confirmation outbox failed"); Confirmations.Add(evt); };
            Engine.DurableBatchCompleted += Completions.Add;
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (!AutoReply || Take() is not { } packet) { await Task.Delay(1); continue; }
                    Commands.Enqueue(packet.Command);
                    BeforeReply?.Invoke(packet);
                    string response = Reject?.Invoke(packet.Command) == true ? "n"
                        : packet.Command.StartsWith("Az", StringComparison.Ordinal) ? "a" + packet.Command[2..] + Status : "o";
                    typeof(AioiGateway).GetMethod("OnPacketReceived", Private)!.Invoke(Gateway,
                        [new AioiPacket { SequenceNo = packet.SequenceNo, Command = response }]);
                }
            });
        }

        public Task Light(string address, string owner = "manual:test") => Engine.QuickLightAsync(address, 5,
            blockNo: "01", correlationId: owner, commandItemId: "item-" + address, pickingDetailId: "detail-" + address);
        public Task<int> Probe(int max = 1, int timeout = 1000, int grace = 0) =>
            Engine.ProbeActiveConfirmationsAsync(max, 0, timeout, grace);
        public Task Resolve(string address, string action, int? qty = null, string id = "resolution")
        {
            var state = Registry.Get(address)!;
            return Engine.ResolveConfirmationAsync(address, state.BlockNo, state.CorrelationId!, state.CommandItemId,
                state.PickingDetailId, state.ConfirmationMissingEventId!, action, qty, id);
        }
        public void Confirm(string address) => ((Action<ConfirmationEvent>)typeof(AioiGateway).GetField("Confirmed", Private)!.GetValue(Gateway)!)
            .Invoke(new ConfirmationEvent { Address = address, SequenceNo = Interlocked.Increment(ref _seq) });
        private object Reader()
        {
            var transport = typeof(AioiGateway).GetField("_transport", Private)!.GetValue(Gateway)!;
            var queue = transport.GetType().GetField("_sendQueue", Private)!.GetValue(transport)!;
            return queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
        }
        public bool HasQueuedPacket() => (bool)Reader().GetType().GetProperty("CanCount")!.GetValue(Reader())!
            ? (int)Reader().GetType().GetProperty("Count")!.GetValue(Reader())! > 0 : true;
        private AioiPacket? Take()
        {
            var reader = Reader();
            object?[] args = [null];
            if (!(bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, args)!) return null;
            var entry = args[0]!;
            var packet = (AioiPacket)entry.GetType().GetProperty("Packet")!.GetValue(entry)!;
            if (entry.GetType().GetProperty("WriteCurrent")!.GetValue(entry) is Func<Func<string?, ValueTask>, ValueTask> current)
            {
                string? command = null;
                current(value => { command = value; return ValueTask.CompletedTask; }).GetAwaiter().GetResult();
                if (command is null) { ((Action?)entry.GetType().GetProperty("Skipped")!.GetValue(entry))?.Invoke(); return null; }
                packet = new AioiPacket { SequenceNo = packet.SequenceNo, Command = command };
            }
            return packet;
        }
        public async Task Until(Func<bool> condition)
        {
            using var limit = new CancellationTokenSource(3000);
            while (!condition()) await Task.Delay(1, limit.Token);
        }
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
            Engine.Dispose();
            _persistence?.Dispose();
            await Gateway.DisposeAsync();
            _cts.Dispose();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
