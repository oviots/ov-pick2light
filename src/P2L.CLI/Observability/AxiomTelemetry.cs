using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using P2L.Shared;

namespace P2L.CLI.Observability;

/// <summary>Bounded, best-effort export. Never performs network or disk I/O on picking threads.</summary>
internal sealed class AxiomTelemetry : ILoggerProvider, IP2LTelemetry, IAsyncDisposable
{
    private readonly AxiomSettings _settings;
    private readonly TelemetryRedactor _redactor;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<string> _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(1000)
        {FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false});
    private readonly Task _worker;
    private long _dropped;
    private int _disposed;
    private long _lastWarning;
    internal long Dropped => Interlocked.Read(ref _dropped);

    public AxiomTelemetry(AxiomSettings settings, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _redactor = new(settings.Token, settings.MqttPassword);
        _http = new(handler ?? new HttpClientHandler {AllowAutoRedirect = false}) {Timeout = TimeSpan.FromSeconds(5)};
        _worker = Task.Run(DrainAsync);
    }

    public ILogger CreateLogger(string categoryName) => new EntryLogger(this, categoryName);
    // LoggerFactory may dispose providers before the service has finished stopping.
    // The application explicitly awaits DisposeAsync after tearing down its services.
    public void Dispose() { }

    public void Record(string kind, string? topic, string payload)
    {
        try
        {
            JsonNode? data;
            if (Encoding.UTF8.GetByteCount(payload) > 131072)
                data = new JsonObject {["oversized"] = true, ["length"] = payload.Length};
            else
            {
                try { data = JsonNode.Parse(payload); }
                catch { data = new JsonObject {["invalidJson"] = true, ["length"] = payload.Length}; }
            }
            Enqueue(kind, topic, data);
        }
        catch { Interlocked.Increment(ref _dropped); }
    }

    private void Enqueue(string kind, string? topic, JsonNode? data)
    {
        var clean = _redactor.Clean(data);
        var entry = new JsonObject
        {
            ["_time"] = DateTime.UtcNow.ToString("O"), ["recordId"] = Guid.NewGuid().ToString("N"),
            ["service"] = "p2l", ["kind"] = kind, ["siteCode"] = _settings.SiteCode,
            ["clientId"] = _settings.ClientId, ["machine"] = Environment.MachineName,
            ["blockNo"] = _settings.BlockNo, ["topic"] = topic
        };
        if (clean is JsonObject obj)
            foreach (var key in new[] {"correlationId", "blockNo", "address", "type", "level", "category"})
                if (obj.TryGetPropertyValue(key, out var value)) entry[key] = value?.DeepClone();
        // Bound each queued record, including large device snapshots or malformed input.
        var serialized = clean?.ToJsonString() ?? "null";
        entry["data"] = Encoding.UTF8.GetByteCount(serialized) <= 16384 ? clean
            : new JsonObject {["truncated"] = true, ["preview"] = serialized[..Math.Min(serialized.Length, 2048)]};
        if (!_queue.Writer.TryWrite(entry.ToJsonString()))
        {
            Interlocked.Increment(ref _dropped);
            Warn("queue full; telemetry was dropped");
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token))
            {
                if (_queue.Reader.Count < 50 && !_queue.Reader.Completion.IsCompleted && Volatile.Read(ref _disposed) == 0)
                    await Task.Delay(1000, _stop.Token);
                var batch = new List<string>(50);
                while (batch.Count < 50 && _queue.Reader.TryRead(out var item)) batch.Add(item);
                await SendAsync(batch);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Warn("exporter stopped unexpectedly"); }
    }

    private async Task SendAsync(List<string> batch)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _settings.IngestUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.Token);
                request.Content = new StringContent("[" + string.Join(',', batch) + "]", Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request, _stop.Token);
                if (response.IsSuccessStatusCode)
                {
                    // Axiom can acknowledge a request while rejecting individual records.
                    var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(_stop.Token));
                    var failed = result?["failed"]?.GetValue<int>() ?? batch.Count;
                    if (failed > 0) { Interlocked.Add(ref _dropped, failed); Warn("ingest rejected records"); }
                    return;
                }
                if ((int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests && (int)response.StatusCode != 430) break;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { throw; }
            catch { /* Network/timeout failures remain isolated from command dispatch. */ }
            if (attempt < 2) await Task.Delay(TimeSpan.FromSeconds(attempt + 1), _stop.Token);
        }
        Interlocked.Add(ref _dropped, batch.Count);
        Warn("ingest failed; telemetry was dropped");
    }

    private void Warn(string message)
    {
        // Avoid ILogger recursion and never print tokens, payloads or response bodies.
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastWarning);
        if ((previous == 0 || now - previous > 60000) && Interlocked.CompareExchange(ref _lastWarning, now, previous) == previous)
            Console.Error.WriteLine("[Axiom] " + message);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { await _stop.CancelAsync(); await _worker; }
        finally { _http.Dispose(); _stop.Dispose(); }
    }

    private sealed class EntryLogger(AxiomTelemetry owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Trace and < LogLevel.None;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            try
            {
                var data = new JsonObject {["level"] = level.ToString(), ["category"] = category,
                    ["message"] = owner._redactor.Text(format(state, error)), ["eventId"] = id.Id};
                if (error is not null) data["exception"] = owner._redactor.Text(error.ToString());
                if (state is IEnumerable<KeyValuePair<string, object?>> fields)
                {
                    var properties = new JsonObject();
                    foreach (var field in fields)
                        if (field.Key != "{OriginalFormat}") properties[field.Key] = owner._redactor.IsSensitive(field.Key) ? "[REDACTED]" : owner._redactor.Text(field.Value?.ToString() ?? "");
                    data["properties"] = properties;
                }
                owner.Enqueue("service_log", null, data);
            }
            catch { Interlocked.Increment(ref owner._dropped); }
        }
    }
}
