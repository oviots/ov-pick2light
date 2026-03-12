using Microsoft.Extensions.Logging;
using P2L.Shared.Enums;
using P2L.Shared.Models;

namespace P2L.Core.Services;

/// <summary>
/// Processes a CSV pick-task file sequentially:
///   1. Read next pending row.
///   2. Wait if MQTT commands are in-flight (PreemptionGate).
///   3. Light the module via PickingEngine.
///   4. Wait for operator confirmation on that specific address.
///   5. Delete the confirmed row from the CSV (auto-delete).
///   6. Fire TaskEvent for MQTT logging.
///   Repeat until no rows remain.
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
    public void Start(string csvPath, LightColor defaultColor, string blockNo = "00")
    {
        if (IsRunning)
            throw new InvalidOperationException("CSV runner is already active. Cancel it first.");

        _cts  = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(csvPath, defaultColor, blockNo, _cts.Token));
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
                                string blockNo, CancellationToken ct)
    {
        _log.LogInformation("[CSV] Runner started: {File}", Path.GetFileName(csvPath));

        while (!ct.IsCancellationRequested)
        {
            // Respect external pause (e.g. operator needs a break)
            while (Volatile.Read(ref _paused) == 1 && !ct.IsCancellationRequested)
                await Task.Delay(100, ct);

            // Yield to any in-flight MQTT command (strict priority)
            await _gate.WaitIfPreemptedAsync(ct);

            var row = ReadNextRow(csvPath, defaultColor);
            if (row is null)
            {
                _log.LogInformation("[CSV] All tasks completed.");
                TaskEvent?.Invoke(new CsvTaskEvent("csv_completed", null));
                break;
            }

            _log.LogInformation("[CSV] Task start: addr={A} qty={Q} color={C} label={L}",
                row.Address, row.Quantity, row.Color, row.Label);

            try
            {
                // Use PP1 batch command (InstructBatchAsync) for each item so that
                // the gateway processes it as a block instruction, enabling proper
                // batch tracking and the D-command termination sequence.
                var instruction = new PickingInstruction
                {
                    Address  = row.Address,
                    Quantity = row.Quantity,
                    Color    = row.Color,
                    LedMode  = LightState.On,
                    BlockNo  = blockNo,
                };
                await _engine.StartBatchAsync([instruction], ct);
                TaskEvent?.Invoke(new CsvTaskEvent("task_started", row));

                // Wait for the operator to confirm THIS module
                var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnConfirmed(ConfirmationEvent evt, LightModuleState? _)
                {
                    if (evt.Address.PadLeft(4, '0') == row.Address)
                        tcs.TrySetResult();
                }

                _engine.ItemConfirmed += OnConfirmed;
                try   { await tcs.Task.WaitAsync(ct); }
                finally { _engine.ItemConfirmed -= OnConfirmed; }

                // Send D command (TerminateBlock) to turn off lights and reset batch state
                await _engine.FinishBatchAsync(blockNo, ct);

                // Auto-delete confirmed row from CSV
                DeleteRow(csvPath, row.SourceLine);

                _log.LogInformation("[CSV] Task confirmed: addr={A}", row.Address);
                TaskEvent?.Invoke(new CsvTaskEvent("task_confirmed", row));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[CSV] Error on addr={A}", row.Address);
                TaskEvent?.Invoke(new CsvTaskEvent("task_error", row));
                // Ensure batch state is clean before next iteration
                if (_engine.IsBatchActive)
                    try { await _engine.FinishBatchAsync(blockNo, ct); } catch { }
            }
        }

        _log.LogInformation("[CSV] Runner stopped.");
    }

    // ─── CSV helpers ──────────────────────────────────────────────────────

    private static CsvPickRow? ReadNextRow(string csvPath, LightColor defaultColor)
    {
        if (!File.Exists(csvPath)) return null;

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
            if (address.Equals("addr", StringComparison.OrdinalIgnoreCase) ||
                address.Equals("address", StringComparison.OrdinalIgnoreCase)) continue;

            if (!int.TryParse(tokens[1].Trim(), out int qty) || qty <= 0) continue;

            var color = defaultColor;
            if (tokens.Length >= 3 && !string.IsNullOrWhiteSpace(tokens[2]) &&
                Enum.TryParse<LightColor>(tokens[2].Trim(), ignoreCase: true, out var c))
                color = c;

            var label = tokens.Length >= 4 ? tokens[3].Trim() : null;

            return new CsvPickRow
            {
                SourceLine = i + 1,
                Address    = address,
                Quantity   = qty,
                Color      = color,
                Label      = string.IsNullOrEmpty(label) ? null : label,
            };
        }

        return null;
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
            // Log but don't crash — row will be re-processed on next run if file write fails
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
