#if WINDOWS_SERVICE
using System.Runtime.Versioning;
using System.ServiceProcess;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// Minimal Windows Service Control Manager (SCM) adapter.
/// Wraps the Spectre.Console CLI runner so SCM start/stop events are honoured.
/// Only compiled when PublishAot targets win-x64 / win-arm64.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class P2LWindowsService : ServiceBase
{
    private readonly Func<Task<int>> _run;
    private readonly CancellationTokenSource _cts;
    private Task<int>? _runTask;

    /// <param name="run">Delegate that invokes <c>app.RunAsync(args)</c>.</param>
    /// <param name="cts">Shared CTS registered in DI; cancelled on Stop to signal commands.</param>
    public P2LWindowsService(Func<Task<int>> run, CancellationTokenSource cts)
    {
        ServiceName = "P2L";
        CanStop = true;
        CanHandleSessionChangeEvent = false;
        AutoLog = false; // P2L has its own rotating-file logger
        _run = run;
        _cts = cts;
    }

    protected override void OnStart(string[] args)
    {
        // Run on the thread-pool; OnStart must return quickly to SCM.
        _runTask = Task.Run(_run);
    }

    protected override void OnStop()
    {
        // Signal long-running commands (e.g. RunPickCommand) to stop cleanly.
        _cts.Cancel();
        try
        {
            // Give the CLI up to 15 s to drain hardware TX queue and close TCP.
            _runTask?.Wait(TimeSpan.FromSeconds(15));
        }
        catch (AggregateException) { /* task cancelled — expected */ }
    }
}
#endif
