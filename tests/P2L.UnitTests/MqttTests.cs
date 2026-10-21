using System.Reflection;
using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using MQTTnet.Formatter;
using P2L.Mqtt;
using P2L.Core;
using P2L.Core.Services;
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
        var entries = new DurableEventBuffer(f.Path).ReadBatch().Select(DurableEventBuffer.Parse).ToArray();
        var entry = Assert.Single(entries, e =>
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(e.Payload));
            return json.RootElement.GetProperty("type").GetString() == "confirmed";
        });
        Assert.Single(entries, e =>
        {
            using var json = JsonDocument.Parse(Convert.FromBase64String(e.Payload));
            return json.RootElement.GetProperty("type").GetString() == "batch_completed";
        });
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
        var offline = new OfflineBufferService(new MqttSettings { OfflineBufferPath = f.Path }, f.Engine, f.Registry,
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

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"type\":4}")]
    [InlineData("{\"type\":\"confirmed\",\"eventId\":4}")]
    public async Task InvalidInnerPayloadIsQuarantinedAndValidTailProgresses(string inner)
    {
        using var f = new Fixture();
        File.WriteAllText(f.Path, JsonSerializer.Serialize(new { topic = "p2l/events", retain = false,
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner)) }) + "\n");
        f.Enqueue("valid-tail");
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(1, f.Client.Attempts);
        Assert.Empty(File.ReadAllLines(f.Path));
        Assert.Single(File.ReadAllLines(f.Path + ".invalid"));
    }

    [Fact]
    public async Task LegacyDeniedTelemetryCannotBlockBusinessTail()
    {
        using var f = new Fixture();
        new DurableEventBuffer(f.Path).Append("p2l/devices/0001", true, Encoding.UTF8.GetBytes("{\"type\":\"light_changed\"}"));
        f.Enqueue("business");
        f.Client.Handler = (message, ct) => Task.FromResult(new MqttClientPublishResult(1,
            message.Retain ? MqttClientPublishReasonCode.NotAuthorized : MqttClientPublishReasonCode.Success, null, []));
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(1, f.Client.Attempts);
        Assert.Empty(File.ReadAllLines(f.Path));
        using var stop = new CancellationTokenSource();
        var businessDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Handler = (message, ct) => {
            if (!message.Retain) businessDelivered.TrySetResult();
            return Task.FromResult(new MqttClientPublishResult(1,
                message.Retain ? MqttClientPublishReasonCode.NotAuthorized : MqttClientPublishReasonCode.Success, null, []));
        };
        var drain = f.Publisher.DrainAsync(stop.Token);
        f.Enqueue("next-business");
        await businessDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel(); await drain;
    }

    [Fact]
    public async Task OfflineTelemetryIsCoalescedWithoutEnteringBusinessOutbox()
    {
        using var f = new Fixture();
        for (var i = 0; i < 1500; i++) f.Publisher.EnqueueStatus(true, i == 1499);
        Assert.Empty(new DurableEventBuffer(f.Path).ReadBatch());
        using var stop = new CancellationTokenSource();
        f.Client.Handler = (message, ct) => {
            using var payload = JsonDocument.Parse(message.Payload.ToArray());
            Assert.True(payload.RootElement.GetProperty("gatewayConnected").GetBoolean());
            stop.Cancel(); return Success(message, ct);
        };
        await f.Publisher.DrainAsync(stop.Token);
        Assert.Equal(1, f.Client.Attempts);
    }

    [Fact]
    public async Task ReplayUsesBoundedBatchesAndPreservesEveryTailEntry()
    {
        using var f = new Fixture();
        for (var i = 0; i < 270; i++) f.Enqueue("entry-" + i);
        Assert.Equal(256, new DurableEventBuffer(f.Path).ReadBatch().Length);
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(14, new DurableEventBuffer(f.Path).ReadBatch().Length);
        Assert.True(await f.Publisher.ReplayBufferAsync(CancellationToken.None));
        Assert.Equal(270, f.Client.Attempts);
        Assert.Empty(File.ReadAllLines(f.Path));
    }

    [Fact]
    public async Task DiagnosticRejectsActivePickingAndPersistsTerminalRejectionBeforeHardwareIo()
    {
        using var f = new Fixture();
        f.Registry.SetInstructed(new PickingInstruction { Address = "0001", Quantity = 1, BlockNo = "01", CorrelationId = "live" });
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "receipts.db"));
        var dispatcher = new MqttCommandDispatcher(f.Engine, f.Publisher, new PreemptionGate(), null,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance),
            new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance, receipts);
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"scan_devices\",\"commandId\":\"scan\",\"expiresAt\":\"2099-01-01T00:00:00Z\",\"from\":1,\"to\":9999}");
        await dispatcher.HandleAsync(payload, CancellationToken.None);
        using var result = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(File.ReadAllLines(f.Path).Single()).Payload));
        Assert.Equal("rejected", result.RootElement.GetProperty("status").GetString());
        Assert.Equal("BRIDGE_BUSY", result.RootElement.GetProperty("errorCode").GetString());
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        Assert.Equal("BRIDGE_BUSY", receipts.GetExisting("command:scan", hash, "scan", null)!.ErrorCode);
        Assert.Equal("COMMAND_ID_CONFLICT", receipts.TryBegin("command:scan", "new-hash", "scan", null)!.ErrorCode);
        Assert.Equal(P2L.Shared.Enums.ModuleStatus.Operating, f.Registry.Get("0001")!.Status);
    }

    [Fact]
    public async Task DiagnosticBudgetCancelsWithoutOpeningGatewayConnection()
    {
        using var f = new Fixture();
        var dispatcher = new MqttCommandDispatcher(f.Engine, f.Publisher, new PreemptionGate(), null,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance),
            new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance,
            new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "receipts.db")), diagnosticTimeoutMs: 10);
        // Gateway is deliberately never started: no TCP/LAN connection is possible.
        await dispatcher.HandleAsync(Encoding.UTF8.GetBytes("{\"type\":\"scan_devices\",\"commandId\":\"scan-budget\",\"expiresAt\":\"2099-01-01T00:00:00Z\",\"from\":1,\"to\":20}"), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        using var result = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(File.ReadAllLines(f.Path).Single()).Payload));
        Assert.Equal("uncertain", result.RootElement.GetProperty("status").GetString());
        Assert.False(f.Gateway.IsConnected);
    }

    [Fact]
    public async Task FullQueueReturnsStoredAppliedOutcomeInsteadOfBusyForLostResultRetry()
    {
        using var f = new Fixture();
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "full-receipts.db"));
        var old = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"commandId\":\"already-applied\"}");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(old));
        Assert.Null(receipts.TryBegin("command:already-applied", hash, "already-applied", null));
        receipts.Complete("command:already-applied", new CommandResultEvt { Type = "command_result", Status = "applied" });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new CommandAdmissionQueue(1, async (_, _) => { entered.TrySetResult(); await release.Task; }, CancellationToken.None, _ => { });
        queue.TryEnqueue(Encoding.UTF8.GetBytes("{\"commandId\":\"other-work\"}"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        CommandResultEvt? result = null;
        Assert.Equal(CommandAdmission.Full, queue.TryEnqueue(old, () => result = MqttBridgeService.GetQueueFullResult(receipts, old)));
        Assert.Equal("applied", result!.Status); Assert.Null(result.ErrorCode);
        var conflict = MqttBridgeService.GetQueueFullResult(receipts, Encoding.UTF8.GetBytes("{\"type\":\"terminate_all\",\"commandId\":\"already-applied\"}"));
        Assert.Equal("COMMAND_ID_CONFLICT", conflict.ErrorCode);
        release.SetResult(); await queue.StopAsync();
    }

    [Fact]
    public void FullQueueReturnsUncertainForExecutingReceiptAndBusyOnlyForUnseenIntent()
    {
        using var f = new Fixture();
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "executing-receipts.db"));
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"commandId\":\"executing\"}");
        Assert.Null(receipts.TryBegin("command:executing", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)), "executing", null));
        Assert.Equal("uncertain", MqttBridgeService.GetQueueFullResult(receipts, payload).Status);
        var unseen = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"commandId\":\"unseen\"}");
        Assert.Equal("BRIDGE_BUSY", MqttBridgeService.GetQueueFullResult(receipts, unseen).ErrorCode);
        Assert.Equal("BRIDGE_BUSY", MqttBridgeService.GetQueueFullResult(receipts, unseen, pendingConflict: true).ErrorCode);
        var conflicting = Encoding.UTF8.GetBytes("{\"type\":\"terminate_all\",\"commandId\":\"unseen\"}");
        Assert.Equal("COMMAND_ID_CONFLICT", MqttBridgeService.GetQueueFullResult(receipts, conflicting, pendingConflict: true).ErrorCode);
        Assert.Equal("BRIDGE_BUSY", receipts.GetExisting("command:unseen", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unseen)), "unseen", null)!.ErrorCode);
        var pending = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"commandId\":\"pending-original\"}");
        Assert.Equal("COMMAND_ID_CONFLICT", MqttBridgeService.GetQueueFullResult(receipts, pending, pendingConflict: true).ErrorCode);
        Assert.Null(receipts.GetExisting("command:pending-original", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pending)), "pending-original", null));
    }

    [Fact]
    public void ConfirmationReplayPreservesPersistedEventIdAndTimestamp()
    {
        using var f = new Fixture();
        var ev = new ConfirmationEvent { Address = "0001", EventId = "persisted-confirm", ReceivedAt = DateTime.UtcNow.AddMinutes(-1) };
        var state = new LightModuleState { Address = "0001", CorrelationId = "s", CommandItemId = "i" };
        var capture = typeof(MqttEventPublisher).GetMethod("OnItemConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        capture.Invoke(f.Publisher, [ev, state]); capture.Invoke(f.Publisher, [ev, state]);
        var entries = new DurableEventBuffer(f.Path).ReadBatch().Select(DurableEventBuffer.Parse).ToArray();
        Assert.Equal(entries[0].Payload, entries[1].Payload);
        using var json = JsonDocument.Parse(Convert.FromBase64String(entries[0].Payload));
        Assert.Equal("persisted-confirm", json.RootElement.GetProperty("eventId").GetString());
        Assert.Equal(ev.ReceivedAt, json.RootElement.GetProperty("timestamp").GetDateTime());
    }

    [Fact]
    public async Task BusyRejectionDurablyFencesSameIdBeforeQueueSpaceReturns()
    {
        using var f = new Fixture();
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "busy-fence.db"));
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"address\":\"0001\",\"quantity\":1,\"commandId\":\"busy-once\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}");
        Assert.Equal("BRIDGE_BUSY", MqttBridgeService.GetQueueFullResult(receipts, payload).ErrorCode);
        var dispatcher = new MqttCommandDispatcher(f.Engine, f.Publisher, new PreemptionGate(), null,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance),
            new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance, receipts);
        // Queue space can now be available; the rejected ID remains non-executable.
        await dispatcher.HandleAsync(payload, CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(500));
        using var result = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(File.ReadAllLines(f.Path).Single()).Payload));
        Assert.Equal("BRIDGE_BUSY", result.RootElement.GetProperty("errorCode").GetString());
        Assert.Null(f.Registry.Get("0001"));
        Assert.False(f.Gateway.IsInitialized);
        Assert.Null(receipts.TryBegin("command:fresh-attempt", "different-hash", "fresh-attempt", null));
    }

    [Fact]
    public async Task CachedAppliedOutcomeWinsOverExpiredExecutionDeadline()
    {
        using var f = new Fixture();
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "cached.db"));
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"address\":\"0001\",\"quantity\":1,\"commandId\":\"cached-expired\",\"expiresAt\":\"2000-01-01T00:00:00Z\",\"executionDeadlineAt\":\"2000-01-01T00:00:00Z\"}");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));
        receipts.TryBegin("command:cached-expired", hash, "cached-expired", null);
        receipts.Complete("command:cached-expired", new CommandResultEvt { Type = "command_result", Status = "applied" });
        var dispatcher = new MqttCommandDispatcher(f.Engine, f.Publisher, new PreemptionGate(), null,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance),
            new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance, receipts);
        await dispatcher.HandleAsync(payload, CancellationToken.None);
        using var result = JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(File.ReadAllLines(f.Path).Single()).Payload));
        Assert.Equal("applied", result.RootElement.GetProperty("status").GetString());
        Assert.Null(f.Registry.Get("0001"));
    }

    [Fact]
    public async Task CancelledQueuedCommandIsTerminalAndCannotExecuteWhenRetried()
    {
        using var f = new Fixture();
        var receipts = new CommandReceiptStore(System.IO.Path.Combine(f.Directory, "queued.db"));
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"light_on\",\"address\":\"0001\",\"quantity\":1,\"commandId\":\"queued-expired\",\"expiresAt\":\"2099-01-01T00:00:00Z\"}");
        var dispatcher = new MqttCommandDispatcher(f.Engine, f.Publisher, new PreemptionGate(), null,
            new DeviceLabelStore(null, NullLogger<DeviceLabelStore>.Instance),
            new DeviceScanService(f.Gateway, f.Registry, NullLogger<DeviceScanService>.Instance), NullLogger.Instance, receipts);
        await dispatcher.HandleAsync(payload, new CancellationToken(canceled: true));
        await dispatcher.HandleAsync(payload, CancellationToken.None);
        var outcomes = File.ReadAllLines(f.Path).Select(line => JsonDocument.Parse(Convert.FromBase64String(DurableEventBuffer.Parse(line).Payload))).ToArray();
        Assert.Equal(2, outcomes.Length);
        Assert.All(outcomes, result => Assert.Equal("COMMAND_CANCELLED", result.RootElement.GetProperty("errorCode").GetString()));
        Assert.Null(f.Registry.Get("0001"));
        foreach (var result in outcomes) result.Dispose();
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
