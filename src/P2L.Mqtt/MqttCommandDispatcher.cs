using System.Text.Json;
using Microsoft.Extensions.Logging;
using P2L.Core;
using P2L.Core.Services;
using P2L.Mqtt.Messages;
using P2L.Shared.Models;

namespace P2L.Mqtt;

/// <summary>
/// Subscribes to MQTT command topics and dispatches to PickingEngine or CsvRunner.
/// </summary>
internal sealed class MqttCommandDispatcher
{
    private readonly PickingEngine       _engine;
    private readonly MqttEventPublisher  _publisher;
    private readonly PreemptionGate      _gate;
    private readonly CsvPickTaskRunner?  _csvRunner;
    private readonly DeviceLabelStore    _labels;
    private readonly DeviceScanService   _scanService;
    private readonly ILogger             _log;
    private readonly SemaphoreSlim        _commandLock = new(1, 1);

    public MqttCommandDispatcher(PickingEngine engine,
                                 MqttEventPublisher publisher,
                                 PreemptionGate gate,
                                 CsvPickTaskRunner? csvRunner,
                                 DeviceLabelStore labels,
                                 DeviceScanService scanService,
                                 ILogger logger)
    {
        _engine      = engine;
        _publisher   = publisher;
        _gate        = gate;
        _csvRunner   = csvRunner;
        _labels      = labels;
        _scanService = scanService;
        _log         = logger;
    }

    public async Task HandleAsync(byte[] payload, CancellationToken ct)
    {
        string type;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
        }
        catch (Exception ex)
        {
            _log.LogWarning("Malformed MQTT command payload: {Err}", ex.Message);
            return;
        }

        var lockTaken = false;
        _gate.Preempt();
        try
        {
            await _commandLock.WaitAsync(ct);
            lockTaken = true;

            switch (type)
            {
                case "light_on":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.LightOnCmd);
                    if (cmd is null) break;
                    var buzMode = cmd.BuzMode ?? cmd.LedMode;
                    await _engine.QuickLightAsync(cmd.Address, cmd.Quantity, cmd.Color, cmd.LedMode, buzMode, cmd.Label, cmd.BlockNo, cmd.CorrelationId, ct);
                    _log.LogInformation("[MQTT] light_on addr={A} qty={Q} block={B}", cmd.Address, cmd.Quantity, cmd.BlockNo);
                    break;
                }

                case "light_off":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.LightOffCmd);
                    if (cmd is null) break;
                    await _engine.ExtinguishAsync(cmd.Address, ct);
                    _log.LogInformation("[MQTT] light_off addr={A}", cmd.Address);
                    break;
                }

                case "batch_start":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.BatchStartCmd);
                    if (cmd is null) break;
                    var batchBuzMode = cmd.BuzMode ?? cmd.LedMode;
                    var instructions = new List<PickingInstruction>(cmd.Items.Count);
                    foreach (var i in cmd.Items)
                    {
                        // Resolve address: use explicit address, or look up by label.
                        var addr = i.Address;
                        if (string.IsNullOrWhiteSpace(addr))
                        {
                            if (string.IsNullOrWhiteSpace(i.Label)) continue;
                            addr = _labels.GetAddress(i.Label);
                            if (addr is null)
                            {
                                _log.LogWarning(
                                    "[MQTT] batch_start: label '{L}' not found in device database — item skipped",
                                    i.Label);
                                continue;
                            }
                        }
                        var itemLedMode = i.LedMode ?? cmd.LedMode;
                        var itemBuzMode = i.BuzMode ?? (i.LedMode.HasValue ? i.LedMode.Value : batchBuzMode);
                        instructions.Add(new PickingInstruction
                        {
                            Address  = addr,
                            Quantity = i.Quantity,
                            Color    = i.Color ?? cmd.Color,
                            LedMode  = itemLedMode,
                            BuzMode  = itemBuzMode,
                            BlockNo  = cmd.BlockNo,
                            Label    = i.Label,
                            CorrelationId = cmd.CorrelationId,
                        });
                    }
                    if (instructions.Count == 0)
                    {
                        _log.LogWarning("[MQTT] batch_start: no valid items after address resolution — command ignored");
                        break;
                    }
                    await _engine.StartBatchAsync(instructions, ct);
                    _log.LogInformation("[MQTT] batch_start count={C} block={B}", instructions.Count, cmd.BlockNo);
                    break;
                }

                case "batch_finish":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.BatchFinishCmd);
                    if (cmd is null) break;
                    await _engine.FinishBatchAsync(cmd.BlockNo, ct);
                    _log.LogInformation("[MQTT] batch_finish block={B}", cmd.BlockNo);
                    break;
                }

                case "terminate_all":
                    await _engine.FinishBatchAsync("00", ct);
                    _log.LogInformation("[MQTT] terminate_all");
                    break;

                case "get_snapshot":
                    _publisher.EnqueueSnapshot();
                    _log.LogInformation("[MQTT] get_snapshot — queued all modules");
                    break;

                case "csv_run":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.CsvRunCmd);
                    if (cmd is null || _csvRunner is null) break;

                    _csvRunner.Start(cmd.File, cmd.Color);
                    _log.LogInformation("[MQTT] csv_run file={F}", cmd.File);
                    break;
                }

                case "csv_control":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.CsvControlCmd);
                    if (cmd is null || _csvRunner is null) break;

                    switch (cmd.Action.ToLowerInvariant())
                    {
                        case "pause":  _csvRunner.Pause();  break;
                        case "resume": _csvRunner.Resume(); break;
                        case "cancel": await _csvRunner.CancelAsync(); break;
                    }
                    _log.LogInformation("[MQTT] csv_control action={A}", cmd.Action);
                    break;
                }

                case "scan_devices":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.ScanDevicesCmd);
                    if (cmd is null) break;

                    await _scanService.ScanAsync(cmd.From, cmd.To, ct: ct);
                    _log.LogInformation("[MQTT] scan_devices from={F} to={T}", cmd.From, cmd.To);
                    break;
                }

                default:
                    _log.LogWarning("[MQTT] Unknown command type: {T}", type);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _log.LogInformation("[MQTT] Command dispatch cancelled: {T}", type);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[MQTT] Command dispatch failed: {T}", type);
        }
        finally
        {
            if (lockTaken)
                _commandLock.Release();
            _gate.Release();
        }
    }
}
