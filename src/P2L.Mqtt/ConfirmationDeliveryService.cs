using Microsoft.Extensions.Logging;
using P2L.Mqtt.Configuration;
using MQTTnet;
using MQTTnet.Protocol;

namespace P2L.Mqtt;

/// <summary>Publishes durable receipts promptly; transport success never deletes them.</summary>
internal sealed class ConfirmationDeliveryService(
    ConfirmationOutbox outbox, IMqttClient client, string topic, ILogger<ConfirmationDeliveryService> log, ConfirmApiSettings? api = null) : IAsyncDisposable
{
    private readonly ConfirmApiClient? _api = api?.Enabled == true ? new ConfirmApiClient(api) : null;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _disposed;

    public void Start()
    {
        outbox.Recorded += Wake;
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public void Wake()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { if (_wake.CurrentCount == 0) _wake.Release(); }
        catch (SemaphoreFullException) { } // Concurrent records coalesce into one wake-up.
        catch (ObjectDisposedException) { } // An in-flight Record can race with shutdown.
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_api is not null || client.IsConnected)
                {
                    var batchSize = _api is null ? 100 : api!.BatchSize;
                    var receipts = outbox.ClaimForDelivery(batchSize);
                    if (_api is not null)
                    {
                        if (receipts.Count > 0)
                        {
                            var stored = await _api.SendAsync(receipts, ct);
                            outbox.AcknowledgeStored(stored);
                            if (stored.Count < receipts.Count)
                                log.LogWarning("[Confirm API] {Count} receipts remain in SQLite awaiting acceptance", receipts.Count - stored.Count);
                        }
                    }
                    else
                    {
                        foreach (var receipt in receipts)
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            timeout.CancelAfter(TimeSpan.FromSeconds(10));
                            await client.PublishAsync(new MqttApplicationMessageBuilder()
                                .WithTopic(topic).WithPayload(receipt.Payload)
                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce).Build(), timeout.Token);
                        }
                    }
                    if (receipts.Count == batchSize) continue; // Drain a backlog without a delay between batches.
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning("[Confirm] Synchronization deferred: {Reason}", ex.Message); }
            // Record and reconnect wake immediately. The timer only handles retries/recovery.
            await _wake.WaitAsync(TimeSpan.FromMilliseconds(_api is null ? 5000 : api!.PollIntervalMs), ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        outbox.Recorded -= Wake;
        await _stop.CancelAsync();
        if (_loop is not null)
            try { await _loop; } catch (OperationCanceledException) { }
        _api?.Dispose();
        _wake.Dispose();
        _stop.Dispose();
    }
}
