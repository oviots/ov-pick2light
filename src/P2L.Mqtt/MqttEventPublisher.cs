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
        await foreach (var evt in _queue.Reader.ReadAllAsync(ct))
        {
            if (!_client.IsConnected) continue;
            try   { await PublishAsync(evt, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning("[MQTT] Publish failed: {Err}", ex.Message); }
        }
    }

    private Task PublishAsync(MqttEvent evt, CancellationToken ct)
    {
        var payload = evt switch
        {
            ConfirmedEvt    e => JsonSerializer.SerializeToUtf8Bytes(e,
                                    P2LMqttJsonContext.Default.ConfirmedEvt),
            LightChangedEvt e => JsonSerializer.SerializeToUtf8Bytes(e,
                                    P2LMqttJsonContext.Default.LightChangedEvt),
            BatchCompletedEvt e => JsonSerializer.SerializeToUtf8Bytes(e,
                                    P2LMqttJsonContext.Default.BatchCompletedEvt),
            BridgeStatusEvt e => JsonSerializer.SerializeToUtf8Bytes(e,
                                    P2LMqttJsonContext.Default.BridgeStatusEvt),
            _ => null,
        };

        if (payload is null) return Task.CompletedTask;

        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(_settings.EventTopic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
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
