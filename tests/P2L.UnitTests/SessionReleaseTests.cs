using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.UnitTests;

public sealed class SessionReleaseTests
{
    [Fact]
    public async Task ReleasesOnlyExactOwnerWithoutSyntheticPickedOrCompletedEvents()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Light("0002");
        await f.Light("0003", block: "02");
        var before = f.Commands.Count;
        await f.Engine.ReleaseSessionAsync("01", "old");
        Assert.Equal(new[] { "D0001", "D0002" }, f.Commands.Skip(before));
        Assert.Equal(ModuleStatus.Standby, f.Registry.Get("0001")!.Status);
        Assert.Equal(ModuleStatus.Standby, f.Registry.Get("0002")!.Status);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0003")!.Status);
        Assert.Empty(f.Confirmations);
        Assert.Empty(f.Completions);
        await f.Light("0001", owner: "new");
        Assert.Equal("new", f.Registry.Get("0001")!.CorrelationId);
    }

    [Fact]
    public async Task StaleCloseDoesNotStopOrReleaseNewOwner()
    {
        await using var f = new Fixture();
        await f.Light("0001", owner: "new");
        var before = f.Commands.Count;
        await f.Engine.ReleaseSessionAsync("01", "old");
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal("new", f.Registry.Get("0001")!.CorrelationId);
        Assert.True(f.Engine.HasActiveWork);
        var rejected = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Light("0002", owner: "old"));
        Assert.Equal("SESSION_CLOSED", rejected.Code);
        Assert.Equal(before, f.Commands.Count);
    }

    [Fact]
    public async Task DuplicateReleaseAndOldIntentsStayFencedAfterRestart()
    {
        string snapshot;
        await using (var first = new Fixture())
        {
            await first.Light("0001");
            await first.Engine.ReleaseSessionAsync("01", "old");
            snapshot = File.ReadAllText(first.StatePath);
        }
        await using var restored = new Fixture(snapshot);
        await restored.Engine.ReleaseSessionAsync("01", "old");
        Assert.Empty(restored.Commands);
        var quick = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => restored.Light("0001"));
        Assert.Equal("SESSION_CLOSED", quick.Code);
        foreach (var relight in new[] { false, true })
        {
            var batch = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
                restored.Engine.StartBatchAsync([Item("0001")], relight: relight));
            Assert.Equal("SESSION_CLOSED", batch.Code);
        }
        Assert.Empty(restored.Commands);
        await restored.Light("0001", owner: "new");
    }

    [Fact]
    public async Task PartialNackRetainsOwnershipAndClosingFenceAcrossRestart()
    {
        string snapshot;
        await using (var first = new Fixture())
        {
            await first.Light("0001");
            await first.Light("0002");
            first.Reject = command => command == "D0002";
            await Assert.ThrowsAsync<AioiNackException>(() => first.Engine.ReleaseSessionAsync("01", "old"));
            Assert.Contains("D0001", first.Commands);
            Assert.Equal("old", first.Registry.Get("0001")!.CorrelationId);
            Assert.Equal("old", first.Registry.Get("0002")!.CorrelationId);
            var before = first.Commands.Count;
            var rejected = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
                first.Engine.StartBatchAsync([Item("0001"), Item("0002")], relight: true));
            Assert.Equal("SESSION_CLOSED", rejected.Code);
            Assert.Equal(before, first.Commands.Count);
            snapshot = File.ReadAllText(first.StatePath);
            Assert.True(JsonNode.Parse(snapshot)!["closedSessions"]![0]!["closing"]!.GetValue<bool>());
        }
        await using var restored = new Fixture(snapshot);
        Assert.Equal("SESSION_CLOSED", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => restored.Light("0001"))).Code);
        Assert.Equal("BLOCK_BUSY", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => restored.Light("0003", owner: "new"))).Code);
        Assert.Empty(restored.Commands);
        await restored.Engine.ReleaseSessionAsync("01", "old");
        Assert.Equal(new[] { "D0001", "D0002" }, restored.Commands);
        await restored.Light("0001", owner: "new");
    }

    [Fact]
    public async Task TimeoutRetainsOwnerAndBlocksRelightUntilReleaseContinues()
    {
        await using var f = new Fixture(executionTimeoutMs: 200);
        await f.Light("0001");
        f.DropTerminateReply = true;
        await Assert.ThrowsAsync<TimeoutException>(() => f.Engine.ReleaseSessionAsync("01", "old"));
        Assert.Equal("old", f.Registry.Get("0001")!.CorrelationId);
        Assert.True(f.Engine.HasActiveWork);
        Assert.Equal("SESSION_CLOSED", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Light("0001"))).Code);
        f.DropTerminateReply = false;
        await f.Engine.ReleaseSessionAsync("01", "old");
        await f.Light("0001", owner: "new");
    }

    [Fact]
    public async Task ClosingSaveFailurePerformsNoHardwareIo()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        var before = f.Commands.Count;
        Directory.CreateDirectory(f.StatePath + ".tmp");
        var error = await Record.ExceptionAsync(() => f.Engine.ReleaseSessionAsync("01", "old"));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal("old", f.Registry.Get("0001")!.CorrelationId);
        Directory.Delete(f.StatePath + ".tmp");
        await f.Engine.StartBatchAsync([Item("0001")], relight: true);
    }

    [Fact]
    public async Task FinalSaveFailureRollsBackOwnerButKeepsDurableClosingFence()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        f.BeforeReply = packet =>
        {
            if (packet.Command == "D0001") Directory.CreateDirectory(f.StatePath + ".tmp");
        };
        var error = await Record.ExceptionAsync(() => f.Engine.ReleaseSessionAsync("01", "old"));
        Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.Equal("old", f.Registry.Get("0001")!.CorrelationId);
        Assert.True(JsonNode.Parse(File.ReadAllText(f.StatePath))!["closedSessions"]![0]!["closing"]!.GetValue<bool>());
        Assert.Equal("SESSION_CLOSED", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Light("0001"))).Code);
        f.BeforeReply = null;
        Directory.Delete(f.StatePath + ".tmp");
        await f.Engine.ReleaseSessionAsync("01", "old");
        await f.Light("0001", owner: "new");
    }

    [Fact]
    public async Task RealConfirmationDuringDrainKeepsItsIdentityWithoutSyntheticCompletion()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        await f.Light("0002");
        f.BeforeReply = packet =>
        {
            if (packet.Command == "D0001") f.Confirm("0001");
        };
        await f.Engine.ReleaseSessionAsync("01", "old");
        var actual = Assert.Single(f.Confirmations);
        Assert.Equal("item-0001", actual.State.CommandItemId);
        Assert.Null(actual.Event.Source);
        Assert.True(actual.Event.SequenceNo >= 0);
        Assert.Empty(f.Completions);
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.Equal(ModuleStatus.Standby, f.Registry.Get("0002")!.Status);
        Assert.False(f.Engine.HasActiveWork);
    }

    [Fact]
    public async Task ReleaseReplaysRealPendingConfirmationBeforeClosing()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        f.FailConfirmation = true;
        f.Confirm("0001");
        var snapshot = JsonNode.Parse(File.ReadAllText(f.StatePath))!;
        var eventId = snapshot["interrupts"]![0]!["eventId"]!.GetValue<string>();
        var before = f.Commands.Count;
        await Assert.ThrowsAsync<IOException>(() => f.Engine.ReleaseSessionAsync("01", "old"));
        Assert.Equal(before, f.Commands.Count);
        Assert.True(f.Engine.HasActiveWork);
        Assert.Empty(f.Confirmations);
        f.FailConfirmation = false;
        await f.Engine.ReleaseSessionAsync("01", "old");
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal(eventId, Assert.Single(f.Confirmations).Event.EventId);
        Assert.Single(f.Completions); // Generated by the real t, never by release.
        Assert.False(f.Engine.HasActiveWork);
        await f.Light("0001", owner: "new");
    }

    [Fact]
    public async Task MissingFlagCanBeReleasedWithoutPickingAndRetryIsFencedWhileClosing()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        f.FailMissing = true;
        await f.Engine.ProbeActiveConfirmationsAsync(1, 0, 1000, 0);
        var state = f.Registry.Get("0001")!;
        Assert.NotNull(state.ConfirmationMissingAt);
        await Assert.ThrowsAsync<IOException>(() => f.Engine.ReleaseSessionAsync("01", "old"));
        var before = f.Commands.Count;
        var rejected = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ResolveConfirmationAsync("0001", "01", "old",
            state.CommandItemId, state.PickingDetailId, state.ConfirmationMissingEventId!, "retry", null, "retry-intent"));
        Assert.Equal("SESSION_CLOSED", rejected.Code);
        Assert.Equal(before, f.Commands.Count);
        Assert.Equal("old", f.Registry.Get("0001")!.CorrelationId);
        f.FailMissing = false;
        await f.Engine.ReleaseSessionAsync("01", "old");
        Assert.Equal(state.ConfirmationMissingEventId, Assert.Single(f.Missing).EventId);
        Assert.Empty(f.Confirmations);
        Assert.Empty(f.Completions);
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
        Assert.False(f.Engine.HasActiveWork);
    }

    [Fact]
    public async Task LegacyVersionTwoAndExpiredClosedRecordsRemainCompatible()
    {
        const string legacy = "{\"version\":2,\"modules\":[],\"completedItems\":[],\"interrupts\":[]}";
        await using var f = new Fixture(legacy);
        await f.Light("0001");
        await f.Engine.ReleaseSessionAsync("01", "old");
        var snapshot = JsonNode.Parse(File.ReadAllText(f.StatePath))!;
        snapshot["closedSessions"]![0]!["closedAt"] = DateTime.UtcNow.AddHours(-25);
        await using var restored = new Fixture(snapshot.ToJsonString());
        await restored.Light("0001");
        Assert.Equal("old", restored.Registry.Get("0001")!.CorrelationId);
    }

    [Fact]
    public async Task ClosingRecordNeverExpiresBeforeItsOwnerIsReleased()
    {
        await using var f = new Fixture();
        await f.Light("0001");
        f.Reject = c => c == "D0001";
        await Assert.ThrowsAsync<AioiNackException>(() => f.Engine.ReleaseSessionAsync("01", "old"));
        var snapshot = JsonNode.Parse(File.ReadAllText(f.StatePath))!;
        snapshot["closedSessions"]![0]!["closedAt"] = DateTime.UtcNow.AddDays(-2);
        await using var restored = new Fixture(snapshot.ToJsonString());
        Assert.Equal("SESSION_CLOSED", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() => restored.Light("0001"))).Code);
        Assert.Empty(restored.Commands);
        await restored.Engine.ReleaseSessionAsync("01", "old");
        await restored.Light("0001", owner: "new");
    }

    [Theory]
    [InlineData(null, "old")]
    [InlineData("1", "old")]
    [InlineData("01", null)]
    [InlineData("01", " ")]
    public async Task InvalidReleaseIdentityDoesNotSendHardware(string? block, string? owner)
    {
        await using var f = new Fixture();
        Assert.Equal("INVALID_SESSION", (await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.ReleaseSessionAsync(block!, owner!))).Code);
        Assert.Empty(f.Commands);
    }

    private static PickingInstruction Item(string address, string owner = "old", string block = "01") => new()
    {
        Address = address, Quantity = 5, BlockNo = block, CorrelationId = owner,
        CommandItemId = "item-" + address, PickingDetailId = "detail-" + address
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        public readonly AioiGateway Gateway = new("unused.invalid", 1, NullLogger.Instance);
        public readonly LightModuleRegistry Registry = new();
        public readonly PickingEngine Engine;
        public readonly ConcurrentQueue<string> Commands = new();
        public readonly List<(ConfirmationEvent Event, LightModuleState State)> Confirmations = [];
        public readonly List<BatchCompletionEvent> Completions = [];
        public readonly List<ConfirmationMissingEvent> Missing = [];
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "p2l-release-tests-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(_directory, "state.json");
        public Func<string, bool>? Reject;
        public Action<AioiPacket>? BeforeReply;
        public bool DropTerminateReply, FailConfirmation, FailMissing;
        private readonly RegistryPersistenceService _persistence;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private int _seq;

        public Fixture(string? snapshot = null, int executionTimeoutMs = 25000)
        {
            Directory.CreateDirectory(_directory);
            if (snapshot is not null) File.WriteAllText(StatePath, snapshot);
            _persistence = new RegistryPersistenceService(Registry, NullLogger<RegistryPersistenceService>.Instance, StatePath);
            _persistence.Load();
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance, executionTimeoutMs);
            Engine.RequireDurableConfirmationConsumer();
            Engine.DurableConfirmation += (evt, state) =>
            {
                if (FailConfirmation) throw new IOException("confirmation outbox failed");
                Confirmations.Add((evt, state!));
            };
            Engine.DurableBatchCompleted += Completions.Add;
            Engine.DurableConfirmationMissing += evt =>
            {
                if (FailMissing) throw new IOException("missing outbox failed");
                Missing.Add(evt);
            };
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (Take() is not { } packet) { await Task.Delay(1); continue; }
                    Commands.Enqueue(packet.Command);
                    BeforeReply?.Invoke(packet);
                    if (DropTerminateReply && packet.Command.StartsWith('D')) continue;
                    Reply(packet.SequenceNo, Reject?.Invoke(packet.Command) == true ? "n"
                        : packet.Command.StartsWith("Az", StringComparison.Ordinal) ? "a" + packet.Command[2..] + "1" : "o");
                }
            });
        }

        public Task Light(string address, string owner = "old", string block = "01") => Engine.QuickLightAsync(address, 5,
            blockNo: block, correlationId: owner, commandItemId: "item-" + address, pickingDetailId: "detail-" + address);
        public void Confirm(string address) => Reply(Interlocked.Increment(ref _seq), "t" + address + "00");
        private void Reply(int seq, string command) => typeof(AioiGateway).GetMethod("OnPacketReceived", Private)!.Invoke(Gateway,
            [new AioiPacket { SequenceNo = seq, Command = command }]);
        private AioiPacket? Take()
        {
            var transport = typeof(AioiGateway).GetField("_transport", Private)!.GetValue(Gateway)!;
            var queue = transport.GetType().GetField("_sendQueue", Private)!.GetValue(transport)!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
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
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _loop;
            Engine.Dispose();
            _persistence.Dispose();
            await Gateway.DisposeAsync();
            _cts.Dispose();
            Directory.Delete(_directory, true);
        }
    }
}
