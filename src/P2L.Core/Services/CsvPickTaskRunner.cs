using Microsoft.Extensions.Logging;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core.Services;

/// <summary>
/// Processes a CSV pick-task file in batch mode:
///   1. Read ALL pending rows.
///   2. Light every module at once via PickingEngine.StartBatchAsync.
///   3. Operators confirm in any order; each confirmation fires TaskEvent.
///   4. When all confirmed, delete rows from CSV and fire csv_completed.
///
/// CSV format (header row optional, # lines are comments):
///   address,quantity[,color][,label]
///   0001,5,Red,Part-A
///   0002,3,,Part-B       ← color blank → uses defaultColor
/// </summary>
public sealed class CsvPickTaskRunner : IAsyncDisposable
{
    private readonly PickingEngine  _engine;
    private readonly PreemptionGate _gate;
    private readonly ILogger        _log;

    private CancellationTokenSource? _cts;
    private Task?                    _loop;
    private int                      _paused; // Interlocked flag

    /// <summary>Fires on each task lifecycle event (task_started, task_confirmed, task_error, csv_completed).</summary>
    public event Action<CsvTaskEvent>? TaskEvent;

    public bool IsRunning => _loop is { IsCompleted: false };

    public CsvPickTaskRunner(PickingEngine engine, PreemptionGate gate,
                              ILogger<CsvPickTaskRunner> log)
    {
        _engine = engine;
        _gate   = gate;
        _log    = log;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────

    /// <summary>Start processing the CSV file. Throws if already running.</summary>
    public void Start(string csvPath, LightColor defaultColor)
    {
        if (IsRunning)
            throw new InvalidOperationException("CSV runner is already active. Cancel it first.");

        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(csvPath, defaultColor, _cts.Token));
    }

    public void Pause()  => Interlocked.Exchange(ref _paused, 1);
    public void Resume() => Interlocked.Exchange(ref _paused, 0);

    public async Task CancelAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loop is not null)
                try { await _loop; } catch (OperationCanceledException) { }
        }
    }

    // ─── Main loop ────────────────────────────────────────────────────────

    private async Task RunAsync(string csvPath, LightColor defaultColor, CancellationToken ct)
    {
        const string jobPrefix = "# p2l-job:";
        Action<ConfirmationEvent, LightModuleState?>? checkpoint = null;
        Action<BatchCompletionEvent>? batchCheckpoint = null;
        string? ownedCorrelation = null;
        string[] ownedAddresses = [];
        try
        {
            while (Volatile.Read(ref _paused) == 1)
                await Task.Delay(100, ct);
            await _gate.WaitIfPreemptedAsync(ct);
            if (ReadAllRows(csvPath, defaultColor).Count == 0)
            {
                NotifyTask(new CsvTaskEvent("csv_completed", null));
                return;
            }
            var lines = File.ReadAllLines(csvPath);
            var jobLine = lines.FirstOrDefault(l => l.StartsWith(jobPrefix, StringComparison.Ordinal));
            if (jobLine is null)
            {
                jobLine = jobPrefix + Guid.NewGuid().ToString("N");
                lines = [jobLine, .. lines];
                WriteCheckpoint(csvPath, lines);
            }
            var correlationId = "csv:" + jobLine[jobPrefix.Length..].Trim();
            ownedCorrelation = correlationId;
            var rows = ReadAllRows(csvPath, defaultColor);
            ownedAddresses = rows.Select(r => r.Address).ToArray();
            var byAddress = rows.ToDictionary(r => r.Address, StringComparer.Ordinal);
            var lastCheckpoint = lines;
            PickingInstruction ToInstruction(CsvPickRow row) => new()
            {
                Address = row.Address, Quantity = row.Quantity, Color = row.Color,
                LedMode = LightState.On, CorrelationId = correlationId,
                CommandItemId = correlationId + ":" + row.Address
            };
            var done = new HashSet<string>(StringComparer.Ordinal);
            var checkpointLock = new object();
            var batchDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            CsvPickRow[] UnfinishedRows()
            {
                lock (checkpointLock) return rows.Where(r => !done.Contains(r.Address)).ToArray();
            }
            void SavePicked(CsvPickRow row)
            {
                lock (checkpointLock)
                {
                    if (done.Contains(row.Address)) return;
                    var picked = done.Append(row.Address).ToHashSet(StringComparer.Ordinal);
                    var removedLines = rows.Where(r => picked.Contains(r.Address)).Select(r => r.SourceLine).ToHashSet();
                    if (!File.ReadAllLines(csvPath).SequenceEqual(lastCheckpoint))
                        throw new IOException("CSV changed while picking; reconcile the file before retrying its confirmation.");
                    var remaining = lines.Where((line, index) => !removedLines.Contains(index + 1)
                        && !(picked.Count == rows.Count && line == jobLine)).ToArray();
                    WriteCheckpoint(csvPath, remaining);
                    lastCheckpoint = remaining;
                    done.Add(row.Address);
                    NotifyTask(new CsvTaskEvent("task_confirmed", row));
                    if (done.Count == rows.Count) batchDone.TrySetResult();
                }
            }
            checkpoint = (_, state) =>
            {
                if (state?.CorrelationId == correlationId && byAddress.TryGetValue(state.Address, out var row)
                    && state.CommandItemId == ToInstruction(row).CommandItemId)
                    SavePicked(row);
            };
            batchCheckpoint = completion =>
            {
                lock (checkpointLock)
                    if (completion.CorrelationId == correlationId && done.Count != rows.Count)
                        throw new IOException("CSV completion arrived before its durable row checkpoints.");
            };
            _engine.RequireDurableConfirmationConsumer();
            _engine.DurableConfirmation += checkpoint;
            _engine.DurableBatchCompleted += batchCheckpoint;
            _engine.ReplayPendingConfirmations();
            foreach (var row in rows)
                if (_engine.IsItemCompleted(ToInstruction(row))) SavePicked(row);
            var pending = UnfinishedRows().Select(ToInstruction).ToArray();
            if (pending.Length > 0)
            {
                await _engine.StartBatchAsync(pending, ct, relight: pending.Any(_engine.IsItemOperating));
                foreach (var row in UnfinishedRows())
                    NotifyTask(new CsvTaskEvent("task_started", row));
            }
            else batchDone.TrySetResult();
            await batchDone.Task.WaitAsync(ct);
            // Picked rows are already durable before hardware cleanup starts.
            await _engine.FinishOwnedBatchAsync(correlationId, rows.Select(r => r.Address).ToArray(), ct);
            NotifyTask(new CsvTaskEvent("csv_completed", null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (ownedCorrelation is not null)
                try { await _engine.FinishOwnedBatchAsync(ownedCorrelation, ownedAddresses, CancellationToken.None); }
                catch (Exception ex) { _log.LogWarning(ex, "CSV cancellation cleanup needs reconciliation"); }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CSV] Runner failed; picked row checkpoints are retained");
            NotifyTask(new CsvTaskEvent("task_error", null));
        }
        finally
        {
            if (checkpoint is not null) _engine.DurableConfirmation -= checkpoint;
            if (batchCheckpoint is not null) _engine.DurableBatchCompleted -= batchCheckpoint;
        }
    }

    private void NotifyTask(CsvTaskEvent evt)
    {
        if (TaskEvent is not { } observers) return;
        foreach (Action<CsvTaskEvent> observer in observers.GetInvocationList())
            try { observer(evt); }
            catch (Exception ex) { _log.LogError(ex, "CSV task observer failed"); }
    }

    private static void WriteCheckpoint(string path, IEnumerable<string> lines)
    {
        var temporary = path + ".p2l.tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            foreach (var line in lines) writer.WriteLine(line);
            writer.Flush();
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    // ─── CSV helpers ──────────────────────────────────────────────────────

    private static List<CsvPickRow> ReadAllRows(string csvPath, LightColor defaultColor)
    {
        var result = new List<CsvPickRow>();
        if (!File.Exists(csvPath)) return result;

        var lines = File.ReadAllLines(csvPath);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;
            if (line.StartsWith('#'))       continue;

            var tokens = line.Split(',');
            if (tokens.Length < 2) continue;

            var address = tokens[0].Trim().PadLeft(4, '0');

            // Skip header row
            if (address.Equals("addr",    StringComparison.OrdinalIgnoreCase) ||
                address.Equals("address", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(tokens[1].Trim(), out int qty) || qty <= 0) continue;

            var color = defaultColor;
            if (tokens.Length >= 3 && !string.IsNullOrWhiteSpace(tokens[2]) &&
                Enum.TryParse<LightColor>(tokens[2].Trim(), ignoreCase: true, out var c))
                color = c;

            var label = tokens.Length >= 4 ? tokens[3].Trim() : null;

            result.Add(new CsvPickRow
            {
                SourceLine = i + 1,
                Address    = address,
                Quantity   = qty,
                Color      = color,
                Label      = string.IsNullOrEmpty(label) ? null : label,
            });
        }

        return result;
    }

    // ─── Disposal ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            if (_loop is not null)
                try { await _loop; } catch (OperationCanceledException) { }
            _cts.Dispose();
        }
    }
}

/// <summary>Row data for one pick task from the CSV file.</summary>
public sealed class CsvPickRow
{
    public int        SourceLine { get; init; }   // 1-based line number in file
    public string     Address    { get; init; } = "";
    public int        Quantity   { get; init; }
    public LightColor Color      { get; init; }
    public string?    Label      { get; init; }
}

/// <summary>Event data fired by CsvPickTaskRunner for each lifecycle step.</summary>
public sealed class CsvTaskEvent
{
    public string      EventType { get; }
    public CsvPickRow? Row       { get; }

    public CsvTaskEvent(string eventType, CsvPickRow? row)
    {
        EventType = eventType;
        Row       = row;
    }
}
