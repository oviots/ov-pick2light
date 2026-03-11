using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Protocol;
using P2L.Core;
using P2L.Mqtt.Configuration;
using P2L.Mqtt.Messages;
using P2L.Shared.Models;
// IMqttClient lives in the root MQTTnet namespace in v5

namespace P2L.Mqtt;

/// <summary>
/// Publishes light module state changes and picking events as JSON to MQTT topics.
///
/// Real-time source: <see cref="LightModuleRegistry.StateChanged"/> fires on every
/// mutation (instruct, confirm, terminate, configure, scan) and enqueues a
/// <c>light_changed</c> event immediately.
///
/// Uses a bounded Channel as a non-blocking queue so event handlers never block
/// the caller thread waiting for MQTT I/O.
/// </summary>
internal sealed class MqttEventPublisher : IAsyncDisposable
{
    private readonly IMqttClient          _client;
    private readonly MqttSettings         _settings;
    private readonly PickingEngine        _engine;
    private readonly LightModuleRegistry  _registry;
    private readonly ILogger              _log;

    private readonly Channel<MqttEvent> _queue =
        Channel.CreateBounded<MqttEvent>(new BoundedChannelOptions(1000)
        {
            FullMode         = BoundedChannelFullMode.DropOldest,
            SingleReader     = true,
            SingleWriter     = false,
        });

    private readonly CancellationTokenSource _cts  = new();
    private          Task?                   _drain;

    public MqttEventPublisher(IMqttClient client, MqttSettings settings,
                               PickingEngine engine, LightModuleRegistry registry,
                               ILogger<MqttEventPublisher> log)
    {
        _client   = client;
        _settings = settings;
        _engine   = engine;
        _registry = registry;
        _log      = log;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public void Start()
    {
        _registry.StateChanged += OnStateChanged;
        _engine.ItemConfirmed  += OnItemConfirmed;
        _engine.BatchCompleted += OnBatchCompleted;
        _drain = Task.Run(() => DrainAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _registry.StateChanged -= OnStateChanged;
        _engine.ItemConfirmed  -= OnItemConfirmed;
        _engine.BatchCompleted -= OnBatchCompleted;

        _queue.Writer.TryComplete();
        await _cts.CancelAsync();

        if (_drain is not null)
        {
            try { await _drain; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    // ─── Event handlers ───────────────────────────────────────────────────

    private void OnStateChanged(LightModuleState state)
        => _queue.Writer.TryWrite(StateToEvt(state));

    private void OnItemConfirmed(ConfirmationEvent evt, LightModuleState? _)
        => _queue.Writer.TryWrite(new ConfirmedEvt
        {
            Type            = "confirmed",
            Address         = evt.Address,
            Status          = evt.Status,
            AdjustedDisplay = evt.AdjustedDisplay,
        });

    private void OnBatchCompleted()
        => _queue.Writer.TryWrite(new BatchCompletedEvt { Type = "batch_completed" });

    // ─── Snapshot (on-demand full state dump) ────────────────────────────

    public void EnqueueSnapshot()
    {
        foreach (var module in _registry.All)
            _queue.Writer.TryWrite(StateToEvt(module));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────

    private static LightChangedEvt StateToEvt(LightModuleState s) => new()
    {
        Type              = "light_changed",
        Address           = s.Address,
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
    };

    // ─── Drain loop ───────────────────────────────────────────────────────

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Block until there is something in the queue
            if (!await _queue.Reader.WaitToReadAsync(ct)) break;

            // Wait until MQTT is connected before consuming the event
            while (!_client.IsConnected && !ct.IsCancellationRequested)
                await Task.Delay(200, ct);

            if (!_queue.Reader.TryRead(out var evt)) continue;

            try   { await PublishAsync(evt, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError("[MQTT] Publish exception: {Err}", ex.Message); }
        }
    }

    private async Task PublishAsync(MqttEvent evt, CancellationToken ct)
    {
        string  topic;
        byte[]? payload;
        bool    retain = false;

        switch (evt)
        {
            case ConfirmedEvt e:
                topic   = _settings.EventTopic;
                payload = JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.ConfirmedEvt);
                break;
            case LightChangedEvt e:
                topic   = _settings.EventTopic;
                payload = JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.LightChangedEvt);
                break;
            case BatchCompletedEvt e:
                topic   = _settings.EventTopic;
                payload = JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.BatchCompletedEvt);
                break;
            case BridgeStatusEvt e:
                // Status goes to its own topic with retain=true so new subscribers
                // immediately see the current bridge/gateway state.
                topic   = _settings.StatusTopic;
                retain  = true;
                payload = JsonSerializer.SerializeToUtf8Bytes(e, P2LMqttJsonContext.Default.BridgeStatusEvt);
                break;
            default:
                return;
        }

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        _log.LogDebug("[MQTT] Publishing {Type} → {Topic}", evt.Type, topic);
        var result = await _client.PublishAsync(msg, ct);
        if (result.ReasonCode != MqttClientPublishReasonCode.Success)
            _log.LogWarning("[MQTT] Publish rejected: type={Type} topic={Topic} reason={Reason}",
                evt.Type, topic, result.ReasonCode);
        else
            _log.LogDebug("[MQTT] Published  {Type} → {Topic}", evt.Type, topic);
    }

    // ─── Public enqueue for bridge-status events ─────────────────────────

    public void EnqueueStatus(bool online, bool gatewayConnected)
        => _queue.Writer.TryWrite(new BridgeStatusEvt
        {
            Type             = "bridge_status",
            Online           = online,
            GatewayConnected = gatewayConnected,
        });
}
