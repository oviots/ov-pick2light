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

    /// <summary>Fires whenever any module's state is mutated (instructed, confirmed, terminated, etc.).</summary>
    public event Action<LightModuleState>? StateChanged;

    /// <summary>Returns all currently tracked modules (snapshot).</summary>
    public IReadOnlyList<LightModuleState> All =>
        _modules.Values.Select(Clone).OrderBy(m => m.Address).ToList();

    /// <summary>Returns the state of a single module, or null if not tracked.</summary>
    public LightModuleState? Get(string address) =>
        _modules.TryGetValue(Normalize(address), out var m) ? Clone(m) : null;

    /// <summary>
    /// Registers or updates a module's state following a pick instruction being sent.
    /// </summary>
    public LightModuleState SetInstructed(PickingInstruction instruction, DateTime? instructedAt = null)
    {
        var addr = Normalize(instruction.Address);
        var state = _modules.AddOrUpdate(addr,
            _ => new LightModuleState
            {
                Address           = addr,
                Status            = ModuleStatus.Operating,
                DisplayData       = instruction.FormattedDisplay,
                Color             = instruction.Color,
                LedMode           = instruction.LedMode,
                SegMode           = instruction.SegMode,
                BuzMode           = instruction.BuzMode,
                BlockNo           = instruction.BlockNo,
                CorrelationId     = instruction.CorrelationId,
                CommandItemId     = instruction.CommandItemId,
                PickingDetailId   = instruction.PickingDetailId,
                LastInstructionAt = instructedAt ?? DateTime.UtcNow,
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Status            = ModuleStatus.Operating;
                    existing.DisplayData       = instruction.FormattedDisplay;
                    existing.Color             = instruction.Color;
                    existing.LedMode           = instruction.LedMode;
                    existing.SegMode           = instruction.SegMode;
                    existing.BuzMode           = instruction.BuzMode;
                    existing.BlockNo           = instruction.BlockNo;
                    existing.CorrelationId     = instruction.CorrelationId;
                    existing.CommandItemId     = instruction.CommandItemId;
                    existing.PickingDetailId   = instruction.PickingDetailId;
                    existing.LastInstructionAt = instructedAt ?? DateTime.UtcNow;
                }
                return existing;
            });
        var snapshot = Clone(state);
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Updates a module's state when a CONFIRM event is received.
    /// </summary>
    public LightModuleState? SetConfirmed(ConfirmationEvent evt)
    {
        var addr = Normalize(evt.Address);
        if (!_modules.TryGetValue(addr, out var module)) return null;

        lock (module)
        {
            module.Status          = evt.Status == CompletionStatus.Shortage
                ? ModuleStatus.Shortage
                : ModuleStatus.Completed;
            module.LastConfirmedAt  = evt.ReceivedAt;
            module.AdjustedQuantity = evt.AdjustedDisplay is not null
                ? int.TryParse(evt.AdjustedDisplay.Trim(), out int qty) ? qty : null
                : null;
        }
        var snapshot = Clone(module);
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Marks a module as terminated (standby) after a D command is sent.
    /// </summary>
    public void SetTerminated(string address)
    {
        var addr = Normalize(address);
        if (_modules.TryGetValue(addr, out var module))
        {
            SetTerminated(module);
        }
    }

    /// <summary>Marks every tracked module in the block as terminated. Block "00" means all modules.</summary>
    public int SetTerminatedForBlock(string blockNo = "00")
    {
        var count = 0;
        foreach (var module in _modules.Values)
        {
            if (blockNo != "00")
            {
                string moduleBlock;
                lock (module)
                    moduleBlock = module.BlockNo;
                if (moduleBlock != blockNo) continue;
            }

            SetTerminated(module);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Updates a module's state after a <c>ConfigureModuleAsync</c> call.
    /// Overwrites colour, LED/SEG/BUZ modes and display data; preserves confirmation history.
    /// </summary>
    public LightModuleState SetConfigured(string address, ModuleParameters parameters, string displayData)
    {
        var addr = Normalize(address);
        var state = _modules.AddOrUpdate(addr,
            _ => new LightModuleState
            {
                Address           = addr,
                Status            = ModuleStatus.Operating,
                DisplayData       = displayData,
                Color             = parameters.Color,
                LedMode           = parameters.LedMode,
                SegMode           = parameters.SegMode,
                BuzMode           = parameters.BuzMode,
                LastInstructionAt = DateTime.UtcNow,
            },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.DisplayData       = displayData;
                    existing.Color             = parameters.Color;
                    existing.LedMode           = parameters.LedMode;
                    existing.SegMode           = parameters.SegMode;
                    existing.BuzMode           = parameters.BuzMode;
                    existing.LastInstructionAt = DateTime.UtcNow;
                }
                return existing;
            });
        var snapshot = Clone(state);
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Registers a module discovered via an Az scan.
    /// If the module is already tracked, only its status is refreshed; other fields are preserved.
    /// </summary>
    public LightModuleState SetScanned(string address, ModuleStatus status)
    {
        var addr = Normalize(address);
        var state = _modules.AddOrUpdate(addr,
            _ => new LightModuleState { Address = addr, Status = status },
            (_, existing) =>
            {
                lock (existing)
                    existing.Status = status;
                return existing;
            });
        var snapshot = Clone(state);
        StateChanged?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>Removes all tracked modules (e.g. after Z initialization).</summary>
    public void Clear() => _modules.Clear();

    /// <summary>Count of modules currently in the <see cref="ModuleStatus.Operating"/> state.</summary>
    public int ActiveCount =>
        _modules.Values.Count(m => HasStatus(m, ModuleStatus.Operating));

    /// <summary>Count of modules awaiting termination (CONFIRM pressed).</summary>
    public int PendingConfirmCount =>
        _modules.Values.Count(m => HasStatus(m, ModuleStatus.Completed));

    private static string Normalize(string address) =>
        address.PadLeft(4, '0')[^4..];

    private static bool HasStatus(LightModuleState module, ModuleStatus status)
    {
        lock (module)
            return module.Status == status;
    }

    private void SetTerminated(LightModuleState module)
    {
        lock (module)
        {
            module.Status        = ModuleStatus.Standby;
            module.Color         = LightColor.Off;
            module.LedMode       = LightState.Off;
            module.BuzMode       = LightState.Off;
            module.CorrelationId = null;
            module.CommandItemId = null;
            module.PickingDetailId = null;
        }
        StateChanged?.Invoke(Clone(module));
    }

    private static LightModuleState Clone(LightModuleState module)
    {
        lock (module)
        {
            return new LightModuleState
            {
                Address           = module.Address,
                Status            = module.Status,
                DisplayData       = module.DisplayData,
                Color             = module.Color,
                LedMode           = module.LedMode,
                SegMode           = module.SegMode,
                BuzMode           = module.BuzMode,
                LastInstructionAt = module.LastInstructionAt,
                LastConfirmedAt   = module.LastConfirmedAt,
                AdjustedQuantity  = module.AdjustedQuantity,
                BlockNo           = module.BlockNo,
                CorrelationId     = module.CorrelationId,
                CommandItemId     = module.CommandItemId,
                PickingDetailId   = module.PickingDetailId,
            };
        }
    }
}
