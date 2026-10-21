using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
    private readonly CommandReceiptStore _receipts;
    private readonly int _diagnosticTimeoutMs;

    public MqttCommandDispatcher(PickingEngine engine,
                                 MqttEventPublisher publisher,
                                 PreemptionGate gate,
                                 CsvPickTaskRunner? csvRunner,
                                 DeviceLabelStore labels,
                                 DeviceScanService scanService,
                                 ILogger logger, CommandReceiptStore? receipts = null, int diagnosticTimeoutMs = 15000)
    {
        _engine      = engine;
        _publisher   = publisher;
        _gate        = gate;
        _csvRunner   = csvRunner;
        _labels      = labels;
        _scanService = scanService;
        _log         = logger;
        _receipts = receipts ?? new CommandReceiptStore(Path.Combine(AppContext.BaseDirectory, "p2l_commands.db"));
        if (diagnosticTimeoutMs is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(diagnosticTimeoutMs));
        _diagnosticTimeoutMs = diagnosticTimeoutMs;
    }

    public async Task HandleAsync(byte[] payload, CancellationToken ct)
    {
        string type = "";
        string? commandId = null;
        string? correlationId = null;
        string? receiptKey = null;
        bool intentRecorded = false;
        bool executionStarted = false;
        bool lockTaken = false;
        bool gateTaken = false;
        CommandResultEvt? outcome = null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            type = doc.RootElement.GetProperty("type").GetString() ?? "";
            commandId = doc.RootElement.TryGetProperty("commandId", out var id) ? id.GetString() : null;
            correlationId = doc.RootElement.TryGetProperty("correlationId", out var correlation) ? correlation.GetString() : null;
            if (commandId is not null && (string.IsNullOrWhiteSpace(commandId) || commandId.Length > 200))
                throw new PickingCommandRejectedException("INVALID_COMMAND_ID", "Command ID must contain 1–200 characters.");
            var hash = Convert.ToHexString(SHA256.HashData(payload));
            receiptKey = commandId is not null ? "command:" + commandId
                : !string.IsNullOrWhiteSpace(correlationId) && (type is "batch_start" or "light_on")
                    ? "legacy:" + hash : null;
            // An expired retry must return its original durable outcome, not pretend
            // the earlier hardware operation was never applied.
            if (receiptKey is not null && _receipts.GetExisting(receiptKey, hash, commandId, correlationId) is { } cached)
            {
                outcome = cached;
                return;
            }
            DateTimeOffset? executionDeadline = null;
            if (doc.RootElement.TryGetProperty("executionDeadlineAt", out var deadline) && deadline.ValueKind != JsonValueKind.Null)
                executionDeadline = deadline.GetDateTimeOffset();
            if (executionDeadline <= DateTimeOffset.UtcNow)
                throw new PickingCommandRejectedException("COMMAND_DEADLINE_EXCEEDED", "Execution budget elapsed before dispatch.");
            DateTimeOffset? expiresAt = null;
            if (doc.RootElement.TryGetProperty("expiresAt", out var expiry) && expiry.ValueKind != JsonValueKind.Null)
                expiresAt = expiry.GetDateTimeOffset();
            if (commandId is not null && expiresAt is null)
                throw new PickingCommandRejectedException("MISSING_EXPIRY", "An identified command requires expiresAt.");
            if (expiresAt <= DateTimeOffset.UtcNow)
                throw new PickingCommandRejectedException("COMMAND_EXPIRED", "Command has expired.");
            if (type == "scan_devices" && _engine.HasActiveWork)
                throw new PickingCommandRejectedException("BRIDGE_BUSY", "Device scan is unavailable while picking work is active.");

            _gate.Preempt();
            gateTaken = true;
            await _commandLock.WaitAsync(ct);
            lockTaken = true;
            ct.ThrowIfCancellationRequested();
            // Commands can wait behind other hardware work; recheck immediately
            // before recording intent and performing any operation.
            if (expiresAt <= DateTimeOffset.UtcNow)
                throw new PickingCommandRejectedException("COMMAND_EXPIRED", "Command expired while waiting for dispatch.");
            if (executionDeadline <= DateTimeOffset.UtcNow)
                throw new PickingCommandRejectedException("COMMAND_DEADLINE_EXCEEDED", "Execution budget elapsed while waiting for dispatch.");
            if (type == "scan_devices" && _engine.HasActiveWork)
                throw new PickingCommandRejectedException("BRIDGE_BUSY", "Device scan is unavailable while picking work is active.");
            if (receiptKey is not null)
            {
                outcome = _receipts.TryBegin(receiptKey, hash, commandId, correlationId);
                if (outcome is not null) return;
                intentRecorded = true;
            }

            switch (type)
            {
                case "light_on":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.LightOnCmd);
                    if (cmd is null) break;
                    var buzMode = cmd.BuzMode ?? cmd.LedMode ?? P2L.Shared.Enums.LightState.On;
                    executionStarted = true;
                    await _engine.QuickLightAsync(cmd.Address, cmd.Quantity, cmd.Color ?? P2L.Shared.Enums.LightColor.Green, cmd.LedMode ?? P2L.Shared.Enums.LightState.On, buzMode, cmd.Label, cmd.BlockNo, cmd.CorrelationId, ct, cmd.CommandItemId, cmd.PickingDetailId);
                    _log.LogInformation("[MQTT] light_on addr={A} qty={Q} block={B}", cmd.Address, cmd.Quantity, cmd.BlockNo);
                    break;
                }

                case "light_off":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.LightOffCmd);
                    if (cmd is null) break;
                    executionStarted = true;
                    await _engine.ExtinguishAsync(cmd.Address, ct);
                    _log.LogInformation("[MQTT] light_off addr={A}", cmd.Address);
                    break;
                }

                case "resolve_confirmation":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.ResolveConfirmationCmd)
                        ?? throw new PickingCommandRejectedException("INVALID_COMMAND", "Recovery command is required.");
                    if (string.IsNullOrWhiteSpace(cmd.CommandId) || string.IsNullOrWhiteSpace(cmd.CorrelationId))
                        throw new PickingCommandRejectedException("INVALID_RECOVERY", "Recovery requires a command ID and the original session.");
                    executionStarted = true;
                    await _engine.ResolveConfirmationAsync(cmd.Address, cmd.BlockNo, cmd.CorrelationId,
                        cmd.CommandItemId, cmd.PickingDetailId, cmd.MissingEventId, cmd.Action,
                        cmd.ActualQuantity, cmd.CommandId, ct);
                    _log.LogInformation("[MQTT] resolve_confirmation addr={Address} action={Action} session={Session}",
                        cmd.Address, cmd.Action, cmd.CorrelationId);
                    break;
                }

                case "release_session":
                {
                    var cmd = JsonSerializer.Deserialize(payload, P2LMqttJsonContext.Default.ReleaseSessionCmd)
                        ?? throw new PickingCommandRejectedException("INVALID_COMMAND", "Session release is required.");
                    if (string.IsNullOrWhiteSpace(cmd.CommandId) || string.IsNullOrWhiteSpace(cmd.CorrelationId))
                        throw new PickingCommandRejectedException("INVALID_RELEASE", "Release requires a command ID and the original session.");
                    executionStarted = true;
                    await _engine.ReleaseSessionAsync(cmd.BlockNo, cmd.CorrelationId, ct);
                    _log.LogInformation("[MQTT] release_session block={Block} session={Session}", cmd.BlockNo, cmd.CorrelationId);
                    break;
                }

                case "batch_start":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.BatchStartCmd);
                    if (cmd is null) break;
                    if (cmd.Items is null) throw new PickingCommandRejectedException("INVALID_ITEMS", "Batch requires an item list.");
                    var batchLedMode = cmd.LedMode ?? P2L.Shared.Enums.LightState.On;
                    var batchBuzMode = cmd.BuzMode ?? batchLedMode;
                    var instructions = new List<PickingInstruction>(cmd.Items.Count);
                    foreach (var i in cmd.Items)
                    {
                        // Resolve address: use explicit address, or look up by label.
                        var addr = i.Address;
                        if (string.IsNullOrWhiteSpace(addr))
                        {
                            if (string.IsNullOrWhiteSpace(i.Label)) throw new PickingCommandRejectedException("INVALID_ADDRESS", "Each batch item needs an address or resolvable label.");
                            addr = _labels.GetAddress(i.Label);
                            if (addr is null)
                            {
                                throw new PickingCommandRejectedException("UNKNOWN_LABEL", $"Label '{i.Label}' is not registered.");
                            }
                        }
                        var itemLedMode = i.LedMode ?? batchLedMode;
                        var itemBuzMode = i.BuzMode ?? (i.LedMode.HasValue ? i.LedMode.Value : batchBuzMode);
                        instructions.Add(new PickingInstruction
                        {
                            Address  = addr,
                            Quantity = i.Quantity,
                            Color    = i.Color ?? cmd.Color ?? P2L.Shared.Enums.LightColor.Green,
                            LedMode  = itemLedMode,
                            BuzMode  = itemBuzMode,
                            BlockNo  = cmd.BlockNo ?? "00",
                            Label    = i.Label,
                            CorrelationId = cmd.CorrelationId,
                            CommandItemId = i.CommandItemId,
                            PickingDetailId = i.PickingDetailId,
                        });
                    }
                    if (instructions.Count == 0)
                    {
                        throw new PickingCommandRejectedException("INVALID_ITEMS", "Batch contains no items.");
                    }
                    PickingEngine.ValidateInstructions(instructions);
                    executionStarted = true;
                    await _engine.StartBatchAsync(instructions, ct, cmd.Relight);
                    _log.LogInformation("[MQTT] batch_start count={C} block={B}", instructions.Count, cmd.BlockNo);
                    break;
                }

                case "batch_finish":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.BatchFinishCmd);
                    if (cmd is null) break;
                    executionStarted = true;
                    await _engine.FinishBatchAsync(cmd.BlockNo, ct);
                    _log.LogInformation("[MQTT] batch_finish block={B}", cmd.BlockNo);
                    break;
                }

                case "terminate_all":
                    executionStarted = true;
                    await _engine.EmergencyStopAsync(ct);
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
                    if (cmd is null || _csvRunner is null) throw new PickingCommandRejectedException("CSV_UNAVAILABLE", "CSV runner is not available.");

                    executionStarted = true;
                    _csvRunner.Start(cmd.File, cmd.Color);
                    _log.LogInformation("[MQTT] csv_run file={F}", cmd.File);
                    break;
                }

                case "csv_control":
                {
                    var cmd = JsonSerializer.Deserialize(payload,
                        P2LMqttJsonContext.Default.CsvControlCmd);
                    if (cmd is null || _csvRunner is null) throw new PickingCommandRejectedException("CSV_UNAVAILABLE", "CSV runner is not available.");

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

                    executionStarted = true;
                    using var diagnostic = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    diagnostic.CancelAfter(_diagnosticTimeoutMs);
                    await _scanService.ScanAsync(cmd.From, cmd.To, ct: diagnostic.Token);
                    _log.LogInformation("[MQTT] scan_devices from={F} to={T}", cmd.From, cmd.To);
                    break;
                }

                default:
                    throw new PickingCommandRejectedException("UNKNOWN_COMMAND", $"Unknown command type: {type}");
            }
        }
        catch (PickingCommandRejectedException ex)
        {
            outcome = Result("rejected", ex.Code, ex.Message);
            _log.LogWarning("[MQTT] Rejected {Type}: {Code} {Message}", type, ex.Code, ex.Message);
        }
        catch (Exception ex) when (!executionStarted && ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            outcome = Result("rejected", "INVALID_COMMAND", ex.Message);
            _log.LogWarning("[MQTT] Invalid command {Type}: {Message}", type, ex.Message);
        }
        catch (Exception ex) when (executionStarted || ex is OperationCanceledException)
        {
            outcome = Result(executionStarted ? "uncertain" : "rejected", executionStarted ? "EXECUTION_UNCERTAIN" : "COMMAND_CANCELLED", ex.Message);
            _log.LogError(ex, "[MQTT] Command {Type}: {Status}", type, outcome.Status);
        }
        catch (Exception ex)
        {
            outcome = Result("uncertain", "PERSISTENCE_FAILED", ex.Message);
            throw;
        }
        finally
        {
            try
            {
                // Commit outcome before publishing it. Disk failure propagates;
                // the executing receipt then prevents a blind retry after restart.
                outcome ??= Result("applied", null, null);
                if (intentRecorded && receiptKey is not null) _receipts.Complete(receiptKey, outcome);
                else if (receiptKey is not null && outcome.Status == "rejected")
                    outcome = _receipts.GetExistingOrReject(receiptKey, Convert.ToHexString(SHA256.HashData(payload)), commandId, correlationId, outcome);
                _publisher.EnqueueCommandResult(outcome);
            }
            finally
            {
                if (lockTaken) _commandLock.Release();
                if (gateTaken) _gate.Release();
            }
        }

        CommandResultEvt Result(string status, string? errorCode, string? message) => new()
        {
            Type = "command_result", CommandId = commandId, CorrelationId = correlationId,
            Status = status, ErrorCode = errorCode, Message = message,
        };
    }
}
