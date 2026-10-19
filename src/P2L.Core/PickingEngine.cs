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
        public required string BlockNo { get; init; }
        public required string? CorrelationId { get; init; }
        public required string Signature { get; init; }
        public HashSet<string> PendingAddresses { get; } = new();
    }

    private readonly AioiGateway         _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger<PickingEngine> _log;

    private readonly ConcurrentDictionary<string, BatchState> _activeBatches = new();
    private readonly object             _lock             = new();

    /// <summary>Fires when an operator presses CONFIRM on any module in any active batch.</summary>
    public event Action<ConfirmationEvent, LightModuleState?>? ItemConfirmed;

    /// <summary>Fires when every module in a specific batch has been confirmed.</summary>
    public event Action<string?>? BatchCompleted;

    /// <summary>True if any batch is currently active.</summary>
    public bool IsBatchActive => !_activeBatches.IsEmpty;

    public PickingEngine(AioiGateway gateway, LightModuleRegistry registry, ILogger<PickingEngine> logger)
    {
        _gateway  = gateway;
        _registry = registry;
        _log      = logger;

        _gateway.Confirmed += OnConfirmed;
    }

    // ─── Batch operations ─────────────────────────────────────────────────

    /// <summary>
    /// Lights up a batch of modules and begins tracking confirmations.
    /// If a batch for the specified block is already active, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task StartBatchAsync(IReadOnlyList<PickingInstruction> instructions,
                                      CancellationToken ct = default, bool relight = false)
    {
        if (instructions.Count == 0) return;
        ValidateInstructions(instructions);
        var blockNo = instructions[0].BlockNo;
        var correlationId = instructions.Select(i => i.CorrelationId).Distinct(StringComparer.Ordinal).Single();
        var signature = BuildBatchSignature(instructions);
        var effective = instructions;
        var toInstruct = instructions;
        BatchState batch;
        lock (_lock)
        {
            ValidateOwnership(instructions);
            if (_activeBatches.TryGetValue(blockNo, out var existing))
            {
                if (existing.CorrelationId != correlationId)
                    throw new PickingCommandRejectedException("BLOCK_BUSY", $"Block {blockNo} belongs to another active session.");
                if (!relight)
                {
                    if (existing.Signature == signature) return;
                    throw new PickingCommandRejectedException("BLOCK_BUSY", $"A different batch for block {blockNo} is already active.");
                }
            }
            if (relight)
            {
                // A confirmation may finish after WMS prepares the relight but
                // before it reaches this bridge. Skip exactly that completed item.
                toInstruct = instructions.Where(inst =>
                {
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
                var live = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == blockNo)
                    .Select(ToInstruction).ToDictionary(i => Normalize(i.Address), StringComparer.Ordinal);
                foreach (var inst in toInstruct) live[Normalize(inst.Address)] = inst;
                effective = live.Values.OrderBy(i => i.Address, StringComparer.Ordinal).ToArray();
                if (effective.Count == 0) return;
                ValidateInstructions(effective);
            }
            batch = new BatchState { BlockNo = blockNo, CorrelationId = correlationId, Signature = signature };
            foreach (var inst in effective) batch.PendingAddresses.Add(Normalize(inst.Address));
            _activeBatches[blockNo] = batch;
            foreach (var inst in toInstruct) _registry.SetInstructed(inst);
        }
        // Keep identities/tracking on an ambiguous hardware failure: a later confirm
        // must retain its original item instead of becoming an uncorrelated event.
        if (relight) await _gateway.OverwriteBatchAsync(effective, ct);
        else await _gateway.InstructBatchAsync(effective, ct);
        _log.LogInformation("Batch applied for block {Block}: {Count} modules (relight={Relight})", blockNo, effective.Count, relight);
    }

    /// <summary>
    /// Terminates the active batch for the specified block (turns off all lights) and resets state.
    /// </summary>
    public async Task FinishBatchAsync(string blockNo = "00", CancellationToken ct = default)
    {
        await _gateway.TerminateBlockAsync(blockNo, ct);

        if (blockNo == "00")
        {
            lock (_lock)
            {
                _activeBatches.Clear();
            }
            _registry.SetTerminatedForBlock("00");
        }
        else
        {
            _activeBatches.TryRemove(blockNo, out _);
            _registry.SetTerminatedForBlock(blockNo);
        }

        _log.LogInformation("Batch finished for block {Block}", blockNo);
    }

    /// <summary>
    /// Lights a single module without turning off other active modules.
    /// Re-sends P1/P5 with all currently active modules plus this one.
    /// Does not affect active batch tracking unless the module is already in a batch.
    /// </summary>
    public async Task QuickLightAsync(string address, int quantity,
                                      LightColor color = LightColor.Green,
                                      LightState ledMode = LightState.On,
                                      LightState buzMode = LightState.Off,
                                      string? label = null, string? blockNo = "00",
                                      string? correlationId = null, CancellationToken ct = default,
                                      string? commandItemId = null, string? pickingDetailId = null)
    {
        var inst = new PickingInstruction
        {
            Address = Normalize(address), Quantity = quantity, Color = color,
            LedMode = ledMode, BuzMode = buzMode, Label = label,
            BlockNo = blockNo ?? "00", CorrelationId = correlationId,
            CommandItemId = commandItemId, PickingDetailId = pickingDetailId,
        };
        ValidateInstructions([inst]);
        List<PickingInstruction> instructions;
        lock (_lock)
        {
            ValidateOwnership([inst]);
            if (_activeBatches.TryGetValue(inst.BlockNo, out var active) && active.CorrelationId != correlationId)
                throw new PickingCommandRejectedException("BLOCK_BUSY", $"Block {inst.BlockNo} belongs to another active session.");
            var current = _registry.Get(inst.Address);
            if (!string.IsNullOrEmpty(commandItemId) && current?.CommandItemId == commandItemId
                && current.CorrelationId == correlationId && current.Status is ModuleStatus.Completed or ModuleStatus.Shortage)
                throw new PickingCommandRejectedException("ITEM_COMPLETED", $"Item {commandItemId} already completed.");
            instructions = _registry.All.Where(m => m.Status == ModuleStatus.Operating && m.BlockNo == inst.BlockNo)
                .Select(ToInstruction).Where(i => i.Address != inst.Address).Append(inst)
                .OrderBy(i => i.Address, StringComparer.Ordinal).ToList();
            ValidateInstructions(instructions);
            _registry.SetInstructed(inst);
            active ??= new BatchState { BlockNo = inst.BlockNo, CorrelationId = correlationId, Signature = "" };
            active.PendingAddresses.Add(inst.Address);
            _activeBatches[inst.BlockNo] = active;
        }
        try { await _gateway.InstructBatchAsync(instructions, ct); }
        catch (AioiNackException)
        {
            _log.LogInformation("[QuickLight] P1 NACKed — retrying with P5 (overwrite)");
            await _gateway.OverwriteBatchAsync(instructions, ct);
        }
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
    public async Task ExtinguishAsync(string address, CancellationToken ct = default)
    {
        await _gateway.TerminateAsync(address, ct);
        _registry.SetTerminated(address);
    }

    // ─── Confirmation handling ─────────────────────────────────────────────

    private void OnConfirmed(ConfirmationEvent evt)
    {
        BatchState? completed = null;
        lock (_lock)
        {
            var addr = Normalize(evt.Address);
            var state = _registry.Get(addr);
            if (state is null || state.Status != ModuleStatus.Operating) return;
            // Persist and complete one captured item atomically relative to a
            // next-detail instruction. Subscribers synchronously persist only;
            // no async/network wait occurs while this lock is held.
            state.Status = evt.Status == CompletionStatus.Shortage ? ModuleStatus.Shortage : ModuleStatus.Completed;
            state.LastConfirmedAt = evt.ReceivedAt;
            state.AdjustedQuantity = int.TryParse(evt.AdjustedDisplay?.Trim(), out var qty) ? qty : null;
            ItemConfirmed?.Invoke(evt, state);
            _registry.SetConfirmed(evt);

            if (_activeBatches.TryGetValue(state.BlockNo, out var batch)
                && batch.CorrelationId == state.CorrelationId && batch.PendingAddresses.Remove(addr)
                && batch.PendingAddresses.Count == 0)
            {
                completed = batch;
                RemoveBatchIfCurrent(state.BlockNo, batch);
            }
        }
        if (completed is not null)
        {
            _log.LogInformation("Batch {Block} complete", completed.BlockNo);
            BatchCompleted?.Invoke(completed.CorrelationId);
        }
    }

    public static void ValidateInstructions(IReadOnlyList<PickingInstruction> instructions)
    {
        const int maxItemsPerFrame = (AioiConstants.MaxCommandLength - 4) / 9;
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
            if (item.BlockNo != block) throw new PickingCommandRejectedException("MIXED_BLOCK", "A batch must belong to one block.");
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
            var current = _registry.Get(inst.Address);
            if (current?.Status == ModuleStatus.Operating && (current.BlockNo != inst.BlockNo || current.CorrelationId != inst.CorrelationId))
                throw new PickingCommandRejectedException("ADDRESS_BUSY", $"Address {inst.Address} belongs to another active block/session.");
        }
    }

    private static string Normalize(string address) => address?.PadLeft(4, '0') ?? string.Empty;

    private static PickingInstruction ToInstruction(LightModuleState state) => new()
    {
        Address = state.Address, Quantity = int.TryParse(state.DisplayData.Trim(), out var q) ? q : 0,
        Color = state.Color, LedMode = state.LedMode, SegMode = state.SegMode, BuzMode = state.BuzMode,
        BlockNo = state.BlockNo, CorrelationId = state.CorrelationId,
        CommandItemId = state.CommandItemId, PickingDetailId = state.PickingDetailId,
    };

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
                i.CorrelationId ?? "",
                i.CommandItemId ?? "",
                i.PickingDetailId ?? ""))
            .Order(StringComparer.Ordinal));

    public void Dispose()
    {
        _gateway.Confirmed -= OnConfirmed;
    }
}
