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

    private async Task RunAsync(string csvPath, LightColor defaultColor,
                                CancellationToken ct)
    {
        _log.LogInformation("[CSV] Batch runner started: {File}", Path.GetFileName(csvPath));

        // Respect external pause before starting
        while (Volatile.Read(ref _paused) == 1 && !ct.IsCancellationRequested)
            await Task.Delay(100, ct);

        // Yield to any in-flight MQTT command (strict priority)
        await _gate.WaitIfPreemptedAsync(ct);

        // Read all pending rows
        var rows = ReadAllRows(csvPath, defaultColor);
        if (rows.Count == 0)
        {
            _log.LogInformation("[CSV] No tasks found.");
            TaskEvent?.Invoke(new CsvTaskEvent("csv_completed", null));
            _log.LogInformation("[CSV] Runner stopped.");
            return;
        }

        _log.LogInformation("[CSV] Lighting {Count} modules at once.", rows.Count);

        var instructions = rows
            .Select(r => new PickingInstruction
            {
                Address  = r.Address,
                Quantity = r.Quantity,
                Color    = r.Color,
                LedMode  = LightState.On,
            })
            .ToList();

        try
        {
            await _engine.StartBatchAsync(instructions, ct);
        }
        catch (OperationCanceledException) { _log.LogInformation("[CSV] Runner stopped."); return; }
        catch (Exception ex)
        {
            _log.LogError(ex, "[CSV] Failed to start batch.");
            TaskEvent?.Invoke(new CsvTaskEvent("task_error", null));
            _log.LogInformation("[CSV] Runner stopped.");
            return;
        }

        foreach (var row in rows)
        {
            _log.LogInformation("[CSV] Task lit: addr={A} qty={Q} color={C} label={L}",
                row.Address, row.Quantity, row.Color, row.Label);
            TaskEvent?.Invoke(new CsvTaskEvent("task_started", row));
        }

        // Track confirmations — keyed by padded address
        var addrToRow = rows.ToDictionary(r => r.Address);
        var confirmed = new List<CsvPickRow>();
        var batchDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnConfirmed(ConfirmationEvent evt, LightModuleState? _)
        {
            var addr = evt.Address.PadLeft(4, '0');
            if (!addrToRow.TryGetValue(addr, out var row)) return;

            confirmed.Add(row);
            _log.LogInformation("[CSV] Confirmed: addr={A}", addr);
            TaskEvent?.Invoke(new CsvTaskEvent("task_confirmed", row));
        }

        void OnBatchCompleted() => batchDone.TrySetResult();

        _engine.ItemConfirmed  += OnConfirmed;
        _engine.BatchCompleted += OnBatchCompleted;

        try
        {
            await batchDone.Task.WaitAsync(ct);
            await _engine.FinishBatchAsync(ct: ct);

            // Delete confirmed rows in reverse line order so earlier line numbers stay valid
            foreach (var row in confirmed.OrderByDescending(r => r.SourceLine))
                DeleteRow(csvPath, row.SourceLine);

            _log.LogInformation("[CSV] All tasks completed.");
            TaskEvent?.Invoke(new CsvTaskEvent("csv_completed", null));
        }
        catch (OperationCanceledException)
        {
            if (_engine.IsBatchActive)
                try { await _engine.FinishBatchAsync(ct: CancellationToken.None); } catch { }
        }
        finally
        {
            _engine.ItemConfirmed  -= OnConfirmed;
            _engine.BatchCompleted -= OnBatchCompleted;
        }

        _log.LogInformation("[CSV] Runner stopped.");
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

    private static void DeleteRow(string csvPath, int sourceLine)
    {
        try
        {
            var lines = File.ReadAllLines(csvPath).ToList();
            int idx   = sourceLine - 1;
            if (idx >= 0 && idx < lines.Count)
            {
                lines.RemoveAt(idx);
                File.WriteAllLines(csvPath, lines);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[CSV] Warning: could not delete row {sourceLine}: {ex.Message}");
        }
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
