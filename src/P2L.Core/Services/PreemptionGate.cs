namespace P2L.Core.Services;

/// <summary>
/// Two-priority gate: high-priority callers (MQTT commands) preempt low-priority work
/// (CSV task runner). The runner calls WaitIfPreemptedAsync before each task step;
/// MQTT dispatcher wraps each command handler in Preempt/Release.
/// </summary>
public sealed class PreemptionGate
{
    private int _count;

    /// <summary>Signal that a high-priority operation is starting. May be called concurrently.</summary>
    public void Preempt()  => Interlocked.Increment(ref _count);

    /// <summary>Signal that the high-priority operation has finished.</summary>
    public void Release()  => Interlocked.Decrement(ref _count);

    /// <summary>Async-wait until no high-priority operation is active.</summary>
    public async Task WaitIfPreemptedAsync(CancellationToken ct)
    {
        while (Volatile.Read(ref _count) > 0 && !ct.IsCancellationRequested)
            await Task.Delay(50, ct);
    }

    public bool IsPreempted => Volatile.Read(ref _count) > 0;
}
