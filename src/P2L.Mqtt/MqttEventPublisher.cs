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
/// Subscribes to <see cref="PickingEngine"/> events and publishes them as JSON
/// to the MQTT event topic. Uses a bounded Channel as a non-blocking queue so
/// event handlers never block the PickingEngine thread waiting for MQTT I/O.
/// </summary>
internal sealed class MqttEventPublisher : IAsyncDisposable
{
    private readonly IMqttClient      _client;
    private readonly MqttSettings     _settings;
    private readonly PickingEngine    _engine;
    private readonly ILogger          _log;

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
                               PickingEngine engine, ILogger<MqttEventPublisher> log)
    {
        _client   = client;
        _settings = settings;
        _engine   = engine;
        _log      = log;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public void Start()
    {
        _engine.ItemConfirmed  += OnItemConfirmed;
        _engine.BatchCompleted += OnBatchCompleted;
        _drain = Task.Run(() => DrainAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
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

    // ─── Event handlers (called on PickingEngine thread) ─────────────────

    private void OnItemConfirmed(ConfirmationEvent evt, LightModuleState? state)
    {
        _queue.Writer.TryWrite(new ConfirmedEvt
        {
            Type            = "confirmed",
            Address         = evt.Address,
            Status          = evt.Status,
            AdjustedDisplay = evt.AdjustedDisplay,
        });

        if (state is not null)
            _queue.Writer.TryWrite(new LightChangedEvt
            {
                Type         = "light_changed",
                Address      = state.Address,
                ModuleStatus = state.Status,
                Color        = state.Color,
                LedMode      = state.LedMode,
                DisplayData  = state.DisplayData,
                BlockNo      = state.BlockNo,
            });
    }

    private void OnBatchCompleted()
        => _queue.Writer.TryWrite(new BatchCompletedEvt { Type = "batch_completed" });

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
            catch (Exception ex) { _log.LogWarning("[MQTT] Publish failed: {Err}", ex.Message); }
        }
    }

    private Task PublishAsync(MqttEvent evt, CancellationToken ct)
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
                return Task.CompletedTask;
        }

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        return _client.PublishAsync(msg, ct);
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
