using Microsoft.Extensions.Logging;
using P2L.Hardware;

namespace P2L.Core.Services;

/// <summary>
/// Monitors gateway connectivity and triggers a self-healing restart when the gateway
/// remains disconnected for longer than <see cref="TimeoutMinutes"/>.
///
/// This catches "stuck process" scenarios where the process has not crashed but is
/// no longer functional (e.g. persistent network partition, hardware fault).
///
/// Lifecycle: Call <see cref="Start"/> once after the gateway has been started.
/// The watchdog waits for the first successful connection before beginning the
/// countdown, so a gateway that never connects does NOT trigger a restart.
/// </summary>
public sealed class WatchdogService : IAsyncDisposable
{
    private readonly AioiGateway _gateway;
    private readonly ILogger     _log;
    private readonly int         _timeoutMinutes;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <summary>Raised when a restart is needed due to persistent disconnection.</summary>
    public event Action<string>? RestartRequested;

    public WatchdogService(AioiGateway gateway, ILogger<WatchdogService> log,
                           int timeoutMinutes = 5)
    {
        _gateway        = gateway;
        _log            = log;
        _timeoutMinutes = timeoutMinutes;
    }

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log.LogInformation("[Watchdog] Started (disconnect timeout: {Min} min)", _timeoutMinutes);
    }

    // ─── Background loop ──────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        // Phase 1 — wait for the first successful connection
        while (!_gateway.IsConnected && !ct.IsCancellationRequested)
            await Task.Delay(2_000, ct);

        if (ct.IsCancellationRequested) return;

        _log.LogDebug("[Watchdog] First connection observed — monitoring active");

        // Phase 2 — monitor for persistent disconnection
        DateTime? disconnectedSince = null;

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5_000, ct);

            if (_gateway.IsConnected)
            {
                if (disconnectedSince.HasValue)
                {
                    _log.LogInformation("[Watchdog] Gateway reconnected — countdown reset");
                    disconnectedSince = null;
                }
                continue;
            }

            // Gateway is disconnected
            if (!disconnectedSince.HasValue)
            {
                disconnectedSince = DateTime.UtcNow;
                _log.LogWarning("[Watchdog] Gateway disconnected — {Min}-min restart countdown started",
                    _timeoutMinutes);
            }
            else if ((DateTime.UtcNow - disconnectedSince.Value).TotalMinutes >= _timeoutMinutes)
            {
                var reason = $"Gateway disconnected for over {_timeoutMinutes} minutes";
                _log.LogError("[Watchdog] {Reason} — requesting restart", reason);
                RestartRequested?.Invoke(reason);
                return; // prevent duplicate invocations
            }
        }
    }

    // ─── Disposal ─────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
        _cts.Dispose();
    }
}
