using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using P2L.Core;
using P2L.Core.Services;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.UnitTests;

public sealed class CoreReliabilityTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public CoreReliabilityTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RepeatedInterruptKeepsOriginalOwnerAcrossAddressReuse()
    {
        await using var f = new Fixture();
        var confirmed = new List<string?>();
        f.Engine.ItemConfirmed += (_, state) => confirmed.Add(state!.CommandItemId);
        await f.Engine.StartBatchAsync([Item("A")]);
        f.Confirm(73);
        f.AutoAck = false;
        await Task.Delay(10);
        var next = f.Engine.StartBatchAsync([Item("B")]);
        var mode = await f.NextAsync();
        f.Confirm(73);
        Assert.Equal(new[] { "A" }, confirmed);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        f.Reply(mode, "o");
        f.Reply(await f.NextAsync(), "o");
        await next;
        f.Confirm(74);
        Assert.Equal(new[] { "A", "B" }, confirmed);
    }

    [Fact]
    public async Task ConfirmationSaveFailureRollsBackAndRedeliveryFinishesBookkeeping()
    {
        await using var f = new Fixture(persist: true);
        var durableIds = new List<string?>();
        f.Engine.ItemConfirmed += (_, state) => durableIds.Add(state!.CommandItemId);
        await f.Engine.StartBatchAsync([Item("A")]);
        Directory.CreateDirectory(f.StatePath + ".tmp");
        f.Confirm(1);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.True(f.Engine.IsBatchActive);
        Assert.Empty(durableIds);
        Directory.Delete(f.StatePath + ".tmp");
        f.Confirm(1);
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.False(f.Engine.IsBatchActive);
        Assert.Single(durableIds);
        Assert.All(durableIds, id => Assert.Equal("A", id));
        await f.Engine.StartBatchAsync([Item("B", session: "next")]);
    }

    [Fact]
    public async Task PendingConfirmationSurvivesOutboxFailureAndRestartWithStableIdentity()
    {
        string snapshot;
        string eventId;
        await using (var first = new Fixture(persist: true))
        {
            await first.Engine.StartBatchAsync([Item("A")]);
            first.Engine.DurableConfirmation += (_, _) => throw new IOException("outbox unavailable");
            first.Confirm(5);
            Assert.Equal(ModuleStatus.Completed, first.Registry.Get("0001")!.Status);
            Assert.True(first.Engine.HasActiveWork);
            snapshot = File.ReadAllText(first.StatePath);
            using var document = JsonDocument.Parse(snapshot);
            eventId = document.RootElement.GetProperty("interrupts")[0].GetProperty("eventId").GetString()!;
        }
        await using var second = new Fixture();
        File.WriteAllText(second.StatePath, snapshot);
        using var persistence = new RegistryPersistenceService(second.Registry, NullLogger<RegistryPersistenceService>.Instance, second.StatePath);
        persistence.Load();
        var events = new List<(string?, string?)>();
        second.Engine.DurableConfirmation += (evt, state) => events.Add((evt.EventId, state!.CommandItemId));
        Assert.True(second.Engine.HasActiveWork);
        second.Engine.ReplayPendingConfirmations();
        Assert.Equal(new[] { ((string?)eventId, (string?)"A") }, events);
        Assert.False(second.Engine.HasActiveWork);
        await second.Engine.StartBatchAsync([Item("B")]);
        second.Confirm(5);
        Assert.Equal(ModuleStatus.Operating, second.Registry.Get("0001")!.Status);
        Assert.Single(events);
    }

    [Fact]
    public async Task OutboxSuccessThenPendingClearFailureFencesNextItemAndReplaysSameEvent()
    {
        await using var f = new Fixture(persist: true);
        await f.Engine.StartBatchAsync([Item("A")]);
        var events = new List<(string?, string?)>();
        bool failClear = true;
        f.Engine.DurableConfirmation += (evt, state) =>
        {
            events.Add((evt.EventId, state!.CommandItemId));
            if (failClear)
                Directory.CreateDirectory(f.StatePath + ".tmp");
        };
        f.Confirm(1);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.QuickLightAsync("0001", 2,
            blockNo: "01", correlationId: "session", commandItemId: "B", pickingDetailId: "detail-B"));
        Assert.Equal("ADDRESS_BUSY", busy.Code);
        Assert.True(f.Engine.IsBatchActive);
        failClear = false;
        Directory.Delete(f.StatePath + ".tmp");
        f.Engine.ReplayPendingConfirmations();
        Assert.Equal(2, events.Count);
        Assert.Equal(events[0], events[1]);
        Assert.Equal("A", events[0].Item2);
        Assert.False(f.Engine.IsBatchActive);
        await f.Engine.QuickLightAsync("0001", 2, blockNo: "01", correlationId: "session", commandItemId: "B", pickingDetailId: "detail-B");
        f.Confirm(1);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task KnownPreIoSaveFailureRetriesActualHardwareAfterRepair()
    {
        await using var f = new Fixture(persist: true);
        Directory.CreateDirectory(f.StatePath + ".tmp");
        var error = await Record.ExceptionAsync(() => f.Engine.StartBatchAsync([Item("A")]));
        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Empty(f.Commands);
        Assert.Null(f.Registry.Get("0001"));
        Directory.Delete(f.StatePath + ".tmp");
        await f.Engine.StartBatchAsync([Item("A")]);
        Assert.Contains(f.Commands, c => c.StartsWith("P1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AmbiguousHardwareFailureCannotBecomeDuplicateAppliedShortcut()
    {
        await using var f = new Fixture();
        f.Reject = true;
        await Assert.ThrowsAsync<AioiNackException>(() => f.Engine.StartBatchAsync([Item("A")]));
        var count = f.Commands.Count;
        f.Reject = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine.StartBatchAsync([Item("A")]));
        Assert.Equal(count, f.Commands.Count);
    }

    [Fact]
    public async Task LegacyStateRestoresBlockOwnerAndDoesNotExpireUnfinishedWork()
    {
        await using var f = new Fixture();
        File.WriteAllText(f.StatePath, JsonSerializer.Serialize(new[] { new {
            address = "0001", quantity = 1, color = "Green", ledMode = "On", segMode = "On", buzMode = "Off",
            blockNo = "01", correlationId = "old", commandItemId = "A", pickingDetailId = "detail-A",
            lastInstructionAt = DateTime.UtcNow.AddDays(-2) } }));
        using var persistence = new RegistryPersistenceService(f.Registry, NullLogger<RegistryPersistenceService>.Instance, f.StatePath);
        persistence.Load();
        Assert.True(f.Engine.HasActiveWork);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.StartBatchAsync([Item("B", "0002", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        var completed = new List<string?>();
        f.Engine.BatchCompleted += completed.Add;
        f.Confirm(1);
        Assert.Equal(new[] { "old" }, completed);
        using var saved = JsonDocument.Parse(File.ReadAllText(f.StatePath));
        Assert.Equal(2, saved.RootElement.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task CompletedItemsAndInterruptGuardsSurviveRestartAndAddressReuse()
    {
        await using var first = new Fixture(persist: true);
        await first.Engine.StartBatchAsync([Item("A")]);
        first.Confirm(1);
        await first.Engine.StartBatchAsync([Item("B")]);
        first.Confirm(2);
        await using var second = new Fixture();
        using var reader = new RegistryPersistenceService(second.Registry, NullLogger<RegistryPersistenceService>.Instance, first.StatePath);
        reader.Load();
        await second.Engine.StartBatchAsync([Item("A")], relight: true);
        await second.Engine.StartBatchAsync([Item("B")], relight: true);
        Assert.Empty(second.Commands);
        await second.Engine.StartBatchAsync([Item("C")]);
        second.Confirm(1);
        Assert.Equal(ModuleStatus.Operating, second.Registry.Get("0001")!.Status);
        second.Confirm(3);
        Assert.Equal(ModuleStatus.Completed, second.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task InvalidExistingStateFailsClosedAndIsNeverOverwrittenOnDispose()
    {
        await using var f = new Fixture();
        f.Registry.SetInstructed(Item("existing"));
        const string invalid = "{broken-state";
        File.WriteAllText(f.StatePath, invalid);
        using (var persistence = new RegistryPersistenceService(f.Registry, NullLogger<RegistryPersistenceService>.Instance, f.StatePath))
            Assert.Throws<IOException>(persistence.Load);
        Assert.Equal(invalid, File.ReadAllText(f.StatePath));
        Assert.Equal("existing", f.Registry.Get("0001")!.CommandItemId);
    }

    [Fact]
    public async Task ScanObservationDoesNotDisableBusinessConfirmation()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("A")]);
        var scan = new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance);
        await scan.ScanAsync(1, 1);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
        Assert.Equal(ModuleStatus.Operating2, f.Registry.Get("0001")!.ObservedStatus);
        f.Confirm(1);
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.False(f.Engine.IsBatchActive);
    }

    [Fact]
    public async Task FinishPreventsNextStartUntilItsStateTransitionCommits()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("A")]);
        f.AutoAck = false;
        await Task.Delay(10);
        var finish = f.Engine.FinishBatchAsync("01");
        var terminate = await f.NextAsync();
        f.Confirm(1);
        var next = f.Engine.StartBatchAsync([Item("B", "0002", "next")]);
        Assert.False(next.IsCompleted);
        Assert.Null(f.Read());
        f.Reply(terminate, "o");
        await finish;
        f.Reply(await f.NextAsync(), "o");
        f.Reply(await f.NextAsync(), "o");
        await next;
        Assert.Equal("B", f.Registry.Get("0002")!.CommandItemId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0002")!.Status);
        Assert.True(f.Engine.IsBatchActive);
    }

    [Fact]
    public async Task CsvCleanupUsesOnlyItsOwnAddressesAndNeverGlobalTerminate()
    {
        await using var f = new Fixture();
        await using var csv = new CsvPickTaskRunner(f.Engine, new PreemptionGate(), NullLogger<CsvPickTaskRunner>.Instance);
        var file = Path.Combine(f.DirectoryPath, "tasks.csv");
        File.WriteAllText(file, "0001,1,Green\n");
        var lit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        csv.TaskEvent += e => { if (e.EventType == "task_started") lit.TrySetResult(); if (e.EventType == "csv_completed") done.TrySetResult(); };
        csv.Start(file, LightColor.Green);
        await lit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.StartBatchAsync([Item("WMS", "0002")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        f.Confirm(1);
        await f.Engine.StartBatchAsync([Item("WMS", "0002")]);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain("D00", f.Commands);
        Assert.Contains("D0001", f.Commands);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0002")!.Status);
        Assert.Equal("WMS", f.Registry.Get("0002")!.CommandItemId);
    }

    [Fact]
    public async Task ConfigureEncodesRequestedModesBeforeDisplayAndRejectsInvalidQuantity()
    {
        await using var f = new Fixture();
        var parameters = new ModuleParameters { Color = LightColor.Red, LedMode = LightState.Flash, SegMode = LightState.Off, BuzMode = LightState.On };
        await f.Engine.ConfigureModuleAsync("0001", 7, parameters);
        var commands = f.Commands.ToArray();
        var (led2, led1, led0) = parameters.Color.ToLedStates(parameters.LedMode);
        Assert.Equal(AioiEncoder.SetModeAm1("0001", led2, led1, led0, parameters.SegMode, parameters.BuzMode), commands[0]);
        Assert.StartsWith("Am2", commands[1]);
        Assert.Equal("L000100007", commands[2]);
        Assert.Equal(parameters.SegMode, f.Registry.Get("0001")!.SegMode);
        await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.ConfigureModuleAsync("0001", -1, parameters));
        Assert.Equal(3, f.Commands.Count);
    }

    [Fact]
    public async Task InterruptWindowAllowsNormalSequenceWrapAndExpiredReuse()
    {
        await using var f = new Fixture();
        for (var i = 0; i <= 1000; i++)
        {
            await f.Engine.StartBatchAsync([Item("item-" + i)]);
            f.Confirm(i % 1000);
            Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        }
        await using var expired = new Fixture();
        await expired.Engine.StartBatchAsync([Item("old")]);
        expired.Confirm(7, receivedAt: DateTime.UtcNow.AddSeconds(-31));
        await expired.Engine.StartBatchAsync([Item("new")]);
        expired.Confirm(7);
        Assert.Equal(ModuleStatus.Completed, expired.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task ConflictingSequenceInsideWindowDoesNotConfirmAnotherOwner()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("A")]);
        f.Confirm(1);
        await f.Engine.StartBatchAsync([Item("B", "0002")]);
        f.Confirm(1, "0002");
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0002")!.Status);
        f.Confirm(2, "0002");
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0002")!.Status);
    }

    [Fact]
    public async Task LateConfirmWithoutDurableConsumerRetainsPendingDespiteOrdinaryObserver()
    {
        await using var f = new Fixture(persist: true);
        f.Engine.RequireDurableConfirmationConsumer();
        var observed = 0;
        f.Engine.ItemConfirmed += (_, _) => observed++;
        await f.Engine.StartBatchAsync([Item("A")]);
        f.Confirm(1);
        Assert.Equal(0, observed);
        Assert.True(f.Engine.HasActiveWork);
        var published = new List<string?>();
        f.Engine.DurableConfirmation += (_, state) => published.Add(state!.CommandItemId);
        f.Engine.ReplayPendingConfirmations();
        Assert.Equal(new[] { "A" }, published);
        Assert.Equal(1, observed);
        Assert.False(f.Engine.HasActiveWork);
    }

    [Fact]
    public async Task ConcurrentDurableMutationAndSnapshotFlushDoesNotDeadlock()
    {
        await using var f = new Fixture(persist: true);
        var writes = Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
                f.Registry.SetInstructed(Item("A"));
        });
        var flushes = Task.Run(() =>
        {
            var persistence = typeof(Fixture).GetField("_persistence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f)!;
            var save = typeof(RegistryPersistenceService).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic)!;
            for (var i = 0; i < 20; i++)
                save.Invoke(persistence, [true]);
        });
        await Task.WhenAll(writes, flushes).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WholeBatchIsDurableBeforeAnyStateObserverRuns()
    {
        await using var f = new Fixture(persist: true);
        var items = Enumerable.Range(1, 110).Select(i => Item("item-" + i, i.ToString("D4"))).ToArray();
        var observerCounts = new List<int>();
        f.Registry.StateChanged += _ =>
        {
            using var document = JsonDocument.Parse(File.ReadAllText(f.StatePath));
            observerCounts.Add(document.RootElement.GetProperty("modules").GetArrayLength());
        };
        f.Registry.SetInstructedBatch(items);
        Assert.Equal(110, observerCounts.Count);
        Assert.All(observerCounts, count => Assert.Equal(110, count));
    }

    [Fact]
    public async Task BatchedStateWritePreservesOneThousandCompletedGuards()
    {
        await using var f = new Fixture();
        File.WriteAllText(f.StatePath, JsonSerializer.Serialize(new
        {
            version = 2,
            modules = Array.Empty<object>(),
            completedItems = Enumerable.Range(0, 1000).Select(i => new
            {
                correlationId = "history", commandItemId = "done-" + i,
                pickingDetailId = "detail-done-" + i, completedAt = DateTime.UtcNow
            }),
            interrupts = Array.Empty<object>()
        }));
        using var persistence = new RegistryPersistenceService(f.Registry, NullLogger<RegistryPersistenceService>.Instance, f.StatePath);
        persistence.Load();
        var items = Enumerable.Range(1, 110).Select(i => Item("item-" + i, i.ToString("D4"))).ToArray();
        var watch = Stopwatch.StartNew();
        f.Registry.SetInstructedBatch(items);
        watch.Stop();
        using var saved = JsonDocument.Parse(File.ReadAllText(f.StatePath));
        Assert.Equal(110, saved.RootElement.GetProperty("modules").GetArrayLength());
        Assert.Equal(1000, saved.RootElement.GetProperty("completedItems").GetArrayLength());
        _output.WriteLine("110-item atomic snapshot with 1000 completed guards: {0:F2} ms; {1} bytes", watch.Elapsed.TotalMilliseconds, new FileInfo(f.StatePath).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfirmDuringModeSetupIsRemovedAtFinalWrite(bool relight)
    {
        await using var f = new Fixture(persist: true);
        await f.Engine.StartBatchAsync([Item("A")]);
        f.AutoAck = false;
        var update = relight ? f.Engine.StartBatchAsync([Item("A")], relight: true)
            : f.Engine.QuickLightAsync("0002", 2, blockNo: "01", correlationId: "session", commandItemId: "B");
        var mode = await f.NextAsync();
        f.Confirm(17);
        f.Reply(mode, "o");
        if (!relight) f.Reply(await f.NextAsync(), "o");
        object? entry;
        using var wait = new CancellationTokenSource(2000);
        while ((entry = f.TakeQueued()) is null) await Task.Delay(1, wait.Token);
        var final = f.Evaluate(entry);
        if (relight) Assert.Null(final);
        else
        {
            Assert.Equal("P101000200002", final!.Command);
            f.Reply(final, "o");
        }
        await update;
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task ConfirmAfterFinalEnqueueIsRemovedBeforeActualWriteAndFallbackRechecks()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("A")]);
        f.AutoAck = false;
        var update = f.Engine.QuickLightAsync("0002", 2, blockNo: "01", correlationId: "session", commandItemId: "B");
        f.Reply(await f.NextAsync(), "o");
        f.Reply(await f.NextAsync(), "o");
        object? entry;
        using var wait = new CancellationTokenSource(2000);
        while ((entry = f.TakeQueued()) is null) await Task.Delay(1, wait.Token);
        f.Confirm(21);
        var final = f.Evaluate(entry)!;
        Assert.Equal("P101000200002", final.Command);
        f.Reply(final, "n");
        f.Reply(await f.NextAsync(), "o");
        f.Reply(await f.NextAsync(), "o");
        var fallback = await f.NextAsync();
        Assert.Equal("PP5050001000200002", fallback.Command);
        f.Reply(fallback, "o");
        await update;
    }

    [Fact]
    public async Task NormalBatchCannotReopenCompletedIdentity()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("A")]);
        f.Confirm(3);
        var count = f.Commands.Count;
        var error = await Assert.ThrowsAsync<PickingCommandRejectedException>(() => f.Engine.StartBatchAsync([Item("A")]));
        Assert.Equal("ITEM_COMPLETED", error.Code);
        Assert.Equal(count, f.Commands.Count);
    }

    [Fact]
    public async Task WholeOperationBudgetStopsFurtherSetupFramesWithoutBlindRetry()
    {
        await using var gateway = new AioiGateway("unused.invalid", 1, NullLogger.Instance);
        var registry = new LightModuleRegistry();
        using var engine = new PickingEngine(gateway, registry, NullLogger<PickingEngine>.Instance, executionTimeoutMs: 40);
        await Assert.ThrowsAsync<TimeoutException>(() => engine.StartBatchAsync([Item("A"), Item("B", "0002")]));
        Assert.True(engine.HasActiveWork);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartBatchAsync([Item("A"), Item("B", "0002")]));
        Assert.Contains("uncertain", error.Message);
    }

    [Fact]
    public async Task BatchCompletionFailureSurvivesRestartAndObserverFailureDoesNotBlockOthers()
    {
        string snapshot;
        string id = "";
        await using (var first = new Fixture(persist: true))
        {
            await first.Engine.StartBatchAsync([Item("A")]);
            first.Engine.DurableBatchCompleted += evt => { id = evt.EventId; throw new IOException("batch outbox failed"); };
            int notified = 0;
            first.Engine.BatchCompleted += _ => throw new IOException("ordinary observer failed");
            first.Engine.BatchCompleted += _ => notified++;
            first.Confirm(9);
            Assert.Equal(1, notified);
            snapshot = File.ReadAllText(first.StatePath);
            using var doc = JsonDocument.Parse(snapshot);
            Assert.Equal(id, doc.RootElement.GetProperty("pendingBatchCompletions")[0].GetProperty("eventId").GetString());
        }
        await using var second = new Fixture();
        File.WriteAllText(second.StatePath, snapshot);
        using var persistence = new RegistryPersistenceService(second.Registry, NullLogger<RegistryPersistenceService>.Instance, second.StatePath);
        persistence.Load();
        second.Engine.DurableConfirmation += (_, _) => { };
        var delivered = new List<string>();
        second.Engine.DurableBatchCompleted += evt => delivered.Add(evt.EventId);
        second.Engine.ReplayPendingConfirmations();
        second.Engine.ReplayPendingConfirmations();
        Assert.Equal(new[] { id }, delivered);
        Assert.False(second.Engine.HasActiveWork);
    }

    [Fact]
    public async Task CsvPickedRowsAreCheckpointedBeforeCleanupNackAndDoNotRelightOnRestart()
    {
        await using var f = new Fixture(persist: true);
        var path = Path.Combine(f.DirectoryPath, "job.csv");
        File.WriteAllText(path, "0001,1,Green\n");
        await using var csv = new CsvPickTaskRunner(f.Engine, new PreemptionGate(), NullLogger<CsvPickTaskRunner>.Instance);
        var lit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        csv.TaskEvent += evt => { if (evt.EventType == "task_started") lit.TrySetResult(); if (evt.EventType == "task_error") failed.TrySetResult(); };
        csv.Start(path, LightColor.Green);
        await lit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        f.AutoAck = false;
        f.Confirm(32);
        var cleanup = await f.NextAsync();
        Assert.Equal("D0001", cleanup.Command);
        Assert.DoesNotContain("0001,1", File.ReadAllText(path));
        f.Reply(cleanup, "n");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        while (csv.IsRunning) await Task.Delay(1);
        var count = f.Commands.Count;
        csv.Start(path, LightColor.Green);
        while (csv.IsRunning) await Task.Delay(1);
        Assert.Equal(count, f.Commands.Count);
    }

    [Fact]
    public async Task BatchCompletionRawRetryUsesTheSameDurableEventId()
    {
        await using var f = new Fixture(persist: true);
        await f.Engine.StartBatchAsync([Item("A")]);
        var delivered = new List<string>();
        f.Engine.DurableBatchCompleted += evt =>
        {
            delivered.Add(evt.EventId);
            if (delivered.Count == 1) throw new IOException("first batch append failed");
        };
        f.Confirm(11);
        Assert.True(f.Engine.HasActiveWork);
        f.Confirm(11);
        Assert.Equal(2, delivered.Count);
        Assert.Equal(delivered[0], delivered[1]);
        Assert.False(f.Engine.HasActiveWork);
    }

    [Fact]
    public async Task CsvPartialConfirmThenSetupNackRerunsOnlyRemainingRows()
    {
        await using var f = new Fixture(persist: true);
        f.AutoAck = false;
        var path = Path.Combine(f.DirectoryPath, "partial.csv");
        File.WriteAllText(path, "0001,1,Green\n0002,2,Green\n");
        await using var csv = new CsvPickTaskRunner(f.Engine, new PreemptionGate(), NullLogger<CsvPickTaskRunner>.Instance);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        csv.TaskEvent += evt => { if (evt.EventType == "task_error") error.TrySetResult(); };
        csv.Start(path, LightColor.Green);
        var modeA = await f.NextAsync();
        f.Confirm(51);
        f.Reply(modeA, "o");
        f.Reply(await f.NextAsync(), "n");
        await error.Task.WaitAsync(TimeSpan.FromSeconds(2));
        while (csv.IsRunning) await Task.Delay(1);
        Assert.DoesNotContain("0001,1", File.ReadAllText(path));
        Assert.Contains("0002,2", File.ReadAllText(path));
        var before = f.Commands.Count;
        f.AutoAck = true;
        var lit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        csv.TaskEvent += evt => { if (evt.EventType == "task_started") lit.TrySetResult(); if (evt.EventType == "csv_completed") done.TrySetResult(); };
        csv.Start(path, LightColor.Green);
        await lit.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(f.Commands.Skip(before), command => command.Contains("0001", StringComparison.Ordinal));
        Assert.Contains("PP5050000000200002", f.Commands.Skip(before));
        f.Confirm(52, "0002");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CompletedCsvFileCanStartANewJobWithTheSameAddress()
    {
        await using var f = new Fixture(persist: true);
        var path = Path.Combine(f.DirectoryPath, "reused.csv");
        await using var csv = new CsvPickTaskRunner(f.Engine, new PreemptionGate(), NullLogger<CsvPickTaskRunner>.Instance);
        for (int run = 0; run < 2; run++)
        {
            File.WriteAllText(path, "0001,1,Green\n");
            var lit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Observe(CsvTaskEvent evt) { if (evt.EventType == "task_started") lit.TrySetResult(); if (evt.EventType == "csv_completed") done.TrySetResult(); }
            csv.TaskEvent += Observe;
            csv.Start(path, LightColor.Green);
            await lit.Task.WaitAsync(TimeSpan.FromSeconds(2));
            f.Confirm(60 + run);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
            while (csv.IsRunning) await Task.Delay(1);
            csv.TaskEvent -= Observe;
            Assert.DoesNotContain("p2l-job:", File.ReadAllText(path));
        }
    }

    private static PickingInstruction Item(string id, string address = "0001", string session = "session") => new()
    {
        Address = address,
        Quantity = 1,
        BlockNo = "01",
        CorrelationId = session,
        CommandItemId = id,
        PickingDetailId = "detail-" + id
    };

    private sealed class Fixture : IAsyncDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        public AioiGateway Gateway { get; } = new("unused.invalid", 1, NullLogger.Instance);
        public LightModuleRegistry Registry { get; } = new();
        public PickingEngine Engine
        {
            get;
        }
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "p2l-reliability-" + Guid.NewGuid().ToString("N"));
        public string StatePath => Path.Combine(DirectoryPath, "state.json");
        public ConcurrentQueue<string> Commands { get; } = new();
        public volatile bool AutoAck = true;
        public volatile bool Reject;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _ackLoop;
        private readonly RegistryPersistenceService? _persistence;
        public Fixture(bool persist = false)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (persist)
            {
                _persistence = new RegistryPersistenceService(Registry, NullLogger<RegistryPersistenceService>.Instance, StatePath);
                _persistence.Load();
            }
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance);
            Engine.DurableBatchCompleted += _ => { };
            _ackLoop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    if (AutoAck && Read() is { } packet)
                        Reply(packet, Reject ? "n" : packet.Command.StartsWith("Az", StringComparison.Ordinal) ? "a" + packet.Command[2..] + "3" : "o");
                    else
                        await Task.Delay(1);
                }
            });
        }
        public AioiPacket? Read()
        {
            var transport = typeof(AioiGateway).GetField("_transport", Private)!.GetValue(Gateway)!;
            var queue = transport.GetType().GetField("_sendQueue", Private)!.GetValue(transport)!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            object?[] args = [null];
            if (!(bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, args)!)
                return null;
            return Evaluate(args[0]!);
        }
        public object? TakeQueued()
        {
            var transport = typeof(AioiGateway).GetField("_transport", Private)!.GetValue(Gateway)!;
            var queue = transport.GetType().GetField("_sendQueue", Private)!.GetValue(transport)!;
            var reader = queue.GetType().GetProperty("Reader")!.GetValue(queue)!;
            object?[] args = [null];
            return (bool)reader.GetType().GetMethod("TryRead")!.Invoke(reader, args)! ? args[0] : null;
        }
        public AioiPacket? Evaluate(object entry)
        {
            var packet = (AioiPacket)entry.GetType().GetProperty("Packet")!.GetValue(entry)!;
            if (entry.GetType().GetProperty("WriteCurrent")!.GetValue(entry) is Func<Func<string?, ValueTask>, ValueTask> current)
            {
                string? command = null;
                current(value => { command = value; return ValueTask.CompletedTask; }).GetAwaiter().GetResult();
                if (command is null)
                {
                    ((Action?)entry.GetType().GetProperty("Skipped")!.GetValue(entry))?.Invoke();
                    return null;
                }
                packet = new AioiPacket { SequenceNo = packet.SequenceNo, Command = command };
            }
            Commands.Enqueue(packet.Command);
            return packet;
        }
        public async Task<AioiPacket> NextAsync()
        {
            using var timeout = new CancellationTokenSource(5000);
            while (true)
            {
                if (Read() is { } packet)
                    return packet;
                await Task.Delay(1, timeout.Token);
            }
        }
        public void Reply(AioiPacket packet, string command) => Receive(packet.SequenceNo, command);
        public void Confirm(int sequence, string address = "0001", DateTime? receivedAt = null)
        {
            if (receivedAt is null)
                Receive(sequence, "t" + address + "00");
            else
                ((Action<ConfirmationEvent>)typeof(AioiGateway).GetField("Confirmed", Private)!.GetValue(Gateway)!)
                .Invoke(new ConfirmationEvent { Address = address, SequenceNo = sequence, ReceivedAt = receivedAt.Value });
        }
        private void Receive(int sequence, string command) => typeof(AioiGateway).GetMethod("OnPacketReceived", Private)!
            .Invoke(Gateway, [new AioiPacket { SequenceNo = sequence, Command = command }]);
        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            await _ackLoop;
            _cts.Dispose();
            Engine.Dispose();
            _persistence?.Dispose();
            await Gateway.DisposeAsync();
            Directory.Delete(DirectoryPath, true);
        }
    }
}
