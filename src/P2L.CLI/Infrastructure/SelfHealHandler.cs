using System.Diagnostics;
using System.Text.Json;
using P2L.CLI.Configuration;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// Installs unhandled-exception crash handlers that automatically restart the process.
///
/// Crash-loop detection: crash timestamps are persisted to a local JSONL file.
/// If more than <see cref="SelfHealSettings.MaxRestarts"/> crashes occur within
/// <see cref="SelfHealSettings.RestartWindowMinutes"/>, self-restart is disabled
/// to prevent runaway crash loops.
///
/// Watchdog integration: call <see cref="TriggerWatchdog"/> when a component hang
/// is detected externally (e.g. from WatchdogService). Restarts the process using
/// the same crash-loop guard.
/// </summary>
internal static class SelfHealHandler
{
    private const string CrashHistoryFileName = "crash_history.jsonl";

    private static SelfHealSettings _settings  = new();
    private static string[]         _launchArgs = [];
    private static string           _crashHistoryFile = CrashHistoryFileName;

    // ─── Public API ───────────────────────────────────────────────────────

    /// <param name="basePath">
    /// Optional absolute directory for the crash-history JSONL file.
    /// Defaults to the process working directory when omitted.
    /// Pass <c>ProgramData\P2L</c> when running as a Windows service so the
    /// file is written to a writable location regardless of working directory.
    /// </param>
    public static void Install(SelfHealSettings settings, string[] launchArgs, string basePath = "")
    {
        _settings   = settings;
        _launchArgs = launchArgs;
        _crashHistoryFile = string.IsNullOrEmpty(basePath)
            ? CrashHistoryFileName
            : Path.Combine(basePath, CrashHistoryFileName);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            WriteCrashLog($"[Crash] {e.ExceptionObject}");
            if (e.IsTerminating)
                RestartIfAllowed();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Process is still alive — log but don't restart
            e.SetObserved();
            WriteCrashLog($"[UnobservedTask] {e.Exception}");
        };
    }

    /// <summary>
    /// Called by an external watchdog (e.g. WatchdogService) when a component hang
    /// is detected. Logs the reason and performs a self-healing restart if allowed.
    /// </summary>
    public static void TriggerWatchdog(string reason)
    {
        WriteCrashLog($"[Watchdog] {reason}");
        RestartIfAllowed();
    }

    // ─── Internals ────────────────────────────────────────────────────────

    private static void RestartIfAllowed()
    {
        if (!_settings.Enabled) return;

        if (IsInCrashLoop())
        {
            WriteCrashLog("[SelfHeal] Crash-loop limit reached — NOT restarting");
            Environment.Exit(1);
            return;
        }

        Thread.Sleep(_settings.BackOffSeconds * 1_000);
        Restart();
    }

    private static bool IsInCrashLoop()
    {
        try
        {
            if (!File.Exists(_crashHistoryFile)) return false;

            var cutoff = DateTime.UtcNow.AddMinutes(-_settings.RestartWindowMinutes);
            int count  = 0;

            foreach (var line in File.ReadLines(_crashHistoryFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("at", out var atEl)
                        && atEl.TryGetDateTime(out var at)
                        && at >= cutoff)
                        count++;
                }
                catch { /* skip malformed lines */ }
            }

            return count >= _settings.MaxRestarts;
        }
        catch { return false; }
    }

    private static void WriteCrashLog(string message)
    {
        try
        {
            // Build JSONL manually — avoids anonymous-type reflection (AOT-safe)
            var at      = DateTime.UtcNow.ToString("O");
            var escaped = message.Replace("\\", "\\\\").Replace("\"", "\\\"")
                                 .Replace("\r", "\\r").Replace("\n", "\\n")
                                 .Replace("\t", "\\t");
            File.AppendAllText(_crashHistoryFile, $"{{\"at\":\"{at}\",\"msg\":\"{escaped}\"}}\n");
        }
        catch { /* best-effort */ }
    }

    private static void Restart()
    {
        try
        {
            var exe = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrEmpty(exe))
            {
                Environment.Exit(99);
                return;
            }

            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            foreach (var arg in _launchArgs)
                psi.ArgumentList.Add(arg);

            Process.Start(psi);
        }
        catch { /* best-effort — at worst the OS supervisor will restart */ }
        finally
        {
            Environment.Exit(99);
        }
    }
}
