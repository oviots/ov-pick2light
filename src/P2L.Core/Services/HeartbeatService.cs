using Microsoft.Extensions.Logging;
using P2L.Hardware;
using P2L.Shared.Constants;

namespace P2L.Core.Services;

/// <summary>
/// Sends periodic keep-alive messages to the gateway to prevent TCP timeout during idle periods.
/// Uses Az0000 (error display module status inquiry) — a harmless read-only operation.
/// </summary>
public sealed class HeartbeatService : IAsyncDisposable, IDisposable
{
    private readonly AioiGateway _gateway;
    private readonly ILogger     _log;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _consecutiveFailures;
    private const int ReconnectAfterFailures = 3;

    public HeartbeatService(AioiGateway gateway, ILogger<HeartbeatService> logger)
    {
        _gateway = gateway;
        _log     = logger;
    }

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log.LogInformation("Heartbeat service started (interval: {Ms} ms)",
            AioiConstants.HeartbeatIntervalMs);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(AioiConstants.HeartbeatIntervalMs, ct);
            if (!_gateway.IsConnected) continue;
            try
            {
                await _gateway.QueryModuleStatusAsync(AioiConstants.ErrorDisplayAddress, ct);
                _consecutiveFailures = 0;
                _log.LogDebug("Heartbeat OK");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Heartbeat failed: {Msg}", ex.Message);
                OnProbeFailed();
            }
        }
    }

    private void OnProbeFailed()
    {
        if (++_consecutiveFailures < ReconnectAfterFailures) return;
        _consecutiveFailures = 0;
        _log.LogWarning("Gateway protocol did not respond to {Count} heartbeats; reconnecting TCP without resetting lights",
            ReconnectAfterFailures);
        _gateway.RequestReconnect();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; } catch (OperationCanceledException) { }
        }
        _cts.Dispose();
    }

    /// <summary>Synchronous wrapper for DI containers that call IDisposable.Dispose.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
