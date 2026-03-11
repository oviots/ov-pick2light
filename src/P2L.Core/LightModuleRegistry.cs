using System.Collections.Concurrent;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>
/// Thread-safe in-memory registry of all known light module states.
/// All state mutations flow through this class to maintain a single source of truth.
/// </summary>
public sealed class LightModuleRegistry
{
    private readonly ConcurrentDictionary<string, LightModuleState> _modules = new();

    /// <summary>Returns all currently tracked modules (snapshot).</summary>
    public IReadOnlyList<LightModuleState> All =>
        _modules.Values.OrderBy(m => m.Address).ToList();

    /// <summary>Returns the state of a single module, or null if not tracked.</summary>
    public LightModuleState? Get(string address) =>
        _modules.TryGetValue(Normalize(address), out var m) ? m : null;

    /// <summary>
    /// Registers or updates a module's state following a pick instruction being sent.
    /// </summary>
    public LightModuleState SetInstructed(PickingInstruction instruction)
    {
        var addr = Normalize(instruction.Address);
        return _modules.AddOrUpdate(addr,
            _ => new LightModuleState
            {
                Address           = addr,
                Status            = ModuleStatus.Operating,
                DisplayData       = instruction.FormattedDisplay,
                Color             = instruction.Color,
                LedMode           = instruction.LedMode,
                BlockNo           = instruction.BlockNo,
                LastInstructionAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                existing.Status            = ModuleStatus.Operating;
                existing.DisplayData       = instruction.FormattedDisplay;
                existing.Color             = instruction.Color;
                existing.LedMode           = instruction.LedMode;
                existing.BlockNo           = instruction.BlockNo;
                existing.LastInstructionAt = DateTime.UtcNow;
                return existing;
            });
    }

    /// <summary>
    /// Updates a module's state when a CONFIRM event is received.
    /// </summary>
    public LightModuleState? SetConfirmed(ConfirmationEvent evt)
    {
        var addr = Normalize(evt.Address);
        if (!_modules.TryGetValue(addr, out var module)) return null;

        module.Status          = evt.Status == CompletionStatus.Shortage
            ? ModuleStatus.Shortage
            : ModuleStatus.Completed;
        module.LastConfirmedAt  = evt.ReceivedAt;
        module.AdjustedQuantity = evt.AdjustedDisplay is not null
            ? int.TryParse(evt.AdjustedDisplay.Trim(), out int qty) ? qty : null
            : null;
        return module;
    }

    /// <summary>
    /// Marks a module as terminated (standby) after a D command is sent.
    /// </summary>
    public void SetTerminated(string address)
    {
        var addr = Normalize(address);
        if (_modules.TryGetValue(addr, out var module))
        {
            module.Status  = ModuleStatus.Standby;
            module.Color   = LightColor.Off;
            module.LedMode = LightState.Off;
        }
    }

    /// <summary>Removes all tracked modules (e.g. after Z initialization).</summary>
    public void Clear() => _modules.Clear();

    /// <summary>Count of modules currently in the <see cref="ModuleStatus.Operating"/> state.</summary>
    public int ActiveCount =>
        _modules.Values.Count(m => m.Status == ModuleStatus.Operating);

    /// <summary>Count of modules awaiting termination (CONFIRM pressed).</summary>
    public int PendingConfirmCount =>
        _modules.Values.Count(m => m.Status == ModuleStatus.Completed);

    private static string Normalize(string address) =>
        address.PadLeft(4, '0')[^4..];
}
