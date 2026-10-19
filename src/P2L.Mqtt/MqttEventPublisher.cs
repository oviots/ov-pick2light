using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Core.Services;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Enums;
using P2L.Shared.Models;
// IMqttClient lives in the root MQTTnet namespace in v5

namespace P2L.Mqtt;

/// <summary>Publishes durable business events before best-effort state telemetry.</summary>
internal sealed class MqttEventPublisher : IAsyncDisposable
{
    private readonly IMqttClient          _client;
    private readonly MqttSettings         _settings;
    private readonly PickingEngine        _engine;
    private readonly LightModuleRegistry  _registry;
    private readonly DeviceLabelStore     _labels;
    private readonly ILogger              _log;
    private readonly DurableEventBuffer   _outbox;

    private readonly Channel<MqttEvent> _queue =
        Channel.CreateBounded<MqttEvent>(new BoundedChannelOptions(1000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _started;
    private int _disposed;
    private DateTime _lastDeliveryWarningAt = DateTime.MinValue;
    private readonly CancellationTokenSource _cts  = new();
    private          Task?                   _drain;

    public MqttEventPublisher(IMqttClient client, MqttSettings settings,
                               PickingEngine engine, LightModuleRegistry registry,
                               DeviceLabelStore labels,
                               ILogger<MqttEventPublisher> log)
    {
        _client     = client;
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
        _engine.ItemConfirmed += OnItemConfirmed;
        _engine.BatchCompleted += OnBatchCompleted;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _drain = Task.Run(() => DrainAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _registry.StateChanged -= OnStateChanged;
        _engine.ItemConfirmed  -= OnItemConfirmed;
        _engine.BatchCompleted -= OnBatchCompleted;

        _queue.Writer.TryComplete();
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

    private void OnBatchCompleted(string? correlationId)
        => EnqueueDurable(new BatchCompletedEvt
        {
            Type = "batch_completed",
            CorrelationId = correlationId,
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
        _outbox.Append(msg.Topic, msg.Retain, msg.Payload);
        Wake();
    }

    private void EnqueueTelemetry(MqttEvent evt)
    {
        _queue.Writer.TryWrite(evt);
        Wake();
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
                    if (!_client.IsConnected)
                    {
                        FlushTelemetry();
                        await _wake.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
                        continue;
                    }

                    if (!await ReplayBufferAsync(ct))
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }

                    if (_queue.Reader.TryRead(out var evt))
                    {
                        var msg = BuildMessage(evt);
                        if (msg is null) continue;
                        // Persist before sending so cancellation and negative PUBACKs
                        // follow the same safe replay path as offline delivery.
                        _outbox.Append(msg.Value.Topic, msg.Value.Retain, msg.Value.Payload);
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
        finally
        {
            FlushTelemetry();
        }
    }

    private void FlushTelemetry()
    {
        while (_queue.Reader.TryRead(out var evt))
        {
            var msg = BuildMessage(evt);
            if (msg is not null) _outbox.Append(msg.Value.Topic, msg.Value.Retain, msg.Value.Payload);
        }
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
                if (ct.IsCancellationRequested || !_client.IsConnected) break;
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

                try
                {
                    var msg = new MqttApplicationMessageBuilder()
                        .WithTopic(entry.Topic)
                        .WithPayload(Convert.FromBase64String(entry.Payload))
                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                        .WithRetainFlag(entry.Retain)
                        .Build();
                    var result = await _client.PublishAsync(msg, ct);
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
