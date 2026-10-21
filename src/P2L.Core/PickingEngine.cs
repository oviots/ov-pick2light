using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Constants;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>
/// Orchestrates picking sessions: sends batch instructions to the gateway,
/// tracks confirmations, and provides completion callbacks.
/// Supports multiple concurrent batches indexed by BlockNo.
/// </summary>
public sealed class PickingEngine : IDisposable
{
    private sealed class BatchState
    {
        public required string BlockNo
        {
            get; init;
        }
        public required string? CorrelationId
        {
            get; init;
        }
        public required string Signature
        {
            get; init;
        }
        public HashSet<string> PendingAddresses { get; } = new();
        public bool HardwareAttempted
        {
            get; set;
        }
        public bool Applied
        {
            get; set;
        }
    }

    private readonly AioiGateway _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger<PickingEngine> _log;
    private readonly int _executionTimeoutMs;

    private readonly ConcurrentDictionary<string, BatchState> _activeBatches = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly HashSet<string> _unpersistedMissingAddresses = new(StringComparer.Ordinal);
    private sealed record StatusMismatch(LightModuleState Instruction, ModuleStatus ObservedStatus, DateTime FirstObservedAt);
    private readonly Dictionary<string, StatusMismatch> _statusMismatches = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private string _lastProbedAddress = "";

    /// <summary>Fires when an operator presses CONFIRM on any module in any active batch.</summary>
    public event Action<ConfirmationEvent, LightModuleState?>? ItemConfirmed;

    /// <summary>Durable event sinks only; ordinary/CSV observers run after this succeeds.</summary>
    public event Action<ConfirmationEvent, LightModuleState?>? DurableConfirmation;
    private int _requiresDurableConfirmationConsumer;

    public void RequireDurableConfirmationConsumer() => Volatile.Write(ref _requiresDurableConfirmationConsumer, 1);

    /// <summary>Fires when every module in a specific batch has been confirmed.</summary>
    public event Action<string?>? BatchCompleted;
    public event Action<BatchCompletionEvent>? DurableBatchCompleted;
    public event Action<ConfirmationMissingEvent>? DurableConfirmationMissing;

    /// <summary>True if any batch is currently active.</summary>
    public bool IsBatchActive => !_activeBatches.IsEmpty || _registry.PendingConfirmations.Count > 0 || _registry.PendingBatchCompletions.Count > 0
        || _registry.All.Any(m => m.Status == ModuleStatus.Operating && m.LastInstructionAt is not null);

    public bool HasActiveWork => IsBatchActive || _registry.ActiveCount > 0;

    public PickingEngine(AioiGateway gateway, LightModuleRegistry registry, ILogger<PickingEngine> logger,
        int executionTimeoutMs = 25000, TimeProvider? clock = null)
    {
        _gateway = gateway;
        _registry = registry;
        _log = logger;
        if (executionTimeoutMs is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(executionTimeoutMs));
        _executionTimeoutMs = executionTimeoutMs;
        _clock = clock ?? TimeProvider.System;

        _gateway.Confirmed += OnConfirmed;
    }

    // One controller mutation is in flight per engine. Confirmation capture never
    // takes this async gate, so the receive loop can complete pending hardware work.
    public Task StartBatchAsync(IReadOnlyList<PickingInstruction> instructions, CancellationToken ct = default, bool relight = false)
        => RunOperationAsync(token => StartBatchCoreAsync(instructions, token, relight), ct);

    public Task FinishBatchAsync(string blockNo = "00", CancellationToken ct = default)
        => RunOperationAsync(token => FinishBatchCoreAsync(blockNo, token), ct);

    /// <summary>Closes exactly one WMS owner, without fabricating picked/completed events.</summary>
    public Task ReleaseSessionAsync(string blockNo, string correlationId, CancellationToken ct = default)
        => RunOperationAsync(token => ReleaseSessionCoreAsync(blockNo, correlationId, token), ct);

    private async Task ReleaseSessionCoreAsync(string blockNo, string correlationId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(blockNo) || blockNo.Length != 2 || !blockNo.All(char.IsAsciiDigit)
            || string.IsNullOrWhiteSpace(correlationId))
            throw new PickingCommandRejectedException("INVALID_SESSION", "Session release requires an exact block and correlation ID.");
        LightModuleState[] captured;
        lock (_lock)
        {
            RestoreActiveBatches();
            captured = _registry.All.Where(m => m.Status == ModuleStatus.Operating
                && m.BlockNo == blockNo && m.CorrelationId == correlationId).ToArray();
            // Fence old intents before the first D: a later D failure must not
            // let a delayed relight turn the already-stopped subset on again.
            _registry.BeginSessionClose(blockNo, correlationId);
        }
        // Per-address D is a drain barrier for the existing owner; never use a
        // block/global stop that could extinguish a newer or unrelated session.
        foreach (var instruction in captured)
            await _gateway.TerminateAsync(instruction.Address, ct);
        lock (_lock)
        {
            PublishMissingConfirmations(blockNo, correlationId);
            foreach (var pending in _registry.PendingConfirmations.Where(i => i.PendingState?.BlockNo == blockNo
                && i.PendingState.CorrelationId == correlationId))
                PublishPending(pending);
            PublishBatchCompletions(blockNo, correlationId);
            _registry.CloseSession(blockNo, correlationId);
            foreach (var instruction in captured)
            {
                _unpersistedMissingAddresses.Remove(instruction.Address);
                _statusMismatches.Remove(instruction.Address);
            }
            if (_activeBatches.TryGetValue(blockNo, out var batch) && batch.CorrelationId == correlationId)
            {
                batch.PendingAddresses.Clear();
                RemoveEmptyBatchIfSettled(batch);
            }
        }
    }

    public Task QuickLightAsync(string address, int quantity, LightColor color = LightColor.Green,
        LightState ledMode = LightState.On, LightState buzMode = LightState.Off, string? label = null,
        string? blockNo = "00", string? correlationId = null, CancellationToken ct = default,
        string? commandItemId = null, string? pickingDetailId = null)
        => RunOperationAsync(token => QuickLightCoreAsync(address, quantity, color, ledMode, buzMode, label,
            blockNo, correlationId, token, commandItemId, pickingDetailId), ct);

    public Task ConfigureModuleAsync(string address, int? newQuantity, ModuleParameters parameters, CancellationToken ct = default)
        => RunOperationAsync(token => ConfigureModuleCoreAsync(address, newQuantity, parameters, token), ct);

    public Task ExtinguishAsync(string address, CancellationToken ct = default)
        => RunOperationAsync(token => ExtinguishCoreAsync(address, token), ct);

    /// <summary>Physical emergency stop; uncertain items keep their business ownership.</summary>
    public Task EmergencyStopAsync(CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            LightModuleState[] captured;
            lock (_lock) captured = _registry.All.Where(m => m.Status == ModuleStatus.Operating).ToArray();
            await _gateway.TerminateBlockAsync("00", token);
            lock (_lock)
            {
                foreach (var instruction in captured)
                {
                    var current = _registry.Get(instruction.Address);
                    if (current is null || !LightModuleRegistry.SameInstruction(current, instruction)
                        || NeedsConfirmationRecovery(current)) continue;
                    if (current.Status is not (ModuleStatus.Completed or ModuleStatus.Shortage))
                        _registry.SetTerminated(current.Address);
                    if (_activeBatches.TryGetValue(instruction.BlockNo, out var batch)
                        && batch.CorrelationId == instruction.CorrelationId)
                    {
                        if (!_registry.HasPendingConfirmation(instruction.Address))
                            batch.PendingAddresses.Remove(instruction.Address);
                        RemoveEmptyBatchIfSettled(batch);
                    }
                }
            }
        }, ct);

    /// <summary>Explicit exact-owner recovery; status observations never call this automatically.</summary>
    public Task ResolveConfirmationAsync(string address, string blockNo, string correlationId,
        string? commandItemId, string? pickingDetailId, string missingEventId, string action,
        int? actualQuantity, string resolutionId, CancellationToken ct = default)
        => RunOperationAsync(token => ResolveConfirmationCoreAsync(address, blockNo, correlationId,
            commandItemId, pickingDetailId, missingEventId, action, actualQuantity, resolutionId, token), ct);

    /// <summary>Bounded observations. Yields the operation gate between addresses to business commands.</summary>
    public async Task<int> ProbeActiveConfirmationsAsync(int maxItems = 4, int minimumAgeMs = 15000,
        int probeTimeoutMs = 1500, int mismatchGraceMs = 10000, CancellationToken ct = default)
    {
        if (maxItems is < 1 or > 16 || minimumAgeMs < 0 || probeTimeoutMs is < 1 or > 5000 || mismatchGraceMs < 0)
            throw new ArgumentOutOfRangeException(nameof(maxItems));
        LightModuleState[] candidates;
        lock (_lock)
        {
            try { PublishMissingConfirmations(); }
            catch (Exception ex) { _log.LogWarning(ex, "Missing-confirmation delivery remains pending"); }
            var active = _registry.All.Where(m => m.Status == ModuleStatus.Operating
                && !string.IsNullOrWhiteSpace(m.CorrelationId)
                && (!string.IsNullOrEmpty(m.CommandItemId) || !string.IsNullOrEmpty(m.PickingDetailId))
                && m.LastInstructionAt <= _clock.GetUtcNow().UtcDateTime.AddMilliseconds(-minimumAgeMs)
                && m.ConfirmationMissingAt is null).OrderBy(m => m.Address, StringComparer.Ordinal).ToArray();
            foreach (var stale in _statusMismatches.Where(pair => !active.Any(m =>
                LightModuleRegistry.SameInstruction(m, pair.Value.Instruction))).Select(pair => pair.Key).ToArray())
                _statusMismatches.Remove(stale);
            candidates = active.Where(m => string.CompareOrdinal(m.Address, _lastProbedAddress) > 0)
                .Concat(active.Where(m => string.CompareOrdinal(m.Address, _lastProbedAddress) <= 0))
                .Take(maxItems).ToArray();
        }
        int probed = 0;
        foreach (var instruction in candidates)
        {
            if (!await _operationGate.WaitAsync(0, ct)) break;
            try
            {
                lock (_lock)
                {
                    var current = _registry.Get(instruction.Address);
                    if (current is null || current.Status != ModuleStatus.Operating
                        || !LightModuleRegistry.SameInstruction(current, instruction)) continue;
                    _lastProbedAddress = instruction.Address;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(probeTimeoutMs);
                probed++;
                var response = await _gateway.QueryModuleStatusAsync(instruction.Address, timeout.Token);
                if (response.ModuleInfo is not { } info) continue;
                lock (_lock)
                {
                    var observedStatus = _unpersistedMissingAddresses.Contains(instruction.Address)
                        && _statusMismatches.TryGetValue(instruction.Address, out var failedSave)
                        ? failedSave.ObservedStatus : info.Status;
                    if (observedStatus is ModuleStatus.Operating or ModuleStatus.Operating2)
                    {
                        _statusMismatches.Remove(instruction.Address);
                        continue;
                    }
                    var now = _clock.GetUtcNow().UtcDateTime;
                    if (!_statusMismatches.TryGetValue(instruction.Address, out var mismatch)
                        || !LightModuleRegistry.SameInstruction(mismatch.Instruction, instruction)
                        || mismatch.ObservedStatus != observedStatus)
                    {
                        _statusMismatches[instruction.Address] = new StatusMismatch(instruction, observedStatus, now);
                        if (mismatchGraceMs > 0) continue;
                    }
                    else if ((now - mismatch.FirstObservedAt).TotalMilliseconds < mismatchGraceMs)
                        continue;
                    try
                    {
                        _registry.SetConfirmationMissing(instruction, observedStatus);
                        _unpersistedMissingAddresses.Remove(instruction.Address);
                        _statusMismatches.Remove(instruction.Address);
                    }
                    catch
                    {
                        // Disk failure must not make a known uncertain address eligible for P1/PP5.
                        _unpersistedMissingAddresses.Add(instruction.Address);
                        throw;
                    }
                    PublishMissingConfirmations();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            catch (TimeoutException) { }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Active status observation failed for {Address}", instruction.Address);
            }
            finally { _operationGate.Release(); }
        }
        return probed;
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation, CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_executionTimeoutMs);
        bool entered = false;
        try
        {
            await _operationGate.WaitAsync(budget.Token);
            entered = true;
            await operation(budget.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Hardware operation exceeded its execution budget; reconcile any partial I/O.");
        }
        finally
        {
            if (entered) _operationGate.Release();
        }
    }

    /// <summary>CSV cleanup is restricted to the exact owner and addresses it created.</summary>
    public Task FinishOwnedBatchAsync(string correlationId, IReadOnlyList<string> addresses, CancellationToken ct = default)
        => RunOperationAsync(async token =>
        {
            lock (_lock)
                if (addresses.Select(_registry.Get).Any(m => m is not null
                    && m.CorrelationId == correlationId && NeedsConfirmationRecovery(m)))
                    throw new PickingCommandRejectedException("CONFIRMATION_RECOVERY_REQUIRED", "Resolve missing confirmations before finishing this owner.");
            foreach (var address in addresses)
            {
                var current = _registry.Get(address);
                if (current?.CorrelationId != correlationId)
                    continue;
                await _gateway.TerminateAsync(address, token);
                lock (_lock)
                {
                    _registry.SetTerminated(address);
                    if (_activeBatches.TryGetValue(current.BlockNo, out var batch) && batch.CorrelationId == correlationId)
                    {
                        batch.PendingAddresses.Remove(Normalize(address));
                        if (batch.PendingAddresses.Count == 0)
                            RemoveBatchIfCurrent(current.BlockNo, batch);
                    }
                }
            }
        }, ct);

    private void RestoreActiveBatches()
    {
        // A pending event restored/retried outside its original call must never
        // be mistaken for a new standalone, local-only confirmation.
        if (_registry.PendingConfirmations.Count > 0 || _registry.PendingBatchCompletions.Count > 0)
            RequireDurableConfirmationConsumer();
        foreach (var group in _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.LastInstructionAt is not null)
                     .Concat(_registry.PendingConfirmations.Select(i => i.PendingState!))
                     .GroupBy(m => m.BlockNo))
        {
            if (_activeBatches.ContainsKey(group.Key))
                continue;
            var owners = group.Select(m => m.CorrelationId).Distinct(StringComparer.Ordinal).ToArray();
            if (owners.Length != 1)
                throw new InvalidOperationException("Restored block has conflicting owners; reconcile before dispatch.");
            var restored = new BatchState { BlockNo = group.Key, CorrelationId = owners[0], Signature = "", HardwareAttempted = true };
            foreach (var module in group)
                restored.PendingAddresses.Add(module.Address);
            _activeBatches[group.Key] = restored;
        }
        foreach (var completion in _registry.PendingBatchCompletions)
        {
            if (_activeBatches.TryGetValue(completion.BlockNo, out var existing))
            {
                if (existing.CorrelationId != completion.CorrelationId)
                    throw new InvalidOperationException("Restored block has conflicting completion ownership; reconcile before dispatch.");
                continue;
            }
            _activeBatches[completion.BlockNo] = new BatchState
            {
                BlockNo = completion.BlockNo, CorrelationId = completion.CorrelationId,
                Signature = "", HardwareAttempted = true
            };
        }
    }

    private void ValidateGlobalOwnership(string blockNo, string? correlationId)
    {
        if (_activeBatches.Values.Any(b => (b.BlockNo == "00" || blockNo == "00")
            && (b.BlockNo != blockNo || b.CorrelationId != correlationId)))
            throw new PickingCommandRejectedException("BLOCK_BUSY", "Global block work cannot overlap another live block.");
    }

    // ─── Batch operations ─────────────────────────────────────────────────

    /// <summary>
    /// Lights up a batch of modules and begins tracking confirmations.
    /// If a batch for the specified block is already active, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task StartBatchCoreAsync(IReadOnlyList<PickingInstruction> instructions,
                                      CancellationToken ct = default, bool relight = false)
    {
        if (instructions.Count == 0)
            return;
        ValidateInstructions(instructions);
        var blockNo = instructions[0].BlockNo;
        var correlationId = instructions.Select(i => i.CorrelationId).Distinct(StringComparer.Ordinal).Single();
        var signature = BuildBatchSignature(instructions);
        var effective = instructions;
        var toInstruct = instructions;
        BatchState batch;
        lock (_lock)
        {
            _registry.ThrowIfSessionClosed(blockNo, correlationId);
            RestoreActiveBatches();
            ValidateGlobalOwnership(blockNo, correlationId);
            ValidateOwnership(instructions);
            if (!relight && instructions.Any(_registry.IsCompletedItem))
                throw new PickingCommandRejectedException("ITEM_COMPLETED", "Batch contains an item that already completed.");
            if (_activeBatches.TryGetValue(blockNo, out var existing))
            {
                if (existing.CorrelationId != correlationId)
                    throw new PickingCommandRejectedException("BLOCK_BUSY", $"Block {blockNo} belongs to another active session.");
                if (!relight)
                {
                    if (existing.Signature == signature)
                    {
                        if (existing.Applied)
                            return;
                        if (existing.HardwareAttempted)
                            throw new InvalidOperationException("Previous batch delivery is uncertain; use an explicit reconciled relight.");
                    }
                    else
                        throw new PickingCommandRejectedException("BLOCK_BUSY", $"A different batch for block {blockNo} is already active.");
                }
            }
            if (relight)
            {
                // A confirmation may finish after WMS prepares the relight but
                // before it reaches this bridge. Skip exactly that completed item.
                toInstruct = instructions.Where(inst =>
                {
                    if (_registry.IsCompletedItem(inst))
                        return false;
                    var current = _registry.Get(inst.Address);
                    var sameItem = !string.IsNullOrEmpty(inst.CommandItemId)
                        ? current?.CommandItemId == inst.CommandItemId
                        : !string.IsNullOrEmpty(inst.PickingDetailId) && current?.PickingDetailId == inst.PickingDetailId;
                    if (current?.Status == ModuleStatus.Operating && current.CorrelationId == inst.CorrelationId
                        && !string.IsNullOrEmpty(current.CommandItemId) && !string.IsNullOrEmpty(inst.CommandItemId)
                        && current.CommandItemId != inst.CommandItemId)
                        return false; // A later detail already owns this address.
                    return !(current is not null && current.Status is ModuleStatus.Completed or ModuleStatus.Shortage
                        && current.CorrelationId == inst.CorrelationId && sameItem);
                }).ToArray();
                // An explicit relight applies the full live set of this block only.
                var live = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == blockNo && !NeedsConfirmationRecovery(m))
                    .Select(ToInstruction).ToDictionary(i => Normalize(i.Address), StringComparer.Ordinal);
                foreach (var inst in toInstruct)
                    live[Normalize(inst.Address)] = inst;
                effective = live.Values.OrderBy(i => i.Address, StringComparer.Ordinal).ToArray();
                if (effective.Count == 0)
                    return;
                ValidateInstructions(effective);
            }
            batch = new BatchState { BlockNo = blockNo, CorrelationId = correlationId, Signature = signature };
            foreach (var inst in effective)
                batch.PendingAddresses.Add(Normalize(inst.Address));
            foreach (var held in _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == blockNo
                && m.CorrelationId == correlationId && NeedsConfirmationRecovery(m)))
                batch.PendingAddresses.Add(held.Address);
            _activeBatches[blockNo] = batch;
            _registry.SetInstructedBatch(toInstruct);
        }
        // Keep identities/tracking on an ambiguous hardware failure: a later confirm
        // must retain its original item instead of becoming an uncorrelated event.
        batch.HardwareAttempted = true;
        if (relight)
            await _gateway.OverwriteBatchAsync(effective, ct, write => WriteLiveBatch(blockNo, correlationId, write));
        else
            await _gateway.InstructBatchAsync(effective, ct, write => WriteLiveBatch(blockNo, correlationId, write));
        batch.Applied = true;
        _log.LogInformation("Batch applied for block {Block}: {Count} modules (relight={Relight})", blockNo, effective.Count, relight);
    }

    /// <summary>
    /// Terminates the active batch for the specified block (turns off all lights) and resets state.
    /// </summary>
    private async Task FinishBatchCoreAsync(string blockNo, CancellationToken ct)
    {
        lock (_lock)
            if (_registry.All.Any(m => (blockNo == "00" || m.BlockNo == blockNo) && NeedsConfirmationRecovery(m)))
                throw new PickingCommandRejectedException("CONFIRMATION_RECOVERY_REQUIRED", "Resolve missing confirmations before finishing this block.");
        await _gateway.TerminateBlockAsync(blockNo, ct);

        _registry.SetTerminatedForBlock(blockNo);
        if (blockNo == "00")
        {
            lock (_lock)
            {
                _activeBatches.Clear();
            }

        }
        else
        {
            _activeBatches.TryRemove(blockNo, out _);

        }

        _log.LogInformation("Batch finished for block {Block}", blockNo);
    }

    /// <summary>
    /// Lights a single module without turning off other active modules.
    /// Re-sends P1/PP5 with all currently active modules plus this one.
    /// Does not affect active batch tracking unless the module is already in a batch.
    /// </summary>
    private async Task QuickLightCoreAsync(string address, int quantity,
                                      LightColor color = LightColor.Green,
                                      LightState ledMode = LightState.On,
                                      LightState buzMode = LightState.Off,
                                      string? label = null, string? blockNo = "00",
                                      string? correlationId = null, CancellationToken ct = default,
                                      string? commandItemId = null, string? pickingDetailId = null)
    {
        var inst = new PickingInstruction
        {
            Address = Normalize(address),
            Quantity = quantity,
            Color = color,
            LedMode = ledMode,
            BuzMode = buzMode,
            Label = label,
            BlockNo = blockNo ?? "00",
            CorrelationId = correlationId,
            CommandItemId = commandItemId,
            PickingDetailId = pickingDetailId,
        };
        ValidateInstructions([inst]);
        List<PickingInstruction> instructions;
        lock (_lock)
        {
            _registry.ThrowIfSessionClosed(inst.BlockNo, correlationId);
            RestoreActiveBatches();
            ValidateGlobalOwnership(inst.BlockNo, correlationId);
            ValidateOwnership([inst]);
            if (_registry.IsCompletedItem(inst))
                throw new PickingCommandRejectedException("ITEM_COMPLETED", "Item already completed.");
            if (_activeBatches.TryGetValue(inst.BlockNo, out var active) && active.CorrelationId != correlationId)
                throw new PickingCommandRejectedException("BLOCK_BUSY", $"Block {inst.BlockNo} belongs to another active session.");
            var current = _registry.Get(inst.Address);
            if (!string.IsNullOrEmpty(commandItemId) && current?.CommandItemId == commandItemId
                && current.CorrelationId == correlationId && current.Status is ModuleStatus.Completed or ModuleStatus.Shortage)
                throw new PickingCommandRejectedException("ITEM_COMPLETED", $"Item {commandItemId} already completed.");
            instructions = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == inst.BlockNo && !NeedsConfirmationRecovery(m))
                .Select(ToInstruction).Where(i => i.Address != inst.Address).Append(inst)
                .OrderBy(i => i.Address, StringComparer.Ordinal).ToList();
            ValidateInstructions(instructions);
            _registry.SetInstructed(inst);
            active ??= new BatchState { BlockNo = inst.BlockNo, CorrelationId = correlationId, Signature = "" };
            active.PendingAddresses.Add(inst.Address);
            _activeBatches[inst.BlockNo] = active;
        }
        try
        {
            await _gateway.InstructBatchAsync(instructions, ct, write => WriteLiveBatch(inst.BlockNo, correlationId, write));
        }
        catch (AioiNackException)
        {
            _log.LogInformation("[QuickLight] P1 NACKed — retrying with PP5 (overwrite)");
            await _gateway.OverwriteBatchAsync(instructions, ct, write => WriteLiveBatch(inst.BlockNo, correlationId, write));
        }
    }

    /// <summary>
    /// Reconfigures per-address lighting, after-confirm modes and display parameters.
    /// </summary>
    public bool IsItemCompleted(PickingInstruction instruction)
    {
        lock (_lock)
        {
            if (_registry.IsCompletedItem(instruction)) return true;
            var current = _registry.Get(instruction.Address);
            return current is not null && current.CorrelationId == instruction.CorrelationId
                && !string.IsNullOrEmpty(instruction.CommandItemId) && current.CommandItemId == instruction.CommandItemId
                && current.Status is ModuleStatus.Completed or ModuleStatus.Shortage;
        }
    }

    public bool IsItemOperating(PickingInstruction instruction)
    {
        var current = _registry.Get(instruction.Address);
        return current?.Status == ModuleStatus.Operating && current.CorrelationId == instruction.CorrelationId
            && current.CommandItemId == instruction.CommandItemId;
    }

    private ValueTask WriteLiveBatch(string blockNo, string? correlationId,
        Func<IReadOnlyList<PickingInstruction>, ValueTask> beginWrite,
        string? recoveryAddress = null, string? recoveryEventId = null)
    {
        lock (_lock)
        {
            var live = _registry.All.Where(m => m.Status == ModuleStatus.Operating
                    && m.BlockNo == blockNo && m.CorrelationId == correlationId && m.LastInstructionAt is not null
                    && (!NeedsConfirmationRecovery(m) || (m.Address == recoveryAddress && m.ConfirmationMissingEventId == recoveryEventId)))
                .Select(ToInstruction).OrderBy(i => i.Address, StringComparer.Ordinal).ToArray();
            return beginWrite(live);
        }
    }

    private async Task ConfigureModuleCoreAsync(string address,
                                           int? newQuantity,
                                           ModuleParameters parameters,
                                           CancellationToken ct = default)
    {
        ValidateInstructions([new PickingInstruction { Address = address, Quantity = newQuantity ?? 0, Color = parameters.Color, LedMode = parameters.LedMode, SegMode = parameters.SegMode, BuzMode = parameters.BuzMode }]);
        if (!Enum.IsDefined(parameters.AfterConfirm) || !Enum.IsDefined(parameters.BuzAfterConfirm))
            throw new PickingCommandRejectedException("INVALID_PARAMETERS", "After-confirm mode is invalid.");
        var addr = address.PadLeft(4, '0');
        if (_registry.HasPendingConfirmation(addr))
            throw new PickingCommandRejectedException("ADDRESS_BUSY", "Address has a durable confirmation awaiting publication.");
        var current = _registry.Get(addr);
        if (current is not null && NeedsConfirmationRecovery(current))
            throw new PickingCommandRejectedException("CONFIRMATION_RECOVERY_REQUIRED", "Resolve the missing confirmation before configuring this item.");

        var displayData = newQuantity.HasValue
            ? newQuantity.Value.ToString().PadLeft(5, '0')
            : (current?.DisplayData ?? "00000");

        await _gateway.ConfigureModuleAsync(addr, displayData, parameters, ct);
        _registry.SetConfigured(addr, parameters, displayData);
    }

    /// <summary>Turns off a single module immediately.</summary>
    private async Task ExtinguishCoreAsync(string address, CancellationToken ct)
    {
        var normalized = Normalize(address);
        LightModuleState? owner;
        BatchState? batch;
        lock (_lock)
        {
            owner = _registry.Get(normalized);
            if (owner is not null && NeedsConfirmationRecovery(owner))
                throw new PickingCommandRejectedException("CONFIRMATION_RECOVERY_REQUIRED", "Use an exact-owner recovery action for this item.");
            batch = owner is not null && _activeBatches.TryGetValue(owner.BlockNo, out var active)
                && active.CorrelationId == owner.CorrelationId ? active : null;
        }

        await _gateway.TerminateAsync(normalized, ct);
        lock (_lock)
        {
            var current = _registry.Get(normalized);
            if (owner is null || current is null || current.BlockNo != owner.BlockNo
                || current.CorrelationId != owner.CorrelationId || current.CommandItemId != owner.CommandItemId
                || current.PickingDetailId != owner.PickingDetailId || current.LastInstructionAt != owner.LastInstructionAt)
                return;

            // A real confirmation can commit while D is awaiting its ACK. Keep
            // that completed identity and its durable event instead of cancelling it.
            if (current.Status is not (ModuleStatus.Completed or ModuleStatus.Shortage))
                _registry.SetTerminated(normalized);

            if (batch is null || !_activeBatches.TryGetValue(owner.BlockNo, out var activeBatch)
                || !ReferenceEquals(activeBatch, batch))
                return;
            if (!_registry.HasPendingConfirmation(normalized))
                batch.PendingAddresses.Remove(normalized);
            RemoveEmptyBatchIfSettled(batch);
        }
    }

    // ─── Confirmation handling ─────────────────────────────────────────────

    private async Task ResolveConfirmationCoreAsync(string address, string blockNo, string correlationId,
        string? commandItemId, string? pickingDetailId, string missingEventId, string action,
        int? actualQuantity, string resolutionId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(address) || address.Length > 4 || !address.All(char.IsAsciiDigit)
            || !int.TryParse(address, out var number) || number < 1
            || string.IsNullOrEmpty(blockNo) || blockNo.Length != 2 || !blockNo.All(char.IsAsciiDigit) || string.IsNullOrWhiteSpace(correlationId)
            || string.IsNullOrWhiteSpace(missingEventId) || string.IsNullOrWhiteSpace(resolutionId)
            || resolutionId.Length > 200 || action is not ("confirm" or "retry" or "cancel")
            || (action == "confirm" ? actualQuantity is null or < 0 or > 99999 : actualQuantity is not null))
            throw new PickingCommandRejectedException("INVALID_RECOVERY", "Recovery requires exact identity, action and actual quantity for confirm.");
        address = Normalize(address);
        if (action == "cancel" && !correlationId.StartsWith("manual:", StringComparison.Ordinal))
            throw new PickingCommandRejectedException("RECOVERY_CANCEL_FORBIDDEN", "Only a manual session can be cancelled here.");

        LightModuleState expected;
        lock (_lock)
        {
            if (action == "retry") _registry.ThrowIfSessionClosed(blockNo, correlationId);
            expected = _registry.Get(address)
                ?? throw new PickingCommandRejectedException("RECOVERY_STALE", "Recovery item no longer exists.");
            if (expected.BlockNo != blockNo || expected.CorrelationId != correlationId
                || expected.CommandItemId != commandItemId || expected.PickingDetailId != pickingDetailId
                || expected.Status != ModuleStatus.Operating || expected.ConfirmationMissingAt is null
                || expected.ConfirmationMissingEventId != missingEventId)
                throw new PickingCommandRejectedException("RECOVERY_STALE", "Recovery identity no longer matches the active item.");
            if (action == "confirm" && (!int.TryParse(expected.DisplayData, out var requested) || actualQuantity > requested))
                throw new PickingCommandRejectedException("INVALID_RECOVERY", "Actual quantity cannot exceed the instructed quantity.");
            if (action == "confirm" && _registry.HasConfirmationEventId(resolutionId))
                throw new PickingCommandRejectedException("INVALID_RECOVERY", "Resolution ID already belongs to a confirmation.");
            PublishMissingConfirmations();
        }

        // The Ethernet controller processes D only after pending t/O exchange.
        // This is a drain barrier, not a business token or an exactly-once firmware guarantee.
        await _gateway.TerminateAsync(address, ct);
        IReadOnlyList<PickingInstruction>? retry = null;
        lock (_lock)
        {
            var current = _registry.Get(address);
            if (current is null || !LightModuleRegistry.SameInstruction(current, expected))
                throw new PickingCommandRejectedException("RECOVERY_STALE", "The item changed while recovery was awaiting D.");
            if (current.Status is ModuleStatus.Completed or ModuleStatus.Shortage)
                return; // A real t captured the old owner; never publish a second picked event.
            if (current.ConfirmationMissingEventId != missingEventId)
                throw new PickingCommandRejectedException("RECOVERY_STALE", "The missing-confirmation observation changed.");

            if (action == "confirm")
            {
                var confirmation = new ConfirmationEvent
                {
                    Address = address, SequenceNo = -1, EventId = resolutionId, Source = "manual_recovery",
                    AdjustedDisplay = actualQuantity!.Value.ToString("D5"),
                    Status = int.TryParse(current.DisplayData, out var requested) && requested == actualQuantity.Value
                        ? CompletionStatus.Normal : CompletionStatus.Shortage
                };
                _registry.SetConfirmed(confirmation);
                _unpersistedMissingAddresses.Remove(address);
                var pending = _registry.PendingConfirmations.First(i => i.EventId == resolutionId && i.Source == "manual_recovery");
                PublishPending(pending);
            }
            else if (action == "cancel")
            {
                _registry.SetTerminated(address);
                _unpersistedMissingAddresses.Remove(address);
                if (_activeBatches.TryGetValue(blockNo, out var batch) && batch.CorrelationId == correlationId)
                {
                    batch.PendingAddresses.Remove(address);
                    RemoveEmptyBatchIfSettled(batch);
                }
            }
            else
            {
                retry = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == blockNo
                        && m.CorrelationId == correlationId && (!NeedsConfirmationRecovery(m) || m.Address == address))
                    .Select(ToInstruction).OrderBy(i => i.Address, StringComparer.Ordinal).ToArray();
                ValidateInstructions(retry);
            }
        }
        if (retry is not null)
        {
            await _gateway.OverwriteBatchAsync(retry, ct,
                write => WriteLiveBatch(blockNo, correlationId, write, address, missingEventId));
            lock (_lock)
            {
                _registry.ClearConfirmationMissing(expected, reinstructed: true);
                _unpersistedMissingAddresses.Remove(address);
                _statusMismatches.Remove(address);
            }
        }
        if (action == "confirm") PublishBatchCompletions();
    }

    private bool NeedsConfirmationRecovery(LightModuleState state) =>
        state.ConfirmationMissingAt is not null || _unpersistedMissingAddresses.Contains(state.Address);

    private void PublishMissingConfirmations(string? blockNo = null, string? correlationId = null)
    {
        foreach (var missing in _registry.PendingMissingConfirmations.Where(e => blockNo is null
            || (e.BlockNo == blockNo && e.CorrelationId == correlationId)))
        {
            var durable = DurableConfirmationMissing
                ?? throw new IOException("Durable missing-confirmation consumer is unavailable; observation retained.");
            durable(missing);
            _registry.MarkMissingConfirmationPublished(missing);
        }
    }

    private void OnConfirmed(ConfirmationEvent evt)
    {
        BatchState? completed;
        lock (_lock)
        {
            var processed = _registry.IsProcessedInterrupt(evt);
            RestoreActiveBatches();
            if (!processed)
            {
                var state = _registry.Get(evt.Address);
                if (state is null || state.Status != ModuleStatus.Operating)
                    return;
                // Completion, raw receipt and the exact pending business event commit
                // together before an outbox consumer can expose the event to WMS.
                _registry.SetConfirmed(evt);
                _unpersistedMissingAddresses.Remove(Normalize(evt.Address));
                _statusMismatches.Remove(Normalize(evt.Address));
            }
            PublishMissingConfirmations();
            var pending = _registry.PendingConfirmations.FirstOrDefault(i => i.SequenceNo >= 0 && i.SequenceNo == evt.SequenceNo);
            completed = pending is null ? null : PublishPending(pending);
        }
        PublishBatchCompletions();
    }

    /// <summary>Called after a durable event consumer subscribes, before transport drain.</summary>
    public void ReplayPendingConfirmations()
    {
        var completed = new List<BatchState>();
        lock (_lock)
        {
            RestoreActiveBatches();
            PublishMissingConfirmations();
            foreach (var pending in _registry.PendingConfirmations)
                if (PublishPending(pending) is { } batch)
                    completed.Add(batch);
        }
        PublishBatchCompletions();
    }

    private void PublishBatchCompletions(string? blockNo = null, string? correlationId = null)
    {
        lock (_lock)
        {
            foreach (var completion in _registry.PendingBatchCompletions.Where(c => blockNo is null
                || (c.BlockNo == blockNo && c.CorrelationId == correlationId)))
            {
                try
                {
                    var durable = DurableBatchCompleted;
                    if (durable is null && Volatile.Read(ref _requiresDurableConfirmationConsumer) != 0)
                        throw new IOException("Durable batch consumer is unavailable; completion retained.");
                    durable?.Invoke(completion);
                    _registry.MarkBatchCompletionPublished(completion);
                    if (_activeBatches.TryGetValue(completion.BlockNo, out var batch)
                        && batch.CorrelationId == completion.CorrelationId)
                        RemoveEmptyBatchIfSettled(batch);
                }
                finally
                {
                    // Local waiters must not be stranded by optional observers or
                    // an outbox error after the item confirmation already committed.
                    if (BatchCompleted is { } observers)
                        foreach (Action<string?> observer in observers.GetInvocationList())
                            try { observer(completion.CorrelationId); }
                            catch (Exception ex)
                            {
                                try { _log.LogError(ex, "Batch completion observer failed"); } catch { }
                            }
                }
            }
        }
    }

    private BatchState? PublishPending(InterruptReceipt receipt)
    {
        var state = receipt.PendingState!;
        var durable = DurableConfirmation;
        if (durable is null && Volatile.Read(ref _requiresDurableConfirmationConsumer) != 0)
            throw new IOException("Durable confirmation consumer is unavailable; pending event retained.");
        durable?.Invoke(receipt.ToEvent(), LightModuleRegistry.Clone(state));
        _registry.MarkConfirmationPublished(receipt);
        if (ItemConfirmed is { } observers)
        {
            foreach (Action<ConfirmationEvent, LightModuleState?> observer in observers.GetInvocationList())
            {
                try
                {
                    observer(receipt.ToEvent(), LightModuleRegistry.Clone(state));
                }
                catch (Exception ex) { _log.LogError(ex, "Confirmation observer failed after durable commit"); }
            }
        }
        if (_activeBatches.TryGetValue(state.BlockNo, out var batch)
            && batch.CorrelationId == state.CorrelationId && batch.PendingAddresses.Remove(state.Address)
            && batch.PendingAddresses.Count == 0)
        {
            RemoveEmptyBatchIfSettled(batch);
            _log.LogInformation("Batch {Block} complete", batch.BlockNo);
            return batch;
        }
        return null;
    }

    public static void ValidateInstructions(IReadOnlyList<PickingInstruction> instructions)
    {
        const int maxItemsPerFrame = AioiConstants.MaxBatchAddresses;
        if (instructions.Count == 0 || instructions.Count > maxItemsPerFrame)
            throw new PickingCommandRejectedException("INVALID_ITEMS", $"A batch must contain 1–{maxItemsPerFrame} modules per hardware frame.");
        var block = instructions[0].BlockNo;
        if (string.IsNullOrEmpty(block) || block.Length != 2 || !block.All(char.IsAsciiDigit))
            throw new PickingCommandRejectedException("INVALID_BLOCK", "Block must contain two digits.");
        if (instructions.Select(i => i.CorrelationId).Distinct(StringComparer.Ordinal).Count() > 1)
            throw new PickingCommandRejectedException("INVALID_SESSION", "A batch must belong to one session.");
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in instructions)
        {
            if (item.BlockNo != block)
                throw new PickingCommandRejectedException("MIXED_BLOCK", "A batch must belong to one block.");
            if (string.IsNullOrWhiteSpace(item.Address) || item.Address.Length > 4 || !item.Address.All(char.IsAsciiDigit)
                || !int.TryParse(item.Address, out var address) || address < 1 || address > 9999)
                throw new PickingCommandRejectedException("INVALID_ADDRESS", "Address must contain digits from 0001 to 9999.");
            if (!addresses.Add(Normalize(item.Address)))
                throw new PickingCommandRejectedException("DUPLICATE_ADDRESS", "A batch cannot assign one address twice.");
            if (item.Quantity is < 0 or > 99999 || !Enum.IsDefined(item.Color) || !Enum.IsDefined(item.LedMode)
                || !Enum.IsDefined(item.SegMode) || !Enum.IsDefined(item.BuzMode))
                throw new PickingCommandRejectedException("INVALID_PARAMETERS", "Quantity or light modes are invalid.");
        }
    }

    private void ValidateOwnership(IReadOnlyList<PickingInstruction> instructions)
    {
        foreach (var inst in instructions)
        {
            if (_registry.HasPendingConfirmation(inst.Address))
                throw new PickingCommandRejectedException("ADDRESS_BUSY", "Address has a durable confirmation awaiting publication.");
            var current = _registry.Get(inst.Address);
            if (current is not null && NeedsConfirmationRecovery(current))
                throw new PickingCommandRejectedException("CONFIRMATION_RECOVERY_REQUIRED", "Item has a missing confirmation requiring an explicit owner decision.");
            if (current?.Status == ModuleStatus.Operating && (current.BlockNo != inst.BlockNo || current.CorrelationId != inst.CorrelationId))
                throw new PickingCommandRejectedException("ADDRESS_BUSY", $"Address {inst.Address} belongs to another active block/session.");
        }
    }

    private static string Normalize(string address) => address?.PadLeft(4, '0') ?? string.Empty;

    private static PickingInstruction ToInstruction(LightModuleState state) => new()
    {
        Address = state.Address,
        Quantity = int.TryParse(state.DisplayData.Trim(), out var q) ? q : 0,
        Color = state.Color,
        LedMode = state.LedMode,
        SegMode = state.SegMode,
        BuzMode = state.BuzMode,
        BlockNo = state.BlockNo,
        CorrelationId = state.CorrelationId,
        CommandItemId = state.CommandItemId,
        PickingDetailId = state.PickingDetailId,
    };

    private void RemoveBatchIfCurrent(string blockNo, BatchState batch)
    {
        var pair = new KeyValuePair<string, BatchState>(blockNo, batch);
        ((ICollection<KeyValuePair<string, BatchState>>)_activeBatches).Remove(pair);
    }

    private void RemoveEmptyBatchIfSettled(BatchState batch)
    {
        if (batch.PendingAddresses.Count != 0
            || _registry.PendingConfirmations.Any(i => i.PendingState?.BlockNo == batch.BlockNo
                && i.PendingState.CorrelationId == batch.CorrelationId)
            || _registry.PendingBatchCompletions.Any(c => c.BlockNo == batch.BlockNo
                && c.CorrelationId == batch.CorrelationId))
            return;
        RemoveBatchIfCurrent(batch.BlockNo, batch);
    }

    private static string BuildBatchSignature(IReadOnlyList<PickingInstruction> instructions) =>
        string.Join('|', instructions
            .Select(i => string.Join(':',
                i.BlockNo,
                i.Address.PadLeft(4, '0')[^4..],
                i.Quantity,
                i.Color,
                i.LedMode,
                i.SegMode,
                i.BuzMode,
                i.Label ?? "",
                i.CorrelationId ?? "",
                i.CommandItemId ?? "",
                i.PickingDetailId ?? ""))
            .Order(StringComparer.Ordinal));

    public void Dispose()
    {
        _gateway.Confirmed -= OnConfirmed;
    }
}
