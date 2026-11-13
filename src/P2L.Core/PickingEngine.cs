using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using P2L.Hardware;
using P2L.Shared.Enums;
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
        public required string BlockNo { get; init; }
        public required string? CorrelationId { get; init; }
        public required string Signature { get; init; }
        public HashSet<string> PendingAddresses { get; } = new();
        public HashSet<string> Addresses { get; } = new();
        public bool DispatchUncertain { get; set; }
        public bool Finishing { get; set; }
    }

    private readonly AioiGateway         _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger<PickingEngine> _log;

    private readonly ConcurrentDictionary<string, BatchState> _activeBatches = new();
    private readonly object             _lock             = new();
    private readonly SemaphoreSlim _operations = new(1, 1);
    private readonly BlockPolicy _blocks;
    private readonly P2L.Shared.IConfirmationJournal? _confirmations;

    /// <summary>Fires when an operator presses CONFIRM on any module in any active batch.</summary>
    public event Action<ConfirmationEvent, LightModuleState?>? ItemConfirmed;

    /// <summary>Fires when every module in a specific batch has been confirmed.</summary>
    public event Action<string?>? BatchCompleted;
    public event Action<string, string?>? BlockBatchCompleted;

    /// <summary>True if any batch is currently active.</summary>
    public bool HasConfirmationJournal => _confirmations is not null;
    public bool IsBatchActive => !_activeBatches.IsEmpty;
    public void ValidateBlock(string blockNo) => _blocks.Validate(blockNo);

    public PickingEngine(AioiGateway gateway, LightModuleRegistry registry, ILogger<PickingEngine> logger, BlockPolicy? blocks = null, P2L.Shared.IConfirmationJournal? confirmations = null)
    {
        _gateway  = gateway;
        _registry = registry;
        _log      = logger;
        _blocks = blocks ?? new BlockPolicy();
        _confirmations = confirmations;

        _gateway.Confirmed += OnConfirmed;
    }

    // ─── Batch operations ─────────────────────────────────────────────────

    /// <summary>
    /// Lights up a batch of modules and begins tracking confirmations.
    /// If a batch for the specified block is already active, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task StartBatchAsync(IReadOnlyList<PickingInstruction> instructions,
                                      CancellationToken ct = default)
    {
        if (instructions.Count == 0) return;
        var block = instructions[0].BlockNo;
        _blocks.Validate(block);
        if (instructions.Any(i => i.BlockNo != block)) throw new InvalidOperationException("A batch must use one block.");
        var ids = instructions.Select(i => i.CorrelationId).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length != 1) throw new InvalidOperationException("A batch must use one correlationId.");
        var addresses = instructions.Select(i => Address(i.Address)).ToArray();
        if (addresses.Distinct().Count() != addresses.Length) throw new InvalidOperationException("Duplicate address in batch.");
        var signature = BuildBatchSignature(instructions);
        await _operations.WaitAsync(ct);
        try
        {
            BatchState batch;
            lock (_lock)
            {
                if (_activeBatches.TryGetValue(block, out var existing))
                {
                    if (existing.Signature == signature && !existing.DispatchUncertain && !existing.Finishing)
                    {
                        _log.LogInformation("Duplicate batch_start ignored for block {Block}", block);
                        return;
                    }
                    throw new InvalidOperationException($"Block {block} is busy; finish its current session first.");
                }
                // A retry may contain already-confirmed items from an earlier batch.
                instructions = instructions.Where(i => !WasConfirmed(i.PickingDetailId)).ToArray();
                if (instructions.Count == 0) return;
                addresses = instructions.Select(i => Address(i.Address)).ToArray();
                ValidateOwnership(block, ids[0], addresses);
                batch = new BatchState {BlockNo = block, CorrelationId = ids[0], Signature = signature};
                batch.Addresses.UnionWith(addresses);
                batch.PendingAddresses.UnionWith(addresses);
                _activeBatches[block] = batch;
                foreach (var inst in instructions) _registry.SetInstructed(inst);
            }
            try { await _gateway.InstructBatchAsync(instructions, ct); }
            catch
            {
                // A partial write may already have reached the controller. Keep this block reserved.
                lock (_lock) batch.DispatchUncertain = true;
                throw;
            }
            _log.LogInformation("Batch started for block {Block}, session {CorrelationId}: {Count} modules", block, ids[0], instructions.Count);
        }
        finally { _operations.Release(); }
    }

    /// <summary>Block 00 is the explicit global stop. Other values affect only that block.</summary>
    public async Task FinishBatchAsync(string blockNo = "00", CancellationToken ct = default, string? correlationId = null)
    {
        if (blockNo != "00") _blocks.Validate(blockNo);
        await _operations.WaitAsync(ct);
        try
        {
            BatchState[] affected;
            string[] stopBlocks;
            lock (_lock)
            {
                affected = _activeBatches.Values.Where(b => blockNo == "00" || b.BlockNo == blockNo).ToArray();
                stopBlocks = blockNo == "00"
                    ? affected.Select(b => b.BlockNo).Concat(_registry.All.Select(m => m.BlockNo))
                        .Concat(_blocks.ConfiguredBlocks).Distinct().DefaultIfEmpty("00").ToArray()
                    : new[] {blockNo};
                if (correlationId is not null && (affected.Any(b => b.CorrelationId != correlationId) ||
                    _registry.All.Any(m => (blockNo == "00" || m.BlockNo == blockNo) && m.Status == ModuleStatus.Operating && m.CorrelationId != correlationId)))
                    throw new InvalidOperationException("Session does not own the requested block.");
                foreach (var batch in affected) batch.Finishing = true;
            }
            try { foreach (var block in stopBlocks) await _gateway.TerminateBlockAsync(block, ct); }
            catch
            {
                lock (_lock) foreach (var batch in affected) { batch.Finishing = false; batch.DispatchUncertain = true; }
                throw;
            }
            lock (_lock)
            {
                foreach (var batch in affected) RemoveBatchIfCurrent(batch.BlockNo, batch);
                _registry.SetTerminatedForBlock(blockNo);
            }
            _log.LogInformation("Batch finished for block {Block}", blockNo);
        }
        finally { _operations.Release(); }
    }

    /// <summary>Adds or updates one light, preserving only the other active lights in the SAME block.</summary>
    public async Task QuickLightAsync(string address, int quantity,
                                      LightColor color = LightColor.Green,
                                      LightState ledMode = LightState.On,
                                      LightState buzMode = LightState.Off,
                                      string? label = null, string? blockNo = "00",
                                      string? correlationId = null, CancellationToken ct = default, string? commandItemId = null, string? pickingDetailId = null)
    {
        var block = blockNo ?? "00";
        _blocks.Validate(block);
        var inst = new PickingInstruction {Address = Address(address), Quantity = quantity, Color = color,
            LedMode = ledMode, BuzMode = buzMode, Label = label, BlockNo = block, CorrelationId = correlationId, CommandItemId = commandItemId, PickingDetailId = pickingDetailId};
        await _operations.WaitAsync(ct);
        try
        {
            BatchState session;
            List<PickingInstruction> lights;
            lock (_lock)
            {
                if (WasConfirmed(pickingDetailId)) return; // Ignore this item, never lock the entire block.
                var previous = _registry.Get(inst.Address);
                if (commandItemId is not null && previous?.CommandItemId == commandItemId &&
                    previous.PickingDetailId == pickingDetailId && previous.BlockNo == block && previous.CorrelationId == correlationId)
                {
                    if (_activeBatches.TryGetValue(block, out var retryBatch) && (retryBatch.DispatchUncertain || retryBatch.Finishing))
                        throw new InvalidOperationException("Command dispatch is uncertain; reconcile this block before retrying.");
                    if (previous.DisplayData != inst.FormattedDisplay) throw new InvalidOperationException("A command item cannot change quantity when retried.");
                    return; // MQTT retry must not relight a confirmed item or change its receipt identity.
                }
                ValidateOwnership(block, correlationId, new[] {inst.Address});
                if (_activeBatches.TryGetValue(block, out var current))
                {
                    if (current.CorrelationId != correlationId || current.DispatchUncertain || current.Finishing)
                        throw new InvalidOperationException($"Block {block} belongs to another or uncertain session.");
                    session = current;
                }
                else
                {
                    session = new BatchState {BlockNo = block, CorrelationId = correlationId, Signature = ""};
                    _activeBatches[block] = session;
                }
                session.Addresses.Add(inst.Address);
                session.PendingAddresses.Add(inst.Address);
                _registry.SetInstructed(inst);
                lights = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == block)
                    .Select(m => new PickingInstruction {Address = m.Address,
                        Quantity = int.TryParse(m.DisplayData.Trim(), out var q) ? q : 0,
                        Color = m.Color, LedMode = m.LedMode, SegMode = m.SegMode, BuzMode = m.BuzMode,
                        BlockNo = m.BlockNo, CorrelationId = m.CorrelationId}).ToList();
            }
            try
            {
                try { await _gateway.InstructBatchAsync(lights, ct); }
                catch (AioiNackException)
                {
                    _log.LogInformation("[QuickLight] P1 NACKed — retrying P5 for block {Block}", block);
                    await _gateway.OverwriteBatchAsync(lights, ct);
                }
            }
            catch { lock (_lock) session.DispatchUncertain = true; throw; }
        }
        finally { _operations.Release(); }
    }

    private bool WasConfirmed(string? pickingDetailId) =>
        !string.IsNullOrEmpty(pickingDetailId) && (_confirmations?.HasRecorded(pickingDetailId) ?? false);

    // Controller confirmations identify only an address, so ownership is controller-wide.
    private void ValidateOwnership(string block, string? correlationId, IEnumerable<string> addresses)
    {
        var operating = _registry.All.Where(m => m.Status == ModuleStatus.Operating).ToArray();
        if (operating.Any(m => m.BlockNo == block &&
                (m.CorrelationId != correlationId || !_activeBatches.ContainsKey(block))))
            throw new InvalidOperationException($"Block {block} has operating lights; reconcile or finish its session first.");
        if (operating.Any(m => m.BlockNo != block && (m.BlockNo == "00" || block == "00")))
            throw new InvalidOperationException("Global block 00 cannot run alongside another block.");
        foreach (var session in _activeBatches.Values)
        {
            if (session.BlockNo == block) continue;
            if (block == "00" || session.BlockNo == "00") throw new InvalidOperationException("Global block 00 cannot run alongside another block.");
            if (correlationId is not null && session.CorrelationId == correlationId)
                throw new InvalidOperationException("A session cannot span two blocks.");
            if (addresses.Any(session.Addresses.Contains)) throw new InvalidOperationException("An address is already owned by another block.");
        }
        foreach (var address in addresses)
        {
            var state = _registry.Get(address);
            if (state?.Status == ModuleStatus.Operating && (state.BlockNo != block || state.CorrelationId != correlationId))
                throw new InvalidOperationException($"Address {address} is already operating in another session.");
        }
    }

    private static string Address(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4 || !value.All(char.IsAsciiDigit) || int.Parse(value) == 0)
            throw new ArgumentException("Light address must be 0001–9999.");
        return value.PadLeft(4, '0');
    }

    /// <summary>
    /// Reconfigures a module's lighting and display parameters in-place using M + L commands.
    /// </summary>
    public async Task ConfigureModuleAsync(string           address,
                                           int?             newQuantity,
                                           ModuleParameters parameters,
                                           CancellationToken ct = default)
    {
        var addr    = address.PadLeft(4, '0');
        var current = _registry.Get(addr);

        var displayData = newQuantity.HasValue
            ? newQuantity.Value.ToString().PadLeft(5, '0')
            : (current?.DisplayData ?? "00000");

        await _gateway.ConfigureModuleAsync(addr, displayData, parameters, ct);
        _registry.SetConfigured(addr, parameters, displayData);
    }

    /// <summary>Turns off a single module immediately.</summary>
    public async Task ExtinguishAsync(string address, CancellationToken ct = default, string? correlationId = null)
    {
        var addr = Address(address);
        await _operations.WaitAsync(ct);
        try
        {
            var state = _registry.Get(addr);
            if (state is not null) _blocks.Validate(state.BlockNo);
            if (correlationId is not null && state?.CorrelationId != correlationId)
                throw new InvalidOperationException("Session does not own this address.");
            await _gateway.TerminateAsync(addr, ct);
            lock (_lock)
            {
                _registry.SetTerminated(addr);
                if (state is not null && _activeBatches.TryGetValue(state.BlockNo, out var batch))
                {
                    batch.PendingAddresses.Remove(addr);
                    if (batch.PendingAddresses.Count == 0) RemoveBatchIfCurrent(batch.BlockNo, batch);
                }
            }
        }
        finally { _operations.Release(); }
    }

    private void OnConfirmed(ConfirmationEvent evt)
    {
        if (evt.Address.Length is < 1 or > 4 || !evt.Address.All(char.IsAsciiDigit) || evt.Address.All(c => c == '0')) return;
        LightModuleState? state;
        BatchState? completed = null;
        var addr = Address(evt.Address);
        lock (_lock)
        {
            var current = _registry.Get(addr);
            if (current?.Status != ModuleStatus.Operating) return; // ignore duplicate/late confirmations for idle lights
            _activeBatches.TryGetValue(current.BlockNo, out var batch);
            if (batch is not null && (batch.CorrelationId != current.CorrelationId || batch.Finishing || !batch.PendingAddresses.Contains(addr))) return;
            // Manual dashboard/CSV commands have no WMS detail to acknowledge.
            if (!string.IsNullOrEmpty(current.PickingDetailId)) _confirmations?.Record(evt, current);
            state = _registry.SetConfirmed(evt);
            if (batch is not null && batch.PendingAddresses.Remove(addr) && batch.PendingAddresses.Count == 0)
            {
                RemoveBatchIfCurrent(batch.BlockNo, batch);
                completed = batch;
            }
        }
        ItemConfirmed?.Invoke(evt, state);
        if (completed is not null)
        {
            _log.LogInformation("Batch {Block}, session {CorrelationId} complete", completed.BlockNo, completed.CorrelationId);
            BlockBatchCompleted?.Invoke(completed.BlockNo, completed.CorrelationId);
            BatchCompleted?.Invoke(completed.CorrelationId); // retained for CSV/legacy consumers
        }
    }

    private void RemoveBatchIfCurrent(string blockNo, BatchState batch)
    {
        var pair = new KeyValuePair<string, BatchState>(blockNo, batch);
        ((ICollection<KeyValuePair<string, BatchState>>)_activeBatches).Remove(pair);
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
                i.CorrelationId ?? "", i.CommandItemId ?? "", i.PickingDetailId ?? ""))
            .Order(StringComparer.Ordinal));

    public void Dispose()
    {
        _gateway.Confirmed -= OnConfirmed;
    }
}
