using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace P2L.Mqtt;

internal enum CommandAdmission { Accepted, Duplicate, Conflict, Full, Stopped }

/// <summary>One bounded executor; identical identified retries share pending work.</summary>
internal sealed class CommandAdmissionQueue
{
    private sealed record Work(string Key, string? CommandId, byte[] Payload, DateTimeOffset Deadline);
    private readonly Channel<Work> _queue;
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _pendingIds = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly int _executionTimeoutMs;
    private bool _stopping;
    private readonly Task _worker;
    public int Count { get { lock (_sync) return _pending.Count; } }

    public CommandAdmissionQueue(int capacity, Func<byte[], CancellationToken, Task> execute,
        CancellationToken lifetime, Action<Exception> failed, int executionTimeoutMs = 25000)
    {
        if (capacity is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (executionTimeoutMs is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(executionTimeoutMs));
        _capacity = capacity;
        _executionTimeoutMs = executionTimeoutMs;
        _queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(capacity)
            { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _worker = Task.Run(async () =>
        {
            // Drain accepted work even on stop: dispatcher records cancelled queued
            // intents while the lifetime token cancels only actual execution.
            await foreach (var work in _queue.Reader.ReadAllAsync())
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                var remaining = work.Deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) budget.Cancel();
                else budget.CancelAfter(remaining);
                try { await execute(work.Payload, budget.Token); }
                catch (Exception ex)
                {
                    try { failed(ex); }
                    catch { /* A failed logger must not stop other accepted commands. */ }
                }
                finally
                {
                    lock (_sync)
                    {
                        _pending.Remove(work.Key);
                        if (work.CommandId is not null) _pendingIds.Remove(work.CommandId);
                    }
                }
            }
        });
    }

    public CommandAdmission TryEnqueue(byte[] payload, Action? full = null)
    {
        string? commandId = null;
        var deadline = DateTimeOffset.UtcNow.AddMilliseconds(_executionTimeoutMs);
        var legacyReceipt = false;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("executionDeadlineAt", out var executionDeadline)
                && executionDeadline.ValueKind == JsonValueKind.String
                && executionDeadline.TryGetDateTimeOffset(out var callerDeadline) && callerDeadline < deadline)
                deadline = callerDeadline;
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("commandId", out var id)
                && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())) commandId = id.GetString();
            if (commandId is null && doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("correlationId", out var correlation)
                && correlation.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(correlation.GetString())
                && doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                legacyReceipt = type.GetString() is "batch_start" or "light_on";
        }
        catch (JsonException) { }
        var key = commandId is not null || legacyReceipt ? Convert.ToHexString(SHA256.HashData(payload)) : Guid.NewGuid().ToString("N");
        commandId = commandId is not null ? "command:" + commandId : legacyReceipt ? "legacy:" + key : null;
        lock (_sync)
        {
            if (_stopping) return CommandAdmission.Stopped;
            if (commandId is not null && _pendingIds.TryGetValue(commandId, out var previous))
                return previous == key ? CommandAdmission.Duplicate : CommandAdmission.Conflict;
            if (_pending.Count >= _capacity)
            {
                // Keep the receipt lookup/rejection atomic against new admissions.
                full?.Invoke();
                return CommandAdmission.Full;
            }
            _pending.Add(key);
            if (commandId is not null) _pendingIds.Add(commandId, key);
            if (_queue.Writer.TryWrite(new Work(key, commandId, payload, deadline))) return CommandAdmission.Accepted;
            _pending.Remove(key);
            if (commandId is not null) _pendingIds.Remove(commandId);
            return CommandAdmission.Stopped;
        }
    }

    public Task StopAsync()
    {
        lock (_sync) { _stopping = true; _queue.Writer.TryComplete(); }
        return _worker;
    }
}
