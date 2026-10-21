using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core;

/// <summary>Business ownership and replay guards. Durable transitions precede observers.</summary>
public sealed class LightModuleRegistry
{
    internal object SyncRoot { get; } = new();
    private readonly Dictionary<string, LightModuleState> _modules = new(StringComparer.Ordinal);
    private readonly List<CompletedItemReceipt> _completed = [];
    private readonly List<InterruptReceipt> _interrupts = [];
    private readonly List<BatchCompletionEvent> _batchCompletions = [];
    private readonly List<ConfirmationMissingEvent> _missingConfirmations = [];
    private readonly List<ClosedSessionReceipt> _closedSessions = [];
    internal const int InterruptHistoryLimit = 256; // Less than the 1000-value sequence space.
    internal const int CompletedHistoryLimit = 100_000;
    internal static readonly TimeSpan InterruptRetention = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(24);

    internal void ThrowIfSessionClosed(string blockNo, string? correlationId)
    {
        lock (SyncRoot)
        {
            PruneClosedSessions();
            if (_closedSessions.Any(c => c.BlockNo == blockNo && c.CorrelationId == correlationId))
                throw new PickingCommandRejectedException("SESSION_CLOSED", "This block session is closing or was already released.");
        }
    }

    internal void BeginSessionClose(string blockNo, string correlationId)
    {
        lock (SyncRoot)
        {
            PruneClosedSessions();
            if (_closedSessions.Any(c => c.BlockNo == blockNo && c.CorrelationId == correlationId))
                return;
            if (_closedSessions.Count >= CompletedHistoryLimit)
                throw new IOException("Closed-session replay history is full; reconcile before releasing more sessions.");
            var receipt = new ClosedSessionReceipt
            {
                BlockNo = blockNo, CorrelationId = correlationId, ClosedAt = DateTime.UtcNow, Closing = true
            };
            _closedSessions.Add(receipt);
            try { PersistenceRequired?.Invoke(); }
            catch { _closedSessions.Remove(receipt); throw; }
        }
    }

    /// <summary>One durable commit fences the old session and releases only its remaining live modules.</summary>
    internal void CloseSession(string blockNo, string correlationId)
    {
        lock (SyncRoot)
        {
            if (_interrupts.Any(i => i.PendingState is { } state && state.BlockNo == blockNo && state.CorrelationId == correlationId)
                || _batchCompletions.Any(c => c.BlockNo == blockNo && c.CorrelationId == correlationId)
                || _missingConfirmations.Any(e => e.BlockNo == blockNo && e.CorrelationId == correlationId))
                throw new IOException("Session events must be durably delivered before releasing ownership.");
            PruneClosedSessions();
            var previousReceipt = _closedSessions.FirstOrDefault(c => c.BlockNo == blockNo && c.CorrelationId == correlationId);
            if (previousReceipt is null && _closedSessions.Count >= CompletedHistoryLimit)
                throw new IOException("Closed-session replay history is full; reconcile before releasing more sessions.");
            var selected = _modules.Values.Where(m => m.Status == ModuleStatus.Operating
                && m.BlockNo == blockNo && m.CorrelationId == correlationId).Select(Clone).ToArray();
            var receipt = new ClosedSessionReceipt { BlockNo = blockNo, CorrelationId = correlationId, ClosedAt = DateTime.UtcNow };
            if (previousReceipt is not null) _closedSessions.Remove(previousReceipt);
            _closedSessions.Add(receipt);
            foreach (var module in selected)
                _modules[module.Address] = Terminated(module);
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                foreach (var module in selected)
                    _modules[module.Address] = module;
                _closedSessions.Remove(receipt);
                if (previousReceipt is not null) _closedSessions.Add(previousReceipt);
                throw;
            }
            foreach (var module in selected)
                Notify(Clone(_modules[module.Address]));
        }
    }

    private void PruneClosedSessions() => _closedSessions.RemoveAll(c => !c.Closing && c.ClosedAt < DateTime.UtcNow - CompletedRetention);

    internal event Action? PersistenceRequired;
    public event Action<LightModuleState>? StateChanged;
    public IReadOnlyList<LightModuleState> All
    {
        get
        {
            lock (SyncRoot)
                return _modules.Values.Select(Clone).OrderBy(m => m.Address).ToArray();
        }
    }
    public LightModuleState? Get(string address)
    {
        lock (SyncRoot)
            return _modules.TryGetValue(Normalize(address), out var m) ? Clone(m) : null;
    }
    public int ActiveCount
    {
        get
        {
            lock (SyncRoot)
                return _modules.Values.Count(m => m.Status == ModuleStatus.Operating);
        }
    }
    public int PendingConfirmCount
    {
        get
        {
            lock (SyncRoot)
                return _modules.Values.Count(m => m.Status == ModuleStatus.Completed);
        }
    }

    public LightModuleState SetInstructed(PickingInstruction instruction, DateTime? instructedAt = null)
        => SetInstructedBatch([instruction], instructedAt)[0];

    public IReadOnlyList<LightModuleState> SetInstructedBatch(IReadOnlyList<PickingInstruction> instructions,
        DateTime? instructedAt = null)
    {
        lock (SyncRoot)
        {
            var previous = instructions.ToDictionary(i => Normalize(i.Address),
                i => _modules.TryGetValue(Normalize(i.Address), out var state) ? state : null,
                StringComparer.Ordinal);
            foreach (var instruction in instructions)
            {
                var address = Normalize(instruction.Address);
                _modules[address] = new LightModuleState
                {
                    Address = address,
                    Status = ModuleStatus.Operating,
                    DisplayData = instruction.FormattedDisplay,
                    Color = instruction.Color,
                    LedMode = instruction.LedMode,
                    SegMode = instruction.SegMode,
                    BuzMode = instruction.BuzMode,
                    AfterConfirm = previous[address]?.AfterConfirm,
                    BuzAfterConfirm = previous[address]?.BuzAfterConfirm,
                    BlockNo = instruction.BlockNo,
                    CorrelationId = instruction.CorrelationId,
                    CommandItemId = instruction.CommandItemId,
                    PickingDetailId = instruction.PickingDetailId,
                    LastInstructionAt = instructedAt ?? DateTime.UtcNow
                };
            }
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                foreach (var entry in previous)
                    if (entry.Value is null)
                        _modules.Remove(entry.Key);
                    else
                        _modules[entry.Key] = entry.Value;
                throw;
            }
            var snapshots = instructions.Select(i => Clone(_modules[Normalize(i.Address)])).ToArray();
            foreach (var snapshot in snapshots)
                Notify(snapshot);
            return snapshots;
        }
    }

    public LightModuleState? SetConfirmed(ConfirmationEvent evt)
    {
        lock (SyncRoot)
        {
            var address = Normalize(evt.Address);
            if (!_modules.TryGetValue(address, out var current))
                return null;
            PruneCompleted();
            var oldCompleted = _completed.ToArray();
            var oldInterrupts = _interrupts.ToArray();
            var oldBatchCompletions = _batchCompletions.ToArray();
            if (_batchCompletions.Count >= InterruptHistoryLimit)
                throw new IOException("Pending batch completion capacity reached; durable delivery must recover first.");
            if (_interrupts.Count >= InterruptHistoryLimit && _interrupts.All(i => i.PendingState is not null))
                throw new IOException("Pending confirmation capacity reached; durable delivery must recover first.");
            var next = Clone(current);
            next.Status = evt.Status == CompletionStatus.Shortage ? ModuleStatus.Shortage : ModuleStatus.Completed;
            next.ConfirmationMissingAt = null;
            next.ConfirmationMissingEventId = null;
            next.LastConfirmedAt = evt.ReceivedAt;
            next.AdjustedQuantity = int.TryParse(evt.AdjustedDisplay?.Trim(), out var quantity) ? quantity : null;
            if (HasIdentity(next) && !IsCompletedItem(next.CorrelationId, next.CommandItemId, next.PickingDetailId))
            {
                if (_completed.Count >= CompletedHistoryLimit)
                    throw new IOException("Completed-item replay history is full; reconcile before accepting more work.");
                _completed.Add(new CompletedItemReceipt
                {
                    CorrelationId = next.CorrelationId,
                    CommandItemId = next.CommandItemId,
                    PickingDetailId = next.PickingDetailId,
                    CompletedAt = evt.ReceivedAt
                });
            }
            if (_interrupts.Count >= InterruptHistoryLimit)
            {
                var removable = _interrupts.FindIndex(i => i.PendingState is null);
                if (removable < 0)
                    throw new IOException("Pending confirmation capacity reached; durable delivery must recover first.");
                _interrupts.RemoveAt(removable);
            }
            _interrupts.Add(new InterruptReceipt
            {
                SequenceNo = evt.SequenceNo,
                Address = address,
                Status = evt.Status,
                AdjustedDisplay = evt.AdjustedDisplay,
                ProcessedAt = evt.ReceivedAt,
                EventId = evt.EventId ?? Guid.NewGuid().ToString("N"),
                Source = evt.Source,
                PendingState = Clone(next)
            });
            if (!_modules.Values.Any(m => m.Address != address && m.BlockNo == next.BlockNo
                && m.CorrelationId == next.CorrelationId && m.Status == ModuleStatus.Operating && m.LastInstructionAt is not null))
            {
                _batchCompletions.Add(new BatchCompletionEvent
                {
                    EventId = Guid.NewGuid().ToString("N"), BlockNo = next.BlockNo,
                    CorrelationId = next.CorrelationId, CompletedAt = evt.ReceivedAt
                });
            }
            try
            {
                return Change(address, () => next);
            }
            catch
            {
                _completed.Clear();
                _completed.AddRange(oldCompleted);
                _interrupts.Clear();
                _interrupts.AddRange(oldInterrupts);
                _batchCompletions.Clear();
                _batchCompletions.AddRange(oldBatchCompletions);
                throw;
            }
        }
    }

    /// <summary>Must be checked before resolving the address's current business owner.</summary>
    internal bool IsProcessedInterrupt(ConfirmationEvent evt)
    {
        lock (SyncRoot)
        {
            _interrupts.RemoveAll(i => i.PendingState is null && i.ProcessedAt < DateTime.UtcNow - InterruptRetention);
            var existing = _interrupts.LastOrDefault(i => i.SequenceNo >= 0 && i.SequenceNo == evt.SequenceNo);
            if (existing is null)
                return false;
            if (existing.Address == Normalize(evt.Address) && existing.Status == evt.Status && existing.AdjustedDisplay == evt.AdjustedDisplay)
                return true;
            throw new InvalidDataException("Controller interrupt sequence reused inside the replay window; reconcile controller state.");
        }
    }

    internal IReadOnlyList<InterruptReceipt> PendingConfirmations
    {
        get
        {
            lock (SyncRoot)
                return _interrupts.Where(i => i.PendingState is not null).ToArray();
        }
    }

    internal IReadOnlyList<BatchCompletionEvent> PendingBatchCompletions
    {
        get { lock (SyncRoot) return _batchCompletions.ToArray(); }
    }

    internal void MarkBatchCompletionPublished(BatchCompletionEvent completion)
    {
        lock (SyncRoot)
        {
            if (!_batchCompletions.Remove(completion)) return;
            try { PersistenceRequired?.Invoke(); }
            catch { _batchCompletions.Add(completion); throw; }
        }
    }

    internal bool HasPendingConfirmation(string address)
    {
        lock (SyncRoot)
            return _interrupts.Any(i => i.PendingState is not null && i.Address == Normalize(address));
    }

    internal bool HasConfirmationEventId(string eventId)
    {
        lock (SyncRoot) return _interrupts.Any(i => i.EventId == eventId);
    }

    internal IReadOnlyList<ConfirmationMissingEvent> PendingMissingConfirmations
    {
        get { lock (SyncRoot) return _missingConfirmations.ToArray(); }
    }

    internal void SetConfirmationMissing(LightModuleState instruction, ModuleStatus observedStatus)
    {
        lock (SyncRoot)
        {
            if (!_modules.TryGetValue(instruction.Address, out var current)
                || current.Status != ModuleStatus.Operating || !SameInstruction(current, instruction)
                || current.ConfirmationMissingAt is not null)
                return;
            if (_missingConfirmations.Count >= InterruptHistoryLimit)
                throw new IOException("Pending missing-confirmation capacity reached; recover durable delivery first.");
            var next = Clone(current);
            next.ObservedStatus = observedStatus;
            next.ConfirmationMissingAt = DateTime.UtcNow;
            next.ConfirmationMissingEventId = Guid.NewGuid().ToString("N");
            var notification = new ConfirmationMissingEvent
            {
                EventId = next.ConfirmationMissingEventId, Address = next.Address, BlockNo = next.BlockNo,
                CorrelationId = next.CorrelationId, CommandItemId = next.CommandItemId,
                PickingDetailId = next.PickingDetailId, ObservedStatus = observedStatus,
                DetectedAt = next.ConfirmationMissingAt.Value, LastInstructionAt = next.LastInstructionAt!.Value
            };
            _missingConfirmations.Add(notification);
            try { Change(next.Address, () => next); }
            catch { _missingConfirmations.Remove(notification); throw; }
        }
    }

    internal void ClearConfirmationMissing(LightModuleState instruction, bool reinstructed = false)
    {
        lock (SyncRoot)
        {
            if (!_modules.TryGetValue(instruction.Address, out var current) || !SameInstruction(current, instruction)
                || current.ConfirmationMissingEventId != instruction.ConfirmationMissingEventId)
                return;
            Change(current.Address, () =>
            {
                var next = Clone(current);
                next.ConfirmationMissingAt = null;
                next.ConfirmationMissingEventId = null;
                if (reinstructed) next.LastInstructionAt = DateTime.UtcNow;
                return next;
            });
        }
    }

    internal void MarkMissingConfirmationPublished(ConfirmationMissingEvent notification)
    {
        lock (SyncRoot)
        {
            if (!_missingConfirmations.Remove(notification)) return;
            try { PersistenceRequired?.Invoke(); }
            catch { _missingConfirmations.Add(notification); throw; }
        }
    }

    internal static bool SameInstruction(LightModuleState current, LightModuleState expected) =>
        current.Address == expected.Address && current.BlockNo == expected.BlockNo
        && current.CorrelationId == expected.CorrelationId && current.CommandItemId == expected.CommandItemId
        && current.PickingDetailId == expected.PickingDetailId && current.LastInstructionAt == expected.LastInstructionAt;

    internal void MarkConfirmationPublished(InterruptReceipt receipt)
    {
        lock (SyncRoot)
        {
            var pending = receipt.PendingState;
            receipt.PendingState = null;
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                receipt.PendingState = pending;
                throw;
            }
        }
    }

    internal bool IsCompletedItem(PickingInstruction instruction) => IsCompletedItem(instruction.CorrelationId, instruction.CommandItemId, instruction.PickingDetailId);
    private bool IsCompletedItem(string? correlation, string? item, string? detail)
    {
        lock (SyncRoot)
        {
            PruneCompleted();
            return _completed.Any(c => c.CorrelationId == correlation && (!string.IsNullOrEmpty(item)
                ? c.CommandItemId == item : !string.IsNullOrEmpty(detail) && c.PickingDetailId == detail));
        }
    }

    public void SetTerminated(string address)
    {
        lock (SyncRoot)
            if (_modules.TryGetValue(Normalize(address), out var current))
                Change(current.Address, () => Terminated(current));
    }
    public int SetTerminatedForBlock(string blockNo = "00")
    {
        lock (SyncRoot)
        {
            var selected = _modules.Values.Where(m => blockNo == "00" || m.BlockNo == blockNo).Select(Clone).ToArray();
            foreach (var current in selected)
                _modules[current.Address] = Terminated(current);
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                foreach (var current in selected)
                    _modules[current.Address] = current;
                throw;
            }
            foreach (var current in selected)
                Notify(Clone(_modules[current.Address]));
            return selected.Length;
        }
    }
    public LightModuleState SetConfigured(string address, ModuleParameters parameters, string displayData)
        => Change(Normalize(address), () =>
        {
            var next = _modules.TryGetValue(Normalize(address), out var current) ? Clone(current) : new LightModuleState { Address = Normalize(address), Status = ModuleStatus.Operating };
            next.DisplayData = displayData;
            next.Color = parameters.Color;
            next.LedMode = parameters.LedMode;
            next.SegMode = parameters.SegMode;
            next.BuzMode = parameters.BuzMode;
            if (parameters.AfterConfirm != LightState.NoChange) next.AfterConfirm = parameters.AfterConfirm;
            if (parameters.BuzAfterConfirm != LightState.NoChange) next.BuzAfterConfirm = parameters.BuzAfterConfirm;
            next.LastInstructionAt = DateTime.UtcNow;
            return next;
        });
    public LightModuleState SetScanned(string address, ModuleStatus status)
    {
        lock (SyncRoot)
        {
            var addr = Normalize(address);
            var next = _modules.TryGetValue(addr, out var current) ? Clone(current) : new LightModuleState { Address = addr, Status = status };
            next.ObservedStatus = status;
            if (next.LastInstructionAt is null)
                next.Status = status;
            _modules[addr] = next;
            Notify(Clone(next));
            return Clone(next);
        }
    }
    public void Clear()
    {
        lock (SyncRoot)
        {
            var old = _modules.ToArray();
            _modules.Clear();
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                foreach (var entry in old)
                    _modules.Add(entry.Key, entry.Value);
                throw;
            }
        }
    }

    internal RegistrySnapshot Snapshot()
    {
        lock (SyncRoot)
        {
            PruneCompleted();
            PruneClosedSessions();
            return new RegistrySnapshot
            {
                Modules = _modules.Values.Where(m => m.LastInstructionAt is not null).Select(Clone).ToList(),
                CompletedItems = _completed.ToList(),
                Interrupts = _interrupts.ToList(),
                PendingBatchCompletions = _batchCompletions.ToList(),
                PendingMissingConfirmations = _missingConfirmations.ToList(),
                ClosedSessions = _closedSessions.ToList()
            };
        }
    }
    internal void Restore(RegistrySnapshot snapshot)
    {
        lock (SyncRoot)
        {
            foreach (var module in snapshot.Modules)
                _modules[module.Address] = Clone(module);
            _interrupts.Clear();
            _interrupts.AddRange(snapshot.Interrupts.TakeLast(InterruptHistoryLimit));
            _completed.Clear();
            _completed.AddRange(snapshot.CompletedItems);
            _batchCompletions.Clear();
            _batchCompletions.AddRange(snapshot.PendingBatchCompletions);
            _missingConfirmations.Clear();
            _missingConfirmations.AddRange(snapshot.PendingMissingConfirmations);
            _closedSessions.Clear();
            _closedSessions.AddRange(snapshot.ClosedSessions);
            PruneCompleted();
            PruneClosedSessions();
        }
    }
    private void PruneCompleted() => _completed.RemoveAll(c => c.CompletedAt < DateTime.UtcNow - CompletedRetention
        && !_interrupts.Any(i => i.PendingState is { } state && state.CorrelationId == c.CorrelationId
            && state.CommandItemId == c.CommandItemId && state.PickingDetailId == c.PickingDetailId));
    private static bool HasIdentity(LightModuleState state) => !string.IsNullOrEmpty(state.CommandItemId) || !string.IsNullOrEmpty(state.PickingDetailId);
    private LightModuleState Change(string address, Func<LightModuleState> create)
    {
        lock (SyncRoot)
        {
            _modules.TryGetValue(address, out var previous);
            var next = create();
            _modules[address] = next;
            try
            {
                PersistenceRequired?.Invoke();
            }
            catch
            {
                if (previous is null)
                    _modules.Remove(address);
                else
                    _modules[address] = previous;
                throw;
            }
            var snapshot = Clone(next);
            Notify(snapshot);
            return snapshot;
        }
    }
    private void Notify(LightModuleState state)
    {
        if (StateChanged is null)
            return;
        foreach (Action<LightModuleState> observer in StateChanged.GetInvocationList())
        {
            try
            {
                observer(state);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("State observer failed after durable commit: {0}", ex.Message);
            }
        }
    }
    private static LightModuleState Terminated(LightModuleState current)
    {
        var next = Clone(current);
        next.Status = ModuleStatus.Standby;
        next.Color = LightColor.Off;
        next.LedMode = LightState.Off;
        next.BuzMode = LightState.Off;
        next.CorrelationId = null;
        next.CommandItemId = null;
        next.PickingDetailId = null;
        next.ConfirmationMissingAt = null;
        next.ConfirmationMissingEventId = null;
        return next;
    }
    private static string Normalize(string address) => address.PadLeft(4, '0')[^4..];
    internal static LightModuleState Clone(LightModuleState module) => new()
    {
        Address = module.Address,
        Status = module.Status,
        ObservedStatus = module.ObservedStatus,
        ConfirmationMissingAt = module.ConfirmationMissingAt,
        ConfirmationMissingEventId = module.ConfirmationMissingEventId,
        DisplayData = module.DisplayData,
        Color = module.Color,
        LedMode = module.LedMode,
        SegMode = module.SegMode,
        BuzMode = module.BuzMode,
        AfterConfirm = module.AfterConfirm,
        BuzAfterConfirm = module.BuzAfterConfirm,
        LastInstructionAt = module.LastInstructionAt,
        LastConfirmedAt = module.LastConfirmedAt,
        AdjustedQuantity = module.AdjustedQuantity,
        BlockNo = module.BlockNo,
        CorrelationId = module.CorrelationId,
        CommandItemId = module.CommandItemId,
        PickingDetailId = module.PickingDetailId,
    };
}

internal sealed class RegistrySnapshot
{
    public int Version { get; set; } = 2;
    public List<LightModuleState> Modules { get; set; } = [];
    public List<CompletedItemReceipt> CompletedItems { get; set; } = [];
    public List<InterruptReceipt> Interrupts { get; set; } = [];
    public List<BatchCompletionEvent> PendingBatchCompletions { get; set; } = [];
    public List<ConfirmationMissingEvent> PendingMissingConfirmations { get; set; } = [];
    public List<ClosedSessionReceipt> ClosedSessions { get; set; } = [];
}
internal sealed class ClosedSessionReceipt
{
    public string BlockNo { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTime ClosedAt { get; set; }
    public bool Closing { get; set; }
}
internal sealed class CompletedItemReceipt
{
    public string? CorrelationId { get; set; }
    public string? CommandItemId { get; set; }
    public string? PickingDetailId { get; set; }
    public DateTime CompletedAt { get; set; }
}
internal sealed class InterruptReceipt
{
    public int SequenceNo { get; set; }
    public string Address { get; set; } = "";
    public CompletionStatus Status { get; set; }
    public string? AdjustedDisplay { get; set; }
    public DateTime ProcessedAt { get; set; }
    public string EventId { get; set; } = "";
    public string? Source { get; set; }
    public LightModuleState? PendingState { get; set; }
    internal ConfirmationEvent ToEvent() => new()
    {
        Address = Address,
        SequenceNo = SequenceNo,
        Status = Status,
        AdjustedDisplay = AdjustedDisplay,
        ReceivedAt = ProcessedAt,
        EventId = EventId,
        Source = Source
    };
}
