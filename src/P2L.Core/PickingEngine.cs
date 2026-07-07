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
                                      CancellationToken ct = default)
    {
        if (instructions.Count == 0) return;

        var blockNo = instructions[0].BlockNo;
        if (instructions.Any(i => i.BlockNo != blockNo))
            throw new InvalidOperationException("All instructions in one batch must use the same block number.");

        var signature = BuildBatchSignature(instructions);

        var batch = new BatchState
        {
            BlockNo = blockNo,
            Signature = signature,
            CorrelationId = instructions
                .Select(i => i.CorrelationId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .SingleOrDefault()
        };

        lock (_lock)
        {
            if (_activeBatches.TryGetValue(blockNo, out var existing))
            {
                if (existing.Signature == signature)
                {
                    _log.LogInformation("Duplicate batch_start ignored for block {Block}: {Count} modules",
                        blockNo, instructions.Count);
                    return;
                }

                throw new InvalidOperationException($"A batch for block {blockNo} is already active. Call FinishBatchAsync first.");
            }

            foreach (var inst in instructions)
                batch.PendingAddresses.Add(inst.Address.PadLeft(4, '0')[^4..]);

            _activeBatches[blockNo] = batch;
        }

        // Update registry before sending (optimistic)
        foreach (var inst in instructions)
            _registry.SetInstructed(inst);

        try
        {
            await _gateway.InstructBatchAsync(instructions, ct);
        }
        catch
        {
            RemoveBatchIfCurrent(blockNo, batch);
            lock (_lock)
            {
                foreach (var addr in batch.PendingAddresses)
                    _registry.SetTerminated(addr);
            }
            throw;
        }

        _log.LogInformation("Batch started for block {Block}: {Count} modules", blockNo, instructions.Count);
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
                                      LightColor color   = LightColor.Green,
                                      LightState ledMode = LightState.On,
                                      LightState buzMode = LightState.Off,
                                      string? label = null,
                                      string? blockNo = "00",
                                      string? correlationId = null,
                                      CancellationToken ct = default)
    {
        var inst = new PickingInstruction
        {
            Address  = address.PadLeft(4, '0'),
            Quantity = quantity,
            Color    = color,
            LedMode  = ledMode,
            BuzMode  = buzMode,
            Label    = label,
            BlockNo  = blockNo ?? "00",
            CorrelationId = correlationId,
        };
        _registry.SetInstructed(inst);

        var batch = _registry.All
            .Where(m => m.Status == ModuleStatus.Operating)
            .Select(m => new PickingInstruction
            {
                Address  = m.Address,
                Quantity = int.TryParse(m.DisplayData.Trim(), out int q) ? q : 0,
                Color    = m.Color,
                LedMode  = m.LedMode,
                BuzMode  = m.BuzMode,
                CorrelationId = m.CorrelationId,
                BlockNo  = m.BlockNo ?? "00",
            })
            .ToList();

        if (!batch.Any(i => i.Address == inst.Address))
            batch.Add(inst);

        try
        {
            await _gateway.InstructBatchAsync(batch, ct);
        }
        catch (AioiNackException)
        {
            _log.LogInformation("[QuickLight] P1 NACKed — retrying with P5 (overwrite)");
            await _gateway.OverwriteBatchAsync(batch, ct);
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
    public Task ExtinguishAsync(string address, CancellationToken ct = default)
    {
        _registry.SetTerminated(address);
        return _gateway.TerminateAsync(address, ct);
    }

    // ─── Confirmation handling ─────────────────────────────────────────────

    private void OnConfirmed(ConfirmationEvent evt)
    {
        var state = _registry.SetConfirmed(evt);
        var addr = evt.Address.PadLeft(4, '0');

        ItemConfirmed?.Invoke(evt, state);

        // Track batches that just completed
        var completedBatches = new List<BatchState>();

        lock (_lock)
        {
            foreach (var batch in _activeBatches.Values)
            {
                if (batch.PendingAddresses.Remove(addr))
                {
                    if (batch.PendingAddresses.Count == 0)
                        completedBatches.Add(batch);
                }
            }
        }

        foreach (var batch in completedBatches)
        {
            if (_activeBatches.TryRemove(batch.BlockNo, out _))
            {
                _log.LogInformation("Batch {Block} complete", batch.BlockNo);
                BatchCompleted?.Invoke(batch.CorrelationId);
            }
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
                i.CorrelationId ?? ""))
            .Order(StringComparer.Ordinal));

    public void Dispose()
    {
        _gateway.Confirmed -= OnConfirmed;
    }
}
