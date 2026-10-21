using Microsoft.Extensions.Logging;
using P2L.Hardware;

namespace P2L.Core.Services;

/// <summary>Low-priority active status observations; never turns an observation into picking.</summary>
public sealed class MissingConfirmationMonitor : IAsyncDisposable
{
    private readonly PickingEngine _engine;
    private readonly AioiGateway _gateway;
    private readonly ILogger<MissingConfirmationMonitor> _log;
    private readonly int _intervalMs, _maxItems, _minimumAgeMs, _probeTimeoutMs, _mismatchGraceMs;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Task? _loop;
    private int _started;
    private int _disposed;

    public MissingConfirmationMonitor(PickingEngine engine, AioiGateway gateway,
        ILogger<MissingConfirmationMonitor> log, int intervalMs = 2000, int maxItems = 4,
        int minimumAgeMs = 15000, int probeTimeoutMs = 1500, int mismatchGraceMs = 10000)
    {
        if (intervalMs < 100 || maxItems is < 1 or > 16 || minimumAgeMs < 0
            || probeTimeoutMs is < 1 or > 5000 || mismatchGraceMs < 0)
            throw new ArgumentOutOfRangeException(nameof(intervalMs));
        _engine = engine;
        _gateway = gateway;
        _log = log;
        _intervalMs = intervalMs;
        _maxItems = maxItems;
        _minimumAgeMs = minimumAgeMs;
        _probeTimeoutMs = probeTimeoutMs;
        _mismatchGraceMs = mismatchGraceMs;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _gateway.ConnectionChanged += OnConnectionChanged;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private void OnConnectionChanged(bool connected)
    {
        if (!connected || Volatile.Read(ref _disposed) != 0) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_gateway.IsHealthy)
                {
                    try
                    {
                        await _engine.ProbeActiveConfirmationsAsync(_maxItems, _minimumAgeMs,
                            _probeTimeoutMs, _mismatchGraceMs, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(ex, "Missing-confirmation observation cycle failed");
                    }
                }
                await _wake.WaitAsync(_intervalMs, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _gateway.ConnectionChanged -= OnConnectionChanged;
        await _cts.CancelAsync();
        if (_loop is not null) await _loop;
        _cts.Dispose();
        _wake.Dispose();
    }
}
