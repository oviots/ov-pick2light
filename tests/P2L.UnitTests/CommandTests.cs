using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Mqtt;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.UnitTests;

public sealed class CommandTests
{
    [Fact]
    public void ReceiptSurvivesRestartAndNeverReexecutesInterruptedIntent()
    {
        var path = TemporaryPath("receipts.db");
        try
        {
            Assert.Null(new CommandReceiptStore(path).TryBegin("id", "hash", "id", "session"));
            var recovered = new CommandReceiptStore(path).TryBegin("id", "hash", "id", "session");
            Assert.Equal("uncertain", recovered!.Status);
            Assert.Equal("RECOVERY_UNCERTAIN", recovered.ErrorCode);
        }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public void CompletedReceiptReplaysOutcomeAndRejectsIdConflict()
    {
        var path = TemporaryPath("receipts.db");
        try
        {
            var store = new CommandReceiptStore(path);
            Assert.Null(store.TryBegin("id", "hash", "id", "session"));
            store.Complete("id", new CommandResultEvt { Type = "command_result", Status = "applied" });
            Assert.Equal("applied", new CommandReceiptStore(path).TryBegin("id", "hash", "id", "session")!.Status);
            Assert.Equal("COMMAND_ID_CONFLICT", store.TryBegin("id", "different", "id", "session")!.ErrorCode);
        }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public void RegistryPersistencePreservesDetailIdentityAndOriginalInstructionTime()
    {
        var path = TemporaryPath("state.json");
        try
        {
            var originalTime = DateTime.UtcNow.AddMinutes(-5);
            var first = new LightModuleRegistry();
            using var persistence = new RegistryPersistenceService(first, NullLogger<RegistryPersistenceService>.Instance, path);
            persistence.Load();
            first.SetInstructed(Item("0001", "01", "session", "item-1"), originalTime);
            // Read immediately, before Dispose/debounce: instruction identity is already durable.
            var restored = new LightModuleRegistry();
            using var reader = new RegistryPersistenceService(restored, NullLogger<RegistryPersistenceService>.Instance, path);
            reader.Load(); reader.Load();
            Assert.Equal("item-1", restored.Get("0001")!.CommandItemId);
            Assert.Equal("detail-item-1", restored.Get("0001")!.PickingDetailId);
            Assert.Equal(originalTime, restored.Get("0001")!.LastInstructionAt);
        }
        finally { DeleteDirectory(path); }
    }

    [Fact]
    public async Task ExplicitPendingRelightAppliesButRetryDoesNotAndCompletedItemCannotRelight()
    {
        await using var f = new Fixture();
        var original = new[] { Item("0001", "01", "session", "one"), Item("0002", "01", "session", "two") };
        await f.Engine.StartBatchAsync(original);
        var before = f.Commands().Count;
        await f.Engine.StartBatchAsync(original);
        Assert.Equal(before, f.Commands().Count);
        f.Confirm("0001");
        await f.Engine.StartBatchAsync([original[1]], relight: true);
        var overwrite = f.Commands().Last(c => c.StartsWith("P5"));
        Assert.DoesNotContain("000100001", overwrite);
        Assert.Contains("000200001", overwrite);
        // WMS captured A/B before the operator confirmed A: completed A must
        // never relight, while B continues without rejecting the whole session.
        await f.Engine.StartBatchAsync(original, relight: true);
        overwrite = f.Commands().Last(c => c.StartsWith("P5"));
        Assert.DoesNotContain("000100001", overwrite);
        Assert.Contains("000200001", overwrite);
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        f.Confirm("0002");
        var afterCompletion = f.Commands().Count;
        await f.Engine.StartBatchAsync(original, relight: true);
        Assert.Equal(afterCompletion, f.Commands().Count);
    }

    [Fact]
    public async Task QuickLightKeepsOtherBlockOutAndTracksNextSameAddressDetail()
    {
        await using var f = new Fixture();
        var completed = new List<string?>();
        var confirmed = new List<LightModuleState?>();
        f.Engine.BatchCompleted += completed.Add;
        f.Engine.ItemConfirmed += (_, state) => confirmed.Add(state);
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one"), Item("0002", "01", "session", "two")]);
        await f.Engine.StartBatchAsync([Item("0003", "02", "other", "three")]);
        f.Confirm("0001");
        await f.Engine.QuickLightAsync("0001", 2, blockNo: "01", correlationId: "session", commandItemId: "next", pickingDetailId: "detail-next");
        var command = f.Commands().Last(c => c.StartsWith("P1"));
        Assert.StartsWith("P101", command);
        Assert.DoesNotContain("000300001", command);
        f.Confirm("0002");
        Assert.DoesNotContain("session", completed);
        f.Confirm("0001");
        Assert.Contains("session", completed);
        Assert.Equal("next", confirmed.Last()!.CommandItemId);
        Assert.Equal("detail-next", confirmed.Last()!.PickingDetailId);
    }

    [Fact]
    public async Task SamePhysicalAddressCannotHaveTwoBlockOwners()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("0001", "01", "first", "one")]);
        var error = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.StartBatchAsync([Item("0001", "02", "second", "two")]));
        Assert.Equal("ADDRESS_BUSY", error.Code);
    }

    [Fact]
    public async Task FailedDurableConfirmSubscriberDoesNotCompleteItem()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        Action<ConfirmationEvent, LightModuleState?> reject = (_, _) => throw new IOException("disk full");
        f.Engine.ItemConfirmed += reject;
        Assert.Throws<IOException>(() => f.Confirm("0001"));
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.True(f.Engine.IsBatchActive);
        f.Engine.ItemConfirmed -= reject;
        f.Confirm("0001");
        Assert.False(f.Engine.IsBatchActive);
    }

    [Fact]
    public async Task DispatcherRejectsExpiredUnknownAndMissingLabelCommandsWithoutHardware()
    {
        await using var f = new Fixture();
        using var dispatcher = f.Dispatcher();
        await dispatcher.Value.HandleAsync(Encoding.UTF8.GetBytes("""{"type":"batch_start","commandId":"expired","expiresAt":"2020-01-01T00:00:00Z","items":[]}"""), CancellationToken.None);
        await dispatcher.Value.HandleAsync(Encoding.UTF8.GetBytes("""{"type":"wrong","commandId":"unknown","expiresAt":"2099-01-01T00:00:00Z"}"""), CancellationToken.None);
        await dispatcher.Value.HandleAsync(Encoding.UTF8.GetBytes("""{"type":"batch_start","commandId":"labels","expiresAt":"2099-01-01T00:00:00Z","items":[{"label":"missing","quantity":1}]}"""), CancellationToken.None);
        Assert.Empty(f.Commands());
        var results = dispatcher.Results();
        Assert.Equal(new[] { "COMMAND_EXPIRED", "UNKNOWN_COMMAND", "UNKNOWN_LABEL" }, results.Select(r => r.ErrorCode));
        Assert.All(results, r => Assert.Equal("rejected", r.Status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DispatcherDuplicateAfterCompletionAndRestartDoesNotSendAgain(bool identified)
    {
        await using var f = new Fixture();
        var payload = Encoding.UTF8.GetBytes("""{"type":"batch_start","commandId":"intent","expiresAt":"2099-01-01T00:00:00Z","correlationId":"session","blockNo":"01","items":[{"address":"0001","quantity":1,"commandItemId":"one","pickingDetailId":"detail-one"}]}""");
        if (!identified) payload = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(payload)
            .Replace("\"commandId\":\"intent\",", "").Replace("\"expiresAt\":\"2099-01-01T00:00:00Z\",", ""));
        using (var first = f.Dispatcher()) await first.Value.HandleAsync(payload, CancellationToken.None);
        f.Confirm("0001");
        var count = f.Commands().Count;
        using var restarted = f.Dispatcher();
        await restarted.Value.HandleAsync(payload, CancellationToken.None);
        Assert.Equal(count, f.Commands().Count);
        Assert.All(restarted.Results(), r => Assert.Equal("applied", r.Status));
    }

    [Fact]
    public async Task IdentityPersistenceFailureStopsBeforeAnyHardwareCommand()
    {
        await using var f = new Fixture();
        var blocked = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "blocked");
        File.WriteAllText(blocked, "not a directory");
        using var persistence = new RegistryPersistenceService(f.Registry, NullLogger<RegistryPersistenceService>.Instance,
            System.IO.Path.Combine(blocked, "state.json"));
        persistence.Load();
        await Assert.ThrowsAsync<IOException>(() => f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]));
        Assert.Empty(f.Commands());
    }

    [Fact]
    public async Task HardwareFailureReceiptPreventsResendOnDuplicate()
    {
        await using var f = new Fixture();
        f.RejectCommands = true;
        using var dispatcher = f.Dispatcher();
        var payload = Encoding.UTF8.GetBytes("""{"type":"batch_start","commandId":"failed","expiresAt":"2099-01-01T00:00:00Z","correlationId":"session","blockNo":"01","items":[{"address":"0001","quantity":1,"commandItemId":"one","pickingDetailId":"detail-one"}]}""");
        await dispatcher.Value.HandleAsync(payload, CancellationToken.None);
        var count = f.Commands().Count;
        Assert.True(count > 0);
        await dispatcher.Value.HandleAsync(payload, CancellationToken.None);
        Assert.Equal(count, f.Commands().Count);
        Assert.All(dispatcher.Results(), result => Assert.Equal("uncertain", result.Status));
    }

    [Fact]
    public async Task FailedExtinguishPreservesCurrentItemIdentity()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        f.RejectCommands = true;
        await Assert.ThrowsAsync<AioiNackException>(() => f.Engine.ExtinguishAsync("0001"));
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.Equal("one", f.Registry.Get("0001")!.CommandItemId);
    }

    [Fact]
    public async Task StaleRelightPreservesNextDetailAlreadyOperatingAtSameAddress()
    {
        await using var f = new Fixture();
        var original = Item("0001", "01", "session", "old");
        await f.Engine.StartBatchAsync([original]);
        f.Confirm("0001");
        await f.Engine.QuickLightAsync("0001", 2, blockNo: "01", correlationId: "session",
            commandItemId: "next", pickingDetailId: "detail-next");
        await f.Engine.StartBatchAsync([original], relight: true);
        var command = f.Commands().Last(c => c.StartsWith("P5"));
        Assert.Contains("000100002", command);
        Assert.DoesNotContain("000100001", command);
        Assert.Equal("next", f.Registry.Get("0001")!.CommandItemId);
        Assert.Equal("detail-next", f.Registry.Get("0001")!.PickingDetailId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task NextDetailWaitsUntilOldConfirmationDurablyCompletes()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "old")]);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Engine.ItemConfirmed += (_, _) =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Test did not release durable confirmation.");
        };
        var confirmation = Task.Run(() => f.Confirm("0001"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var next = Task.Run(async () =>
        {
            nextStarted.SetResult();
            await f.Engine.QuickLightAsync("0001", 2, blockNo: "01", correlationId: "session",
                commandItemId: "next", pickingDetailId: "detail-next");
        });
        try
        {
            await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotSame(next, await Task.WhenAny(next, Task.Delay(50)));
            Assert.Equal("old", f.Registry.Get("0001")!.CommandItemId);
        }
        finally { release.Set(); }
        await confirmation.WaitAsync(TimeSpan.FromSeconds(5));
        await next.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("next", f.Registry.Get("0001")!.CommandItemId);
        Assert.Equal("detail-next", f.Registry.Get("0001")!.PickingDetailId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.True(f.Engine.IsBatchActive);
    }

    private static PickingInstruction Item(string address, string block, string session, string id) => new()
    { Address = address, BlockNo = block, CorrelationId = session, CommandItemId = id, PickingDetailId = "detail-" + id, Quantity = 1 };
    private static string TemporaryPath(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "p2l-command-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir); return Path.Combine(dir, name);
    }
    private static void DeleteDirectory(string path) { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }

    private sealed class Fixture : IAsyncDisposable
    {
        public readonly AioiGateway Gateway = new("unused.invalid", 1, NullLogger.Instance);
        public readonly LightModuleRegistry Registry = new();
        public readonly PickingEngine Engine;
        public readonly string Path = TemporaryPath("commands.db");
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _ackLoop;
        private readonly List<string> _commands = [];
        public bool RejectCommands;
        public Fixture()
        {
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance);
            // No StartAsync: no TCP connection. ACK only the in-memory pending map.
            _ackLoop = Task.Run(async () =>
            {
                var pending = (ConcurrentDictionary<int, TaskCompletionSource<AioiDecoder.ParsedResponse>>)typeof(AioiGateway).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gateway)!;
                while (!_cts.IsCancellationRequested)
                {
                    foreach (var pair in pending) pair.Value.TrySetResult(new AioiDecoder.ParsedResponse { Kind = RejectCommands ? AioiDecoder.ResponseKind.Nack : AioiDecoder.ResponseKind.Ack, Raw = RejectCommands ? "n" : "o" });
                    await Task.Delay(1);
                }
            });
        }
        public IReadOnlyList<string> Commands()
        {
            var transport = typeof(AioiGateway).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gateway)!;
            var queue = transport.GetType().GetField("_sendQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(transport)!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            var read = reader.GetType().GetMethod("TryRead")!;
            object?[] args = [null];
            while ((bool)read.Invoke(reader, args)!)
            {
                var packet = (AioiPacket)args[0]!.GetType().GetProperty("Packet")!.GetValue(args[0])!;
                _commands.Add(packet.Command);
                args[0] = null;
            }
            return _commands.ToArray();
        }
        public void Confirm(string address)
        {
            var handlers = (Action<ConfirmationEvent>?)typeof(AioiGateway).GetField("Confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gateway);
            handlers?.Invoke(new ConfirmationEvent { Address = address });
        }
        public DispatcherFixture Dispatcher() => new(this);
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel(); await _ackLoop; _cts.Dispose(); Engine.Dispose(); await Gateway.DisposeAsync(); DeleteDirectory(Path);
        }
    }
    private sealed class DispatcherFixture : IDisposable
    {
        private readonly string _outbox;
        private readonly MqttEventPublisher _publisher;
        private readonly IMqttClient _client;
        public readonly MqttCommandDispatcher Value;
        public DispatcherFixture(Fixture f)
        {
            _outbox = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, Guid.NewGuid().ToString("N") + ".jsonl");
            _client = new MqttClientFactory().CreateMqttClient();
            var labels = new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance);
            _publisher = new MqttEventPublisher(_client, new MqttSettings { OfflineBufferPath = _outbox }, f.Engine, f.Registry, labels, NullLogger<MqttEventPublisher>.Instance);
            Value = new MqttCommandDispatcher(f.Engine, _publisher, new PreemptionGate(), null, labels,
                new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance, new CommandReceiptStore(f.Path));
        }
        public CommandResultEvt[] Results() => new DurableEventBuffer(_outbox).ReadBatch()
            .Select(line => Convert.FromBase64String(DurableEventBuffer.Parse(line).Payload))
            .Where(payload =>
            {
                using var json = JsonDocument.Parse(payload);
                return json.RootElement.GetProperty("type").GetString() == "command_result";
            })
            .Select(payload => JsonSerializer.Deserialize(payload, P2LMqttJsonContext.Default.CommandResultEvt)!).ToArray();
        public void Dispose() { _publisher.DisposeAsync().AsTask().GetAwaiter().GetResult(); _client.Dispose(); }
    }
}
