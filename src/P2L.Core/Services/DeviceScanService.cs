using Microsoft.Extensions.Logging;
using P2L.Hardware;
using P2L.Hardware.Protocol;
using P2L.Shared.Models;

namespace P2L.Core.Services;

/// <summary>
/// Scans a range of module addresses by sending Az (status-inquiry) commands concurrently
/// and returns every module that responds with a non-Malfunction status.
///
/// Results are written into the <see cref="LightModuleRegistry"/> so the live dashboard
/// can display them immediately after the scan.
/// </summary>
public sealed class DeviceScanService
{
    private readonly AioiGateway         _gateway;
    private readonly LightModuleRegistry _registry;
    private readonly ILogger             _log;

    public DeviceScanService(AioiGateway         gateway,
                             LightModuleRegistry  registry,
                             ILogger<DeviceScanService> logger)
    {
        _gateway  = gateway;
        _registry = registry;
        _log      = logger;
    }

    /// <summary>
    /// Queries every module address in [<paramref name="startAddr"/>, <paramref name="endAddr"/>]
    /// and returns a sorted list of discovered modules.
    /// </summary>
    /// <param name="startAddr">First address to probe (1–9999).</param>
    /// <param name="endAddr">Last address to probe (inclusive).</param>
    /// <param name="maxConcurrency">
    ///   Maximum parallel Az queries in flight at once.
    ///   Keep ≤ 8 to avoid overwhelming the gateway's receive buffer.
    /// </param>
    /// <param name="progress">
    ///   Called after each address is probed with the running count of completed probes.
    /// </param>
    public async Task<IReadOnlyList<LightModuleState>> ScanAsync(
        int                startAddr       = 1,
        int                endAddr         = 99,
        int                maxConcurrency  = 4,
        IProgress<int>?    progress        = null,
        CancellationToken  ct              = default)
    {
        if (startAddr < 1 || endAddr < startAddr || endAddr > 9999)
            throw new ArgumentOutOfRangeException(
                nameof(endAddr), "Address range must be 1–9999 with start ≤ end.");

        var addresses = Enumerable
            .Range(startAddr, endAddr - startAddr + 1)
            .Select(n => n.ToString().PadLeft(4, '0'))
            .ToList();

        _log.LogInformation("Device scan started: {Start}–{End} ({Count} addresses, concurrency={C})",
            addresses[0], addresses[^1], addresses.Count, maxConcurrency);

        var found = new System.Collections.Concurrent.ConcurrentBag<LightModuleState>();
        int done  = 0;

        using var sem = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        var tasks = addresses.Select(async addr =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var response = await _gateway.QueryModuleStatusAsync(addr, ct)
                                             .ConfigureAwait(false);

                if (response.Kind == AioiDecoder.ResponseKind.ModuleStatus
                    && response.ModuleInfo is { IsPresent: true } info)
                {
                    var state = _registry.SetScanned(addr, info.Status);
                    found.Add(state);
                    _log.LogDebug("Found module {Addr}: {Status}", addr, info.Status);
                }
            }
            catch (TimeoutException)
            {
                // Expected for absent modules — not an error
                _log.LogDebug("No response from {Addr}", addr);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Error querying module {Addr}: {Msg}", addr, ex.Message);
            }
            finally
            {
                sem.Release();
                progress?.Report(Interlocked.Increment(ref done));
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var result = found.OrderBy(m => m.Address).ToList();
        _log.LogInformation("Device scan complete: {Found}/{Total} modules found",
            result.Count, addresses.Count);
        return result;
    }
}
