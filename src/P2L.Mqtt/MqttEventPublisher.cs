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

/// <summary>
/// Publishes light module state changes and picking events as JSON to MQTT topics.
///
/// Offline buffering: when the broker is unreachable, events are written to a local
/// JSONL file instead of being dropped. On reconnect the file is replayed line-by-line
/// before live events are processed, then deleted. If the process is killed mid-replay
/// the file is truncated to remove only the events that were successfully sent.
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
    private readonly DeviceLabelStore     _labels;
    private readonly ILogger              _log;
    private readonly string?              _bufferPath;

    private readonly Channel<MqttEvent> _queue =
        Channel.CreateBounded<MqttEvent>(new BoundedChannelOptions(1000)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

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
        _bufferPath = string.IsNullOrWhiteSpace(settings.OfflineBufferPath)
                      ? null
                      : settings.OfflineBufferPath;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    public void Start()
    {
        _labels.Load();
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

    private void OnItemConfirmed(ConfirmationEvent evt, LightModuleState? state)
        => _queue.Writer.TryWrite(new ConfirmedEvt
        {
            Type             = "confirmed",
            Address          = evt.Address,
            Status           = evt.Status,
            AdjustedQuantity = state?.AdjustedQuantity,
            AdjustedDisplay  = evt.AdjustedDisplay,
        });

    private void OnBatchCompleted()
        => _queue.Writer.TryWrite(new BatchCompletedEvt { Type = "batch_completed" });

    // ─── Snapshot / task events ───────────────────────────────────────────

    public void EnqueueSnapshot()
    {
        foreach (var module in _registry.All)
            _queue.Writer.TryWrite(StateToEvt(module));
    }

    public void EnqueueTaskEvent(CsvTaskEvent taskEvt)
        => _queue.Writer.TryWrite(new TaskEvt
        {
            Type     = taskEvt.EventType,
            Address  = taskEvt.Row?.Address  ?? "",
            Quantity = taskEvt.Row?.Quantity ?? 0,
            Color    = taskEvt.Row?.Color    ?? LightColor.Green,
            Label    = taskEvt.Row?.Label,
        });

    public void EnqueueStatus(bool online, bool gatewayConnected)
        => _queue.Writer.TryWrite(new BridgeStatusEvt
        {
            Type             = "bridge_status",
            Online           = online,
            GatewayConnected = gatewayConnected,
        });

    // ─── Helpers ──────────────────────────────────────────────────────────

    private LightChangedEvt StateToEvt(LightModuleState s) => new()
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

    private async Task DrainAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // ── Offline path ─────────────────────────────────────────────
            if (!_client.IsConnected)
            {
                // Flush any pending in-memory events to the buffer file immediately —
                // this is non-blocking (TryRead never waits).
                while (_queue.Reader.TryRead(out var evt))
                {
                    var msg = BuildMessage(evt);
                    if (msg is not null)
                        AppendToBuffer(msg.Value.Topic, msg.Value.Retain, msg.Value.Payload);
                }

                // Poll for reconnection every 500 ms
                await Task.Delay(500, ct);
                continue;
            }

            // ── Reconnected: replay buffer before live events ─────────────
            if (_bufferPath is not null && File.Exists(_bufferPath))
                await ReplayBufferAsync(ct);

            // ── Live publish path ─────────────────────────────────────────
            // WaitToReadAsync blocks until an event arrives or the channel closes.
            if (!await _queue.Reader.WaitToReadAsync(ct)) break;

            // Connection may have dropped while we were waiting
            if (!_client.IsConnected) continue;

            if (!_queue.Reader.TryRead(out var liveEvt)) continue;

            var liveMsg = BuildMessage(liveEvt);
            if (liveMsg is null) continue;

            try
            {
                await PublishAsync(liveMsg.Value.Topic, liveMsg.Value.Retain,
                                   liveMsg.Value.Payload, liveEvt.Type, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError("[MQTT] Publish exception: {Err}", ex.Message);
                // Rescue the event — broker disconnected mid-send, so buffer it for replay
                // on the next reconnect instead of silently dropping it.
                AppendToBuffer(liveMsg.Value.Topic, liveMsg.Value.Retain, liveMsg.Value.Payload);
            }
        }

        // On shutdown: buffer any remaining in-memory events so they survive restart
        while (_queue.Reader.TryRead(out var remaining))
        {
            var msg = BuildMessage(remaining);
            if (msg is not null)
                AppendToBuffer(msg.Value.Topic, msg.Value.Retain, msg.Value.Payload);
        }
    }

    // ─── Live publish ─────────────────────────────────────────────────────

    private async Task PublishAsync(string topic, bool retain, byte[] payload,
                                    string evtType, CancellationToken ct)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(retain)
            .Build();

        _log.LogDebug("[MQTT] Publishing {Type} → {Topic}", evtType, topic);
        var result = await _client.PublishAsync(msg, ct);

        // NoMatchingSubscribers (0x10) is an MQTT v5 success code — message reached the broker.
        if (result.ReasonCode is MqttClientPublishReasonCode.Success
                               or MqttClientPublishReasonCode.NoMatchingSubscribers)
            _log.LogDebug("[MQTT] Published  {Type} → {Topic}", evtType, topic);
        else
            _log.LogWarning("[MQTT] Publish rejected: type={Type} topic={Topic} reason={Reason}",
                evtType, topic, result.ReasonCode);
    }

    // ─── Offline buffer ───────────────────────────────────────────────────

    /// <summary>
    /// Appends one event to the offline JSONL buffer file.
    /// Each line: { "topic": "...", "retain": true, "payload": "<base64>" }
    /// </summary>
    private void AppendToBuffer(string topic, bool retain, byte[] payload)
    {
        if (_bufferPath is null) return;
        try
        {
            var entry = new BufferedEntry(topic, retain, Convert.ToBase64String(payload));
            var line  = JsonSerializer.Serialize(entry, P2LMqttJsonContext.Default.BufferedEntry);
            File.AppendAllText(_bufferPath, line + "\n");
            _log.LogDebug("[MQTT] Offline buffered → {Topic}", topic);
        }
        catch (Exception ex)
        {
            _log.LogWarning("[MQTT] Failed to write offline buffer: {Err}", ex.Message);
        }
    }

    /// <summary>
    /// Replays all lines from the offline buffer file to the broker.
    /// Successfully replayed lines are removed; the remainder is kept for the next attempt.
    /// The file is deleted when fully replayed.
    /// </summary>
    private async Task ReplayBufferAsync(CancellationToken ct)
    {
        if (_bufferPath is null || !File.Exists(_bufferPath)) return;

        string[] lines;
        try   { lines = File.ReadAllLines(_bufferPath); }
        catch { return; }

        int total = lines.Length;
        if (total == 0) { TryDeleteBuffer(); return; }

        _log.LogInformation("[MQTT] Replaying {N} offline buffered event(s) from {File}",
            total, Path.GetFileName(_bufferPath));

        int replayed = 0;
        foreach (var line in lines)
        {
            if (ct.IsCancellationRequested || !_client.IsConnected) break;
            if (string.IsNullOrWhiteSpace(line)) { replayed++; continue; }

            try
            {
                var entry = JsonSerializer.Deserialize(line,
                    P2LMqttJsonContext.Default.BufferedEntry);
                if (entry is null) { replayed++; continue; } // skip malformed lines

                var payload = Convert.FromBase64String(entry.Payload);
                var msg = new MqttApplicationMessageBuilder()
                    .WithTopic(entry.Topic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                    .WithRetainFlag(entry.Retain)
                    .Build();

                var result = await _client.PublishAsync(msg, ct);
                if (result.ReasonCode is MqttClientPublishReasonCode.Success
                                       or MqttClientPublishReasonCode.NoMatchingSubscribers)
                    replayed++;
                else
                {
                    _log.LogWarning("[MQTT] Buffer replay rejected: topic={Topic} reason={R}",
                        entry.Topic, result.ReasonCode);
                    break; // stop on broker rejection; retry next connect
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning("[MQTT] Buffer replay line {N} skipped: {Err}", replayed + 1, ex.Message);
                replayed++; // skip corrupted line rather than retrying forever
            }
        }

        if (replayed >= total)
        {
            _log.LogInformation("[MQTT] Buffer replay complete — {N} event(s) synced", replayed);
            TryDeleteBuffer();
        }
        else
        {
            // Persist only the un-replayed tail for the next reconnect attempt
            try { File.WriteAllLines(_bufferPath, lines.Skip(replayed)); }
            catch (Exception ex)
            {
                _log.LogWarning("[MQTT] Failed to truncate buffer after partial replay: {Err}", ex.Message);
            }
            _log.LogInformation("[MQTT] Buffer partially replayed: {Done}/{Total}; {Rem} event(s) remain",
                replayed, total, total - replayed);
        }
    }

    private void TryDeleteBuffer()
    {
        if (_bufferPath is null) return;
        try   { File.Delete(_bufferPath); }
        catch { /* best-effort */ }
    }
}

/// <summary>
/// One entry in the offline JSONL buffer file.
/// Stores the already-serialized payload as base64 so replay needs no re-serialization.
/// </summary>
internal sealed record BufferedEntry(string Topic, bool Retain, string Payload);
