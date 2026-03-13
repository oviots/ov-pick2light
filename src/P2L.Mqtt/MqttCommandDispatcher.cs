using System.Text.Json;
using Microsoft.Extensions.Logging;
using P2L.Core;
using P2L.Core.Services;
using P2L.Mqtt.Messages;
using P2L.Shared.Models;

namespace P2L.Mqtt;

/// <summary>
/// Parses inbound MQTT command payloads and dispatches them to <see cref="PickingEngine"/>.
/// Uses a two-step AOT-safe JSON strategy: peek at the "type" field via JsonDocument,
/// then deserialize the full payload into the concrete command type.
/// </summary>
internal sealed class MqttCommandDispatcher
{
    private readonly PickingEngine       _engine;
    private readonly MqttEventPublisher  _publisher;
    private readonly ILogger             _log;
    private readonly PreemptionGate      _gate;
    private readonly CsvPickTaskRunner?  _csvRunner;

    public MqttCommandDispatcher(PickingEngine engine, MqttEventPublisher publisher,
                                  PreemptionGate gate, CsvPickTaskRunner? csvRunner,
                                  ILogger<MqttCommandDispatcher> log)
    {
        _engine    = engine;
        _publisher = publisher;
        _gate      = gate;
        _csvRunner = csvRunner;
        _log       = log;
    }

    public async Task HandleAsync(byte[] payload, CancellationToken ct)
    {
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            type = doc.RootElement.TryGetProperty("type", out var typeProp)
                 ? typeProp.GetString()
                 : null;
        }
        catch (JsonException ex)
        {
            _log.LogWarning("Malformed MQTT command payload: {Err}", ex.Message);
            return;
        }

        _gate.Preempt();
        try
        {
            switch (type)
            {
                case "light_on":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.LightOnCmd);
                    if (cmd is null) break;
                    await _engine.QuickLightAsync(cmd.Address, cmd.Quantity,
                        cmd.Color, cmd.LedMode, cmd.LedMode, ct);
                    _log.LogInformation("[MQTT] light_on addr={A} qty={Q} color={C} mode={M}",
                        cmd.Address, cmd.Quantity, cmd.Color, cmd.LedMode);
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
                    var instructions = cmd.Items.Select(i => new PickingInstruction
                    {
                        Address  = i.Address,
                        Quantity = i.Quantity,
                        Color    = i.Color ?? cmd.Color,   // per-item color, falls back to batch default
                        LedMode  = cmd.LedMode,
                        Label    = i.Label,
                    }).ToList();
                    await _engine.StartBatchAsync(instructions, ct);
                    _log.LogInformation("[MQTT] batch_start count={C}", instructions.Count);
                    break;
                }

                case "batch_finish":
                {
                    await _engine.FinishBatchAsync(ct: ct);
                    _log.LogInformation("[MQTT] batch_finish");
                    break;
                }

                case "terminate_all":
                    await _engine.FinishBatchAsync("00", ct);
                    _log.LogInformation("[MQTT] terminate_all");
                    break;

                case "get_snapshot":
                    _publisher.EnqueueSnapshot();
                    _log.LogInformation("[MQTT] get_snapshot — queued {N} modules", 0);
                    break;

                case "csv_run":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.CsvRunCmd);
                    if (cmd is null || _csvRunner is null) break;
                    if (_csvRunner.IsRunning)
                    {
                        _log.LogWarning("[MQTT] csv_run ignored — runner already active");
                        break;
                    }
                    _csvRunner.Start(cmd.File, cmd.Color);
                    _log.LogInformation("[MQTT] csv_run file={F} color={C}", cmd.File, cmd.Color);
                    break;
                }

                case "csv_control":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.CsvControlCmd);
                    if (cmd is null || _csvRunner is null) break;
                    switch (cmd.Action.ToLowerInvariant())
                    {
                        case "pause":
                            _csvRunner.Pause();
                            _log.LogInformation("[MQTT] csv_control pause");
                            break;
                        case "resume":
                            _csvRunner.Resume();
                            _log.LogInformation("[MQTT] csv_control resume");
                            break;
                        case "cancel":
                            await _csvRunner.CancelAsync();
                            _log.LogInformation("[MQTT] csv_control cancel");
                            break;
                    }
                    break;
                }

                default:
                    _log.LogWarning("[MQTT] Unknown command type: {Type}", type ?? "(null)");
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "[MQTT] Error executing command type={Type}", type);
        }
        finally
        {
            _gate.Release();
        }
    }
}
