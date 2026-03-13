using Microsoft.Extensions.Logging;
using P2L.Hardware;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>
/// Orchestrates picking sessions: sends batch instructions to the gateway,
/// tracks confirmations, and provides completion callbacks.
///
/// Typical flow:
///   1. Build a list of <see cref="PickingInstruction"/> objects.
///   2. Call <see cref="StartBatchAsync"/> — lights up all modules.
///   3. Operators pick items and press CONFIRM; <see cref="ItemConfirmed"/> fires.
///   4. When <see cref="IsBatchComplete"/> is true, call <see cref="FinishBatchAsync"/>.
/// </summary>
public sealed class PickingEngine : IDisposable
{
    private readonly AioiGateway         _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger<PickingEngine> _log;

    private readonly HashSet<string>    _pendingAddresses = new();
    private readonly object             _lock             = new();

    /// <summary>Fires when an operator presses CONFIRM on any module in the current batch.</summary>
    public event Action<ConfirmationEvent, LightModuleState?>? ItemConfirmed;

    /// <summary>Fires when every module in the active batch has been confirmed.</summary>
    public event Action? BatchCompleted;

    public bool IsBatchActive   { get; private set; }
    public bool IsBatchComplete => IsBatchActive && _pendingAddresses.Count == 0;

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
    /// If a batch is already active, throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task StartBatchAsync(IReadOnlyList<PickingInstruction> instructions,
                                      CancellationToken ct = default)
    {
        if (IsBatchActive)
            throw new InvalidOperationException("A batch is already active. Call FinishBatchAsync first.");

        lock (_lock)
        {
            _pendingAddresses.Clear();
            foreach (var inst in instructions)
                _pendingAddresses.Add(inst.Address.PadLeft(4, '0'));
        }

        IsBatchActive = true;

        // Update registry before sending (optimistic)
        foreach (var inst in instructions)
            _registry.SetInstructed(inst);

        await _gateway.InstructBatchAsync(instructions, ct);

        _log.LogInformation("Batch started: {Count} modules", instructions.Count);
    }

    /// <summary>
    /// Terminates the active batch (turns off all lights) and resets state.
    /// </summary>
    public async Task FinishBatchAsync(string blockNo = "00", CancellationToken ct = default)
    {
        await _gateway.TerminateBlockAsync(blockNo, ct);

        lock (_lock)
        {
            foreach (var addr in _pendingAddresses)
                _registry.SetTerminated(addr);
            _pendingAddresses.Clear();
        }

        IsBatchActive = false;
        _log.LogInformation("Batch finished");
    }

    /// <summary>
    /// Lights a single module without turning off other active modules.
    /// Re-sends P1 with all currently active modules plus this one so the
    /// block replace does not extinguish lights that are already on.
    /// Does not affect active batch tracking.
    /// </summary>
    public async Task QuickLightAsync(string address, int quantity,
                                      LightColor color   = LightColor.Green,
                                      LightState ledMode = LightState.On,
                                      LightState buzMode = LightState.Off,
                                      CancellationToken ct = default)
    {
        var inst = new PickingInstruction
        {
            Address  = address.PadLeft(4, '0'),
            Quantity = quantity,
            Color    = color,
            LedMode  = ledMode,
            BuzMode  = buzMode,
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
            })
            .ToList();

        if (!batch.Any(i => i.Address == inst.Address))
            batch.Add(inst);

        // Per Aioi spec §3.4: P1 can only instruct standby modules. If one or
        // more modules are already under instruction, P1 is NACKed and we must
        // retry with P5 (rewritable overwrite). Trying P1 first is correct for
        // the common case (fresh start / after Z), avoiding an unnecessary P5
        // round-trip when all modules are in standby.
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
    /// If <paramref name="newQuantity"/> is null the currently displayed quantity is preserved;
    /// if the module is not yet in the registry, the display defaults to zero.
    /// Does not affect active batch tracking.
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

        bool removed;
        lock (_lock)
            removed = _pendingAddresses.Remove(evt.Address.PadLeft(4, '0'));

        ItemConfirmed?.Invoke(evt, state);

        if (IsBatchActive && removed && _pendingAddresses.Count == 0)
        {
            // Auto-reset so the next batch_start is accepted without requiring
            // an explicit FinishBatchAsync call from the WMS after batch_completed.
            IsBatchActive = false;
            _log.LogInformation("All modules confirmed — batch complete");
            BatchCompleted?.Invoke();
        }
    }

    public void Dispose()
    {
        _gateway.Confirmed -= OnConfirmed;
    }
}
