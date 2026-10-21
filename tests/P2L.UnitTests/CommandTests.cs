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
    public async Task SessionReleaseReceiptAndDelayedIntentNeverStopNewOwner()
    {
        await using var f = new Fixture();
        using var dispatcher = f.Dispatcher();
        await f.Engine.StartBatchAsync([Item("0001", "01", "old", "old-item")]);
        var command = new ReleaseSessionCmd
        {
            Type = "release_session", CommandId = "close-old", CorrelationId = "old", BlockNo = "01",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, P2LMqttJsonContext.Default.ReleaseSessionCmd);
        await dispatcher.Value.HandleAsync(bytes, CancellationToken.None);
        Assert.Equal("applied", dispatcher.Results().Last().Status);
        Assert.Single(f.Commands(), command => command == "D0001");
        await f.Engine.StartBatchAsync([Item("0002", "01", "new", "new-item")]);
        var before = f.Commands().Count;
        await dispatcher.Value.HandleAsync(bytes, CancellationToken.None);
        await dispatcher.Value.HandleAsync(JsonSerializer.SerializeToUtf8Bytes(command with { CommandId = "late-close-old" },
            P2LMqttJsonContext.Default.ReleaseSessionCmd), CancellationToken.None);
        Assert.Equal(before, f.Commands().Count);
        Assert.Equal("new", f.Registry.Get("0002")!.CorrelationId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0002")!.Status);
        var lateStart = Encoding.UTF8.GetBytes("""{"type":"batch_start","commandId":"late-old-start","expiresAt":"2099-01-01T00:00:00Z","blockNo":"01","correlationId":"old","items":[{"address":"0001","quantity":1,"commandItemId":"old-item"}]}""");
        await dispatcher.Value.HandleAsync(lateStart, CancellationToken.None);
        Assert.Equal("SESSION_CLOSED", dispatcher.Results().Last().ErrorCode);
        Assert.Equal(before, f.Commands().Count);
    }

    [Fact]
    public async Task UnidentifiedSessionReleaseCannotTouchHardware()
    {
        await using var f = new Fixture();
        using var dispatcher = f.Dispatcher();
        await f.Engine.StartBatchAsync([Item("0001", "01", "active", "item")]);
        var before = f.Commands().Count;
        await dispatcher.Value.HandleAsync(Encoding.UTF8.GetBytes("""{"type":"release_session","blockNo":"01","correlationId":"active"}"""), CancellationToken.None);
        Assert.Equal("INVALID_RELEASE", Assert.Single(dispatcher.Results()).ErrorCode);
        Assert.Equal(before, f.Commands().Count);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task RecoveryCommandPublishesOneDurableConfirmationAndReplaysItsReceipt()
    {
        await using var f = new Fixture();
        using var dispatcher = f.Dispatcher();
        await f.Engine.QuickLightAsync("0001", 5, blockNo: "01", correlationId: "manual:test",
            commandItemId: "item", pickingDetailId: "detail");
        typeof(LightModuleRegistry).GetMethod("SetConfirmationMissing", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(f.Registry, [f.Registry.Get("0001"), ModuleStatus.Standby]);
        f.Engine.ReplayPendingConfirmations();
        var command = new ResolveConfirmationCmd
        {
            Type = "resolve_confirmation", CommandId = "operator-intent", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1),
            Address = "0001", BlockNo = "01", CorrelationId = "manual:test", CommandItemId = "item", PickingDetailId = "detail",
            MissingEventId = f.Registry.Get("0001")!.ConfirmationMissingEventId!, Action = "confirm", ActualQuantity = 0,
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(command, P2LMqttJsonContext.Default.ResolveConfirmationCmd);
        await dispatcher.Value.HandleAsync(payload, CancellationToken.None);
        var beforeRetry = f.Commands().Count;
        await dispatcher.Value.HandleAsync(payload, CancellationToken.None);
        Assert.Equal(beforeRetry, f.Commands().Count);
        Assert.All(dispatcher.Results(), result => Assert.Equal("applied", result.Status));
        var events = dispatcher.Payloads().Select(bytes => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(bytes)!).ToArray();
        var missing = Assert.Single(events, e => e["type"].GetString() == "light_feedback_missing");
        Assert.DoesNotContain("confirm", missing["type"].GetString()!);
        Assert.Equal(command.MissingEventId, missing["eventId"].GetString());
        Assert.Equal("item", missing["commandItemId"].GetString());
        var confirmed = Assert.Single(events, e => e["type"].GetString() == "confirmed");
        Assert.Equal("operator-intent", confirmed["eventId"].GetString());
        Assert.Equal("manual_recovery", confirmed["source"].GetString());
        Assert.Equal(0, confirmed["adjustedQuantity"].GetInt32());
        Assert.Equal("detail", confirmed["pickingDetailId"].GetString());
        Assert.Null(f.Registry.Get("0001")!.ConfirmationMissingAt);
    }

    [Fact]
    public async Task RecoveryCommandWithStaleWarningIsRejectedBeforeHardwareIo()
    {
        await using var f = new Fixture();
        using var dispatcher = f.Dispatcher();
        var payload = Encoding.UTF8.GetBytes("""{"type":"resolve_confirmation","commandId":"stale-recovery","expiresAt":"2099-01-01T00:00:00Z","address":"0001","blockNo":"01","correlationId":"manual:old","commandItemId":"old-item","missingEventId":"old-warning","action":"retry"}""");
        await dispatcher.Value.HandleAsync(payload, CancellationToken.None);
        var result = Assert.Single(dispatcher.Results());
        Assert.Equal("rejected", result.Status);
        Assert.Equal("RECOVERY_STALE", result.ErrorCode);
        Assert.Empty(f.Commands());
    }

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
        var overwrite = f.Commands().Last(c => c.StartsWith("PP50500"));
        Assert.DoesNotContain("000100001", overwrite);
        Assert.Contains("000200001", overwrite);
        // WMS captured A/B before the operator confirmed A: completed A must
        // never relight, while B continues without rejecting the whole session.
        await f.Engine.StartBatchAsync(original, relight: true);
        overwrite = f.Commands().Last(c => c.StartsWith("PP50500"));
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
    public async Task FailedDurableSubscriberLeavesReplayablePendingConfirmation()
    {
        await using var f = new Fixture();
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        Action<ConfirmationEvent, LightModuleState?> reject = (_, _) => throw new IOException("disk full");
        f.Engine.DurableConfirmation += reject;
        Assert.Throws<IOException>(() => f.Confirm("0001"));
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.True(f.Engine.IsBatchActive);
        f.Engine.DurableConfirmation -= reject;
        f.Confirm("0001", 1);
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
        Assert.True(f.Engine.IsBatchActive);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        f.RejectCommands = false;
        await f.Engine.ExtinguishAsync("0001");
        Assert.False(f.Engine.IsBatchActive);
    }

    [Fact]
    public async Task ExtinguishOneAddressKeepsRemainingOwnerAndOtherBlockWithoutConfirmingItems()
    {
        await using var f = new Fixture();
        int confirmed = 0, completed = 0;
        f.Engine.ItemConfirmed += (_, _) => confirmed++;
        f.Engine.BatchCompleted += _ => completed++;
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one"), Item("0002", "01", "session", "two")]);
        await f.Engine.StartBatchAsync([Item("0003", "02", "other-session", "three")]);

        await f.Engine.ExtinguishAsync("1");

        Assert.Contains("D0001", f.Commands());
        Assert.Equal(ModuleStatus.Standby, f.Registry.Get("0001")!.Status);
        Assert.Null(f.Registry.Get("0001")!.CommandItemId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0002")!.Status);
        Assert.Equal("two", f.Registry.Get("0002")!.CommandItemId);
        Assert.Equal("other-session", f.Registry.Get("0003")!.CorrelationId);
        Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0003")!.Status);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.StartBatchAsync([Item("0004", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);

        await f.Engine.ExtinguishAsync("0002");
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
        Assert.Equal("next-session", f.Registry.Get("0001")!.CorrelationId);
        Assert.Equal("other-session", f.Registry.Get("0003")!.CorrelationId);
        Assert.Equal(0, confirmed);
        Assert.Equal(0, completed);
    }

    [Theory]
    [InlineData("00")]
    [InlineData("01")]
    public async Task ExtinguishLastManualLightReleasesBlockForANewOwner(string block)
    {
        await using var f = new Fixture();
        int confirmed = 0, completed = 0;
        f.Engine.ItemConfirmed += (_, _) => confirmed++;
        f.Engine.BatchCompleted += _ => completed++;
        await f.Engine.QuickLightAsync("0001", 1, blockNo: block, correlationId: "manual-first");

        await f.Engine.ExtinguishAsync("0001");

        Assert.False(f.Engine.IsBatchActive);
        Assert.False(f.Engine.HasActiveWork);
        await f.Engine.QuickLightAsync("0001", 2, blockNo: block, correlationId: "manual-next");
        Assert.Equal("manual-next", f.Registry.Get("0001")!.CorrelationId);
        Assert.Equal("00002", f.Registry.Get("0001")!.DisplayData);
        Assert.Equal(0, confirmed);
        Assert.Equal(0, completed);
    }

    [Fact]
    public async Task ExtinguishPersistenceFailureRetainsOwnerUntilSuccessfulRetry()
    {
        await using var f = new Fixture();
        var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(f.Path)!, "state.json");
        using var persistence = new RegistryPersistenceService(f.Registry, NullLogger<RegistryPersistenceService>.Instance, path);
        persistence.Load();
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        Directory.CreateDirectory(path + ".tmp");
        try
        {
            var error = await Record.ExceptionAsync(() => f.Engine.ExtinguishAsync("0001"));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Contains("D0001", f.Commands());
            Assert.Equal(ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
            Assert.Equal("one", f.Registry.Get("0001")!.CommandItemId);
            var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
                f.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
            Assert.Equal("BLOCK_BUSY", busy.Code);
        }
        finally { Directory.Delete(path + ".tmp"); }

        await f.Engine.ExtinguishAsync("0001");
        Assert.False(f.Engine.HasActiveWork);
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
    }

    [Fact]
    public async Task ExtinguishKeepsPendingConfirmationOwnerUntilDurableReplay()
    {
        await using var f = new Fixture();
        var eventIds = new List<string?>();
        int confirmed = 0, completed = 0;
        f.Engine.ItemConfirmed += (_, _) => confirmed++;
        f.Engine.BatchCompleted += _ => completed++;
        Action<ConfirmationEvent, LightModuleState?> reject = (evt, _) =>
        {
            eventIds.Add(evt.EventId);
            throw new IOException("outbox unavailable");
        };
        f.Engine.DurableConfirmation += reject;
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        Assert.Throws<IOException>(() => f.Confirm("0001"));

        await f.Engine.ExtinguishAsync("0001");

        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.Equal("one", f.Registry.Get("0001")!.CommandItemId);
        Assert.True(f.Engine.IsBatchActive);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        Assert.Equal(0, confirmed);
        Assert.Equal(0, completed);

        f.Engine.DurableConfirmation -= reject;
        f.Engine.DurableConfirmation += (evt, _) => eventIds.Add(evt.EventId);
        f.Engine.ReplayPendingConfirmations();
        Assert.Equal(eventIds[0], eventIds[1]);
        Assert.Equal(1, confirmed);
        Assert.Equal(1, completed);
        Assert.False(f.Engine.IsBatchActive);
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
    }

    [Fact]
    public async Task ExtinguishKeepsPendingBatchCompletionOwnerUntilDurableReplay()
    {
        await using var f = new Fixture();
        var eventIds = new List<string>();
        int confirmed = 0;
        f.Engine.ItemConfirmed += (_, _) => confirmed++;
        Action<BatchCompletionEvent> reject = evt =>
        {
            eventIds.Add(evt.EventId);
            throw new IOException("batch outbox unavailable");
        };
        f.Engine.DurableBatchCompleted += reject;
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        Assert.Throws<IOException>(() => f.Confirm("0001"));

        await f.Engine.ExtinguishAsync("0001");

        Assert.True(f.Engine.IsBatchActive);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        Assert.Equal(1, confirmed);

        f.Engine.DurableBatchCompleted -= reject;
        f.Engine.DurableBatchCompleted += evt => eventIds.Add(evt.EventId);
        f.Engine.ReplayPendingConfirmations();
        Assert.Equal(eventIds[0], eventIds[1]);
        Assert.Equal(1, confirmed);
        Assert.False(f.Engine.IsBatchActive);
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
    }

    [Fact]
    public async Task ConfirmationWhileExtinguishAwaitsAckPreservesCompletedIdentity()
    {
        await using var f = new Fixture();
        var confirmed = new List<string?>();
        f.Engine.ItemConfirmed += (_, state) => confirmed.Add(state?.CommandItemId);
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        f.BeforeNextAck(() => f.Confirm("0001"));

        await f.Engine.ExtinguishAsync("0001");

        Assert.Equal(new[] { "one" }, confirmed);
        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.Equal("one", f.Registry.Get("0001")!.CommandItemId);
        Assert.False(f.Engine.IsBatchActive);
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
    }

    [Fact]
    public async Task RestartRetainsCompletionOnlyOwnerUntilDurableReplay()
    {
        string snapshot;
        string? completionId = null;
        await using (var first = new Fixture())
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(first.Path)!, "state.json");
            using var persistence = new RegistryPersistenceService(first.Registry, NullLogger<RegistryPersistenceService>.Instance, path);
            persistence.Load();
            first.Engine.DurableBatchCompleted += evt =>
            {
                completionId = evt.EventId;
                throw new IOException("batch outbox unavailable");
            };
            await first.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
            Assert.Throws<IOException>(() => first.Confirm("0001"));
            await first.Engine.ExtinguishAsync("0001");
            snapshot = File.ReadAllText(path);
        }

        await using var restored = new Fixture();
        var restoredPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(restored.Path)!, "state.json");
        File.WriteAllText(restoredPath, snapshot);
        using var reader = new RegistryPersistenceService(restored.Registry, NullLogger<RegistryPersistenceService>.Instance, restoredPath);
        reader.Load();
        var delivered = new List<string>();
        restored.Engine.DurableBatchCompleted += evt => delivered.Add(evt.EventId);

        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            restored.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);
        Assert.Empty(restored.Commands());
        restored.Engine.ReplayPendingConfirmations();
        Assert.Equal(new[] { completionId }, delivered);
        Assert.False(restored.Engine.IsBatchActive);
        await restored.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
    }

    [Fact]
    public async Task ConfirmationAppendFailureWhileExtinguishAwaitsAckRetainsOwnerForReplay()
    {
        await using var f = new Fixture();
        Action<ConfirmationEvent, LightModuleState?> reject = (_, _) => throw new IOException("outbox unavailable");
        f.Engine.DurableConfirmation += reject;
        await f.Engine.StartBatchAsync([Item("0001", "01", "session", "one")]);
        f.BeforeNextAck(() => Assert.Throws<IOException>(() => f.Confirm("0001")));

        await f.Engine.ExtinguishAsync("0001");

        Assert.Equal(ModuleStatus.Completed, f.Registry.Get("0001")!.Status);
        Assert.Equal("one", f.Registry.Get("0001")!.CommandItemId);
        var busy = await Assert.ThrowsAsync<PickingCommandRejectedException>(() =>
            f.Engine.StartBatchAsync([Item("0002", "01", "next-session", "next")]));
        Assert.Equal("BLOCK_BUSY", busy.Code);

        f.Engine.DurableConfirmation -= reject;
        f.Engine.ReplayPendingConfirmations();
        Assert.False(f.Engine.IsBatchActive);
        await f.Engine.StartBatchAsync([Item("0001", "01", "next-session", "next")]);
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
        var command = f.Commands().Last(c => c.StartsWith("PP50500"));
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
        private int _interruptSequence;
        private Action? _beforeAck;
        public void BeforeNextAck(Action action) => _beforeAck = action;
        public Fixture()
        {
            Engine = new PickingEngine(Gateway, Registry, NullLogger<PickingEngine>.Instance);
            Engine.DurableConfirmation += (_, _) => { }; // In-process durable-sink fixture; never connects.
            Engine.DurableBatchCompleted += _ => { };
            // No StartAsync: no TCP connection. ACK only the in-memory pending map.
            _ackLoop = Task.Run(async () =>
            {
                var pending = (ConcurrentDictionary<int, TaskCompletionSource<AioiDecoder.ParsedResponse>>)typeof(AioiGateway).GetField("_pending", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gateway)!;
                while (!_cts.IsCancellationRequested)
                {
                    foreach (var pair in pending)
                    {
                        try { Interlocked.Exchange(ref _beforeAck, null)?.Invoke(); }
                        catch (Exception ex) { pair.Value.TrySetException(ex); continue; }
                        pair.Value.TrySetResult(new AioiDecoder.ParsedResponse { Kind = RejectCommands ? AioiDecoder.ResponseKind.Nack : AioiDecoder.ResponseKind.Ack, Raw = RejectCommands ? "n" : "o" });
                    }
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
        public void Confirm(string address, int? sequence = null)
        {
            var handlers = (Action<ConfirmationEvent>?)typeof(AioiGateway).GetField("Confirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Gateway);
            handlers?.Invoke(new ConfirmationEvent { Address = address, SequenceNo = sequence ?? Interlocked.Increment(ref _interruptSequence) % 1000 });
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
        public byte[][] Payloads() => new DurableEventBuffer(_outbox).ReadBatch()
            .Select(line => Convert.FromBase64String(DurableEventBuffer.Parse(line).Payload)).ToArray();
        public CommandResultEvt[] Results() => Payloads()
            .Where(payload =>
            {
                using var json = JsonDocument.Parse(payload);
                return json.RootElement.GetProperty("type").GetString() == "command_result";
            })
            .Select(payload => JsonSerializer.Deserialize(payload, P2LMqttJsonContext.Default.CommandResultEvt)!).ToArray();
        public void Dispose() { _publisher.DisposeAsync().AsTask().GetAwaiter().GetResult(); _client.Dispose(); }
    }
}
