using System.Text.Json;
using Microsoft.Extensions.Logging;
using P2L.Core;
using P2L.Core.Services;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Mqtt;

/// <summary>
/// Captures P2L events and appends them directly to the offline JSONL buffer
/// without requiring an active MQTT connection or a configured broker.
///
/// Activated when <c>Mqtt.Enabled=false</c> but <c>Mqtt.OfflineBufferPath</c> is set,
/// so no events are silently dropped while the broker is not yet configured.
/// The buffer file format is identical to <see cref="MqttEventPublisher"/>'s offline
/// output, so once a broker is configured (Enabled=true) the first reconnect will
/// replay all events written here before going live.
/// </summary>
public sealed class OfflineBufferService : IAsyncDisposable
{
    private readonly MqttSettings        _settings;
    private readonly PickingEngine       _engine;
    private readonly LightModuleRegistry _registry;
    private readonly CsvPickTaskRunner?  _csvRunner;
    private readonly ILogger             _log;
    private readonly DurableEventBuffer _outbox;

    public OfflineBufferService(
        MqttSettings settings,
        PickingEngine engine,
        LightModuleRegistry registry,
        ILogger<OfflineBufferService> log,
        CsvPickTaskRunner? csvRunner = null)
    {
        _settings  = settings;
        _engine    = engine;
        _registry  = registry;
        _csvRunner = csvRunner;
        _log       = log;
        _outbox    = new DurableEventBuffer(settings.OfflineBufferPath);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    private int _started;

    /// <summary>
    /// Idempotent — safe to call multiple times (e.g. from TypeRegistrar.Build and
    /// from RootCommand/RunPickCommand). Subscriptions are only registered once.
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;

        _registry.StateChanged += OnStateChanged;
        _engine.ItemConfirmed  += OnItemConfirmed;
        _engine.BatchCompleted += OnBatchCompleted;
        if (_csvRunner is not null)
            _csvRunner.TaskEvent += OnTaskEvent;

        _log.LogInformation("[OfflineBuffer] Active — writing events to {Path}",
            _settings.OfflineBufferPath);
    }

    public ValueTask DisposeAsync()
    {
        _registry.StateChanged -= OnStateChanged;
        _engine.ItemConfirmed  -= OnItemConfirmed;
        _engine.BatchCompleted -= OnBatchCompleted;
        if (_csvRunner is not null)
            _csvRunner.TaskEvent -= OnTaskEvent;

        return ValueTask.CompletedTask;
    }

    // ─── Event handlers ───────────────────────────────────────────────────

    private void OnStateChanged(LightModuleState state)
    {
        var evt = new LightChangedEvt
        {
            Type              = "light_changed",
            Address           = state.Address,
            CorrelationId     = state.CorrelationId,
            ModuleStatus      = state.Status,
            Color             = state.Color,
            LedMode           = state.LedMode,
            SegMode           = state.SegMode,
            BuzMode           = state.BuzMode,
            DisplayData       = state.DisplayData,
            BlockNo           = state.BlockNo,
            AdjustedQuantity  = state.AdjustedQuantity,
            LastInstructionAt = state.LastInstructionAt,
            LastConfirmedAt   = state.LastConfirmedAt,
        };
        Append($"{_settings.DevicesTopic}/{state.Address}", retain: true,
               JsonSerializer.SerializeToUtf8Bytes(evt, P2LMqttJsonContext.Default.LightChangedEvt));
    }

    private void OnItemConfirmed(ConfirmationEvent evt, LightModuleState? state)
    {
        var e = new ConfirmedEvt
        {
            Type            = "confirmed",
            Address         = evt.Address,
            Status          = evt.Status,
            AdjustedDisplay = evt.AdjustedDisplay,
            CorrelationId   = state?.CorrelationId,
            CommandItemId   = state?.CommandItemId,
            PickingDetailId = state?.PickingDetailId,
            AdjustedQuantity = state?.AdjustedQuantity,
            DisplayData     = state?.DisplayData,
        };
        Append(_settings.EventTopic, retain: false,
               JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.ConfirmedEvt));
    }

    private void OnBatchCompleted(string? correlationId)
    {
        var e = new BatchCompletedEvt
        {
            Type = "batch_completed",
            CorrelationId = correlationId,
        };
        Append(_settings.EventTopic, retain: false,
               JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.BatchCompletedEvt));
    }

    private void OnTaskEvent(CsvTaskEvent taskEvt)
    {
        var e = new TaskEvt
        {
            Type     = taskEvt.EventType,
            Address  = taskEvt.Row?.Address  ?? "",
            Quantity = taskEvt.Row?.Quantity ?? 0,
            Color    = taskEvt.Row?.Color    ?? LightColor.Green,
            Label    = taskEvt.Row?.Label,
        };
        Append(_settings.EventTopic, retain: false,
               JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.TaskEvt));
    }

    // ─── Buffer write ─────────────────────────────────────────────────────

    private void Append(string topic, bool retain, byte[] payload)
    {
        // Persistence failures must reach the confirmation handler; logging and
        // returning would let the hardware confirmation be accepted without a record.
        _outbox.Append(topic, retain, payload);
    }
}
