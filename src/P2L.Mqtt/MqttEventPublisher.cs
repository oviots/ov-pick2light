using System.Text.Json;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Core.Services;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Mqtt.Transport;
using P2L.Shared.Enums;
using P2L.Shared.Models;
// IMqttClient lives in the root MQTTnet namespace in v5

namespace P2L.Mqtt;

/// <summary>Publishes durable business events before best-effort state telemetry.</summary>
internal sealed class MqttEventPublisher : IAsyncDisposable
{
    private readonly IMqttClient?         _client;
    private readonly BridgeTransportCoordinator? _transport;
    private bool IsConnected => _transport?.IsConnected ?? _client!.IsConnected;
    private readonly MqttSettings         _settings;
    private readonly PickingEngine        _engine;
    private readonly LightModuleRegistry  _registry;
    private readonly DeviceLabelStore     _labels;
    private readonly ILogger              _log;
    private readonly DurableEventBuffer   _outbox;

    // Optional retained state is coalesced separately from the durable business FIFO.
    private readonly Dictionary<string, byte[]> _telemetry = new(StringComparer.Ordinal);
    private readonly object _telemetrySync = new();

    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _started;
    private int _disposed;
    private DateTime _lastDeliveryWarningAt = DateTime.MinValue;
    private readonly CancellationTokenSource _cts  = new();
    private          Task?                   _drain;

    public MqttEventPublisher(IMqttClient client, MqttSettings settings,
        PickingEngine engine, LightModuleRegistry registry, DeviceLabelStore labels,
        ILogger<MqttEventPublisher> log) : this(client, null, settings, engine, registry, labels, log) { }

    public MqttEventPublisher(BridgeTransportCoordinator transport, MqttSettings settings,
        PickingEngine engine, LightModuleRegistry registry, DeviceLabelStore labels,
        ILogger<MqttEventPublisher> log) : this(null, transport, settings, engine, registry, labels, log) { }

    private MqttEventPublisher(IMqttClient? client, BridgeTransportCoordinator? transport, MqttSettings settings,
        PickingEngine engine, LightModuleRegistry registry, DeviceLabelStore labels,
        ILogger<MqttEventPublisher> log)
    {
        _client     = client;
        _transport  = transport;
        _settings   = settings;
        _engine     = engine;
        _registry   = registry;
        _labels     = labels;
        _log        = log;
        _outbox = new DurableEventBuffer(settings.OfflineBufferPath);
        // The gateway can receive confirmations before the broker loop starts.
        // Attach durable capture as soon as this bridge is constructed.
        _labels.Load();
        _registry.StateChanged += OnStateChanged;
        _engine.RequireDurableConfirmationConsumer();
        _engine.DurableConfirmation += OnItemConfirmed;
        _engine.DurableConfirmationMissing += OnConfirmationMissing;
        _engine.DurableBatchCompleted += OnBatchCompleted;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { _engine.ReplayPendingConfirmations(); }
        catch { Volatile.Write(ref _started, 0); throw; }
        _drain = Task.Run(() => DrainAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registry.StateChanged -= OnStateChanged;
        _engine.DurableConfirmation  -= OnItemConfirmed;
        _engine.DurableConfirmationMissing -= OnConfirmationMissing;
        _engine.DurableBatchCompleted -= OnBatchCompleted;

        await _cts.CancelAsync();

        try
        {
            if (_drain is not null)
                try { await _drain; } catch (OperationCanceledException) { }
        }
        finally
        {
            _wake.Dispose();
            _cts.Dispose();
        }
    }

    // ─── Event handlers ───────────────────────────────────────────────────

    private void OnStateChanged(LightModuleState state)
        => EnqueueTelemetry(StateToEvt(state));

    private void OnItemConfirmed(ConfirmationEvent evt, LightModuleState? state)
        => EnqueueDurable(new ConfirmedEvt
        {
            Type             = "confirmed",
            EventId          = evt.EventId,
            Source           = evt.Source,
            Timestamp        = evt.ReceivedAt,
            Address          = evt.Address,
            Status           = evt.Status,
            AdjustedQuantity = state?.AdjustedQuantity,
            AdjustedDisplay  = evt.AdjustedDisplay,
            CorrelationId    = state?.CorrelationId,
            CommandItemId    = state?.CommandItemId,
            PickingDetailId  = state?.PickingDetailId,
            DisplayData      = state?.DisplayData,
            Label            = _labels.GetLabel(evt.Address) is { Length: > 0 } l ? l : null,
        });

    private void OnConfirmationMissing(ConfirmationMissingEvent evt)
        => EnqueueDurable(MissingToEvt(evt));

    internal static ConfirmationMissingEvt MissingToEvt(ConfirmationMissingEvent evt) => new()
    {
        // Older WMS versions treat any event type containing "confirm" as a pick.
        Type = "light_feedback_missing",
        EventId = evt.EventId,
        Timestamp = evt.DetectedAt,
        Address = evt.Address,
        BlockNo = evt.BlockNo,
        CorrelationId = evt.CorrelationId,
        CommandItemId = evt.CommandItemId,
        PickingDetailId = evt.PickingDetailId,
        ObservedStatus = evt.ObservedStatus,
        LastInstructionAt = evt.LastInstructionAt,
    };

    private void OnBatchCompleted(BatchCompletionEvent completion)
        => EnqueueDurable(new BatchCompletedEvt
        {
            Type = "batch_completed",
            CorrelationId = completion.CorrelationId,
            EventId = completion.EventId,
            Timestamp = completion.CompletedAt,
        });

    // ─── Snapshot / task events ───────────────────────────────────────────

    public void EnqueueSnapshot()
    {
        foreach (var module in _registry.All)
            EnqueueTelemetry(StateToEvt(module));
    }

    public void EnqueueTaskEvent(CsvTaskEvent taskEvt)
        => EnqueueDurable(new TaskEvt
        {
            Type     = taskEvt.EventType,
            Address  = taskEvt.Row?.Address  ?? "",
            Quantity = taskEvt.Row?.Quantity ?? 0,
            Color    = taskEvt.Row?.Color    ?? LightColor.Green,
            Label    = taskEvt.Row?.Label,
        });

    public void EnqueueStatus(bool online, bool gatewayConnected)
        => EnqueueTelemetry(new BridgeStatusEvt
        {
            Type             = "bridge_status",
            Online           = online,
            GatewayConnected = gatewayConnected,
        });

    // A successful return means the event survives a process restart. Disk errors
    // propagate so command handlers cannot ACK an outcome that was not persisted.
    public void EnqueueCommandResult(CommandResultEvt result) => EnqueueDurable(result);

    private void EnqueueDurable(MqttEvent evt)
    {
        var msg = BuildMessage(evt) ?? throw new InvalidOperationException("Unsupported MQTT event.");
        _outbox.Append(msg.Topic, msg.Retain, msg.Retain ? msg.Payload : EventIdentity.Ensure(msg.Payload));
        Wake();
    }

    private void EnqueueTelemetry(MqttEvent evt)
    {
        var message = BuildMessage(evt);
        if (message is null) return;
        KeepTelemetry(message.Value.Topic, message.Value.Payload);
        Wake();
    }

    private void KeepTelemetry(string topic, byte[] payload, bool replace = true)
    {
        lock (_telemetrySync)
        {
            if (_telemetry.ContainsKey(topic)) { if (replace) _telemetry[topic] = payload; }
            else if (_telemetry.Count < 1000) _telemetry.Add(topic, payload);
        }
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private LightChangedEvt StateToEvt(LightModuleState s) => new()
    {
        Type              = "light_changed",
        Address           = s.Address,
        CorrelationId     = s.CorrelationId,
        CommandItemId     = s.CommandItemId,
        PickingDetailId   = s.PickingDetailId,
        ObservedStatus    = s.ObservedStatus,
        ConfirmationMissingAt = s.ConfirmationMissingAt,
        ConfirmationMissingEventId = s.ConfirmationMissingEventId,
        ModuleStatus      = s.Status,
        Color             = s.Color,
        LedMode           = s.LedMode,
        SegMode           = s.SegMode,
        BuzMode           = s.BuzMode,
        DisplayData       = s.DisplayData,
        BlockNo           = s.BlockNo,
        AdjustedQuantity  = s.AdjustedQuantity,
        LastInstructionAt = s.LastInstructionAt,
        LastConfirmedAt   = s.LastConfirmedAt,
        Label             = _labels.GetLabel(s.Address),
    };

    /// <summary>
    /// Resolves the MQTT topic, retain flag, and serialized payload for an event.
    /// Returns null for unknown event types (no-op).
    /// </summary>
    private (string Topic, bool Retain, byte[] Payload)? BuildMessage(MqttEvent evt)
    {
        switch (evt)
        {
            case CommandResultEvt e:
                return (_settings.EventTopic, false,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.CommandResultEvt));

            case ConfirmedEvt e:
                return (_settings.EventTopic, false,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.ConfirmedEvt));

            case ConfirmationMissingEvt e:
                return (_settings.EventTopic, false,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.ConfirmationMissingEvt));

            case LightChangedEvt e:
                return ($"{_settings.DevicesTopic}/{e.Address}", true,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.LightChangedEvt));

            case BatchCompletedEvt e:
                return (_settings.EventTopic, false,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.BatchCompletedEvt));

            case BridgeStatusEvt e:
                return (_settings.StatusTopic, true,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.BridgeStatusEvt));

            case TaskEvt e:
                return (_settings.EventTopic, false,
                    JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.TaskEvt));

            default:
                return null;
        }
    }

    // ─── Drain loop ───────────────────────────────────────────────────────

    internal async Task DrainAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!IsConnected)
                    {
                        await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                        continue;
                    }

                    if (!await ReplayBufferAsync(ct))
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }
                    if (_outbox.ReadBatch(1).Length != 0) continue;

                    KeyValuePair<string, byte[]> telemetry;
                    lock (_telemetrySync)
                    {
                        telemetry = _telemetry.FirstOrDefault();
                        if (telemetry.Key is not null) _telemetry.Remove(telemetry.Key);
                    }
                    if (telemetry.Key is not null)
                    {
                        try { await SendTelemetryAsync(telemetry.Key, telemetry.Value, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                        catch (Exception ex)
                        {
                            // Business replay is always retried before this optional topic.
                            KeepTelemetry(telemetry.Key, telemetry.Value, replace: false);
                            ReportDeliveryFailure("Optional telemetry deferred: " + ex.Message);
                            await Task.Delay(500, ct);
                        }
                        continue;
                    }
                    await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    ReportDeliveryFailure("Event outbox unavailable: " + ex.Message);
                    await Task.Delay(500, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task SendTelemetryAsync(string topic, byte[] payload, CancellationToken ct)
    {
        if (_transport is not null) { await _transport.SendAsync(topic, payload, true, ct); return; }
        var result = await _client!.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic).WithPayload(payload).WithRetainFlag(true)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), ct);
        if (result.ReasonCode is not (MqttClientPublishReasonCode.Success or MqttClientPublishReasonCode.NoMatchingSubscribers))
            throw new IOException("Telemetry publish rejected: " + result.ReasonCode);
    }

    private void ReportDeliveryFailure(string reason)
    {
        var now = DateTime.UtcNow;
        if (now - _lastDeliveryWarningAt < TimeSpan.FromSeconds(30)) return;
        _lastDeliveryWarningAt = now;
        _log.LogWarning("[MQTT] Event delivery deferred; durable outbox preserved: {Reason}", reason);
    }

    internal async Task<bool> ReplayBufferAsync(CancellationToken ct)
    {
        var lines = _outbox.ReadBatch();
        var acknowledged = 0;
        try
        {
            foreach (var line in lines)
            {
                if (ct.IsCancellationRequested || !IsConnected) break;
                if (string.IsNullOrWhiteSpace(line)) { acknowledged++; continue; }
                BufferedEntry entry;
                try
                {
                    entry = DurableEventBuffer.Parse(line);
                }
                catch (Exception ex) when (ex is JsonException or FormatException)
                {
                    _outbox.Quarantine(line);
                    _log.LogError("[MQTT] Invalid offline entry quarantined: {Err}", ex.Message);
                    acknowledged++;
                    continue;
                }

                if (entry.Retain)
                {
                    // Migrate old mixed buffers without making optional state a FIFO barrier.
                    KeepTelemetry(entry.Topic, Convert.FromBase64String(entry.Payload));
                    acknowledged++;
                    continue;
                }
                try
                {
                    var payload = entry.Retain ? Convert.FromBase64String(entry.Payload) : EventIdentity.FromBuffered(entry);
                    if (_transport is not null)
                    {
                        await _transport.SendAsync(entry.Topic, payload, entry.Retain, ct);
                        acknowledged++;
                        _lastDeliveryWarningAt = DateTime.MinValue;
                        continue;
                    }
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(entry.Topic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(entry.Retain)
                        .Build();
                    var result = await _client!.PublishAsync(msg, ct);
                    if (result.ReasonCode != MqttClientPublishReasonCode.Success
                        && !(entry.Retain && result.ReasonCode == MqttClientPublishReasonCode.NoMatchingSubscribers))
                    {
                        ReportDeliveryFailure("Broker rejected delivery: " + result.ReasonCode);
                        break;
                    }
                    acknowledged++;
                    _lastDeliveryWarningAt = DateTime.MinValue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    ReportDeliveryFailure(ex.Message);
                    break;
                }
            }
        }
        finally
        {
            _outbox.AcknowledgePrefix(lines, acknowledged);
        }
        return acknowledged == lines.Length;
    }
}
