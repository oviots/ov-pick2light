using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using P2L.CLI.Configuration;

namespace P2L.CLI.Infrastructure;

/// <summary>Bounded, asynchronous Axiom export. Never waits for HTTP in ILogger.Log.</summary>
public sealed class AxiomLoggerProvider : ILoggerProvider, IAsyncDisposable
{
    private const int MaxEventBytes = 16 * 1024;
    private const int MaxBatchBytes = 256 * 1024;
    private readonly AxiomLoggingOptions _options;
    private readonly Guid _runId = Guid.NewGuid();
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _endpoint;
    private readonly string[] _secrets;
    private readonly Channel<byte[]> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private int _disposed;
    private long _sent, _dropped, _lastWarning;
    public long SentCount => Interlocked.Read(ref _sent);
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public AxiomLoggerProvider(AxiomLoggingOptions options, HttpClient? httpClient = null,
        IEnumerable<string>? secrets = null)
    {
        if (!options.IsValid()) throw new ArgumentException("Invalid Axiom logging configuration.", nameof(options));
        _options = options;
        _endpoint = new UriBuilder(options.Endpoint) { Query = "timestamp-field=_time" }.Uri;
        _secrets = (secrets ?? []).Append(options.ApiToken).Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal).OrderByDescending(s => s.Length).ToArray();
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromMilliseconds(options.RequestTimeoutMs),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        // Wait mode + TryWrite drops the incoming record without blocking or losing count.
        _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.QueueCapacity)
        { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, AllowSynchronousContinuations = false });
        _worker = Task.Run(RunAsync);
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);
    private bool Enabled(LogLevel level) => Volatile.Read(ref _disposed) == 0
        && level != LogLevel.None && level >= _options.MinimumLevel;

    private sealed class Logger(AxiomLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => owner.Enabled(level);
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            try
            {
                var record = owner.Encode(level, category, eventId, state, exception, formatter);
                if (record.Length > MaxEventBytes || !owner._queue.Writer.TryWrite(record))
                    Interlocked.Increment(ref owner._dropped);
            }
            catch { Interlocked.Increment(ref owner._dropped); } // Logging cannot fail the pick path.
        }
    }

    private byte[] Encode<TState>(LogLevel level, string category, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var fields = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.Take(32).ToArray() : [];
        var sensitiveValues = fields.Where(p => Sensitive(p.Key) && p.Value is string { Length: > 0 })
            .Select(p => (string)p.Value!).ToArray();
        var secrets = _secrets.Concat(sensitiveValues).Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length).ToArray();
        string Clean(string text, int limit = 4096)
        {
            foreach (var secret in secrets)
                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            return text.Length > limit ? text[..limit] + "…" : text;
        }
        using var bytes = new MemoryStream();
        using (var json = new Utf8JsonWriter(bytes))
        {
            json.WriteStartObject();
            json.WriteString("_time", DateTimeOffset.UtcNow);
            json.WriteString("event_id", Guid.NewGuid());
            json.WriteString("level", level.ToString());
            json.WriteString("category", Clean(category, 256));
            json.WriteNumber("event_code", eventId.Id);
            json.WriteString("service", Clean(_options.Service, 128));
            json.WriteString("environment", Clean(_options.Environment, 128));
            json.WriteString("instance", Clean(_options.InstanceName, 128));
            json.WriteString("client_id", Clean(_options.ClientId, 256));
            json.WriteString("run_id", _runId);
            json.WriteNumber("process_id", System.Environment.ProcessId);
            json.WriteString("message", Clean(formatter(state, exception)));
            if (exception is not null) json.WriteString("exception", Clean(exception.ToString(), 8192));
            json.WriteStartObject("properties");
            foreach (var (key, value) in fields)
            {
                if (key == "{OriginalFormat}") continue;
                var name = Clean(key, 128);
                if (Sensitive(key)) { json.WriteString(name, "[REDACTED]"); continue; }
                // Keep scalars queryable, without reflection over arbitrary application objects.
                switch (value)
                {
                    case null: json.WriteNull(name); break;
                    case bool b: json.WriteBoolean(name, b); break;
                    case int n: json.WriteNumber(name, n); break;
                    case long n: json.WriteNumber(name, n); break;
                    case double n when double.IsFinite(n): json.WriteNumber(name, n); break;
                    case decimal n: json.WriteNumber(name, n); break;
                    case string s: json.WriteString(name, Clean(s, 1024)); break;
                    case DateTime dt: json.WriteString(name, dt); break;
                    case DateTimeOffset dt: json.WriteString(name, dt); break;
                    case Guid id: json.WriteString(name, id); break;
                    default: json.WriteString(name, "[non-scalar]"); break;
                }
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return bytes.ToArray();
    }

    private static bool Sensitive(string key) => new[] { "password", "token", "secret", "authorization", "credential", "apikey", "api_key" }
        .Any(term => key.Contains(term, StringComparison.OrdinalIgnoreCase));

    private async Task RunAsync()
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(_stop.Token).ConfigureAwait(false))
            {
                var batch = new List<byte[]>(_options.BatchSize);
                var size = 0;
                using var flush = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                flush.CancelAfter(_options.FlushIntervalMs);
                while (batch.Count < _options.BatchSize)
                {
                    while (batch.Count < _options.BatchSize && _queue.Reader.TryPeek(out var next)
                        && size + next.Length + 1 <= MaxBatchBytes && _queue.Reader.TryRead(out next))
                    { batch.Add(next); size += next.Length + 1; }
                    if (batch.Count >= _options.BatchSize || _queue.Reader.TryPeek(out _)) break;
                    try { if (!await _queue.Reader.WaitToReadAsync(flush.Token).ConfigureAwait(false)) break; }
                    catch (OperationCanceledException) { break; }
                }
                if (batch.Count == 0) continue;
                try { await SendAsync(batch, size, _stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                { Interlocked.Add(ref _dropped, batch.Count); break; }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception) { Warn("export worker stopped; local logging continues"); }
        finally
        {
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out _)) Interlocked.Increment(ref _dropped);
            if (DroppedCount > 0) Warn("cloud records dropped; local audit remains available", force: true);
        }
    }

    private async Task SendAsync(List<byte[]> batch, int size, CancellationToken ct)
    {
        using var payload = new MemoryStream(size);
        foreach (var entry in batch) { payload.Write(entry); payload.WriteByte((byte)'\n'); }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_options.RequestTimeoutMs);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiToken);
            request.Content = new ByteArrayContent(payload.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-ndjson");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .WaitAsync(timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                // A 200 can include per-event failures. No failed/unknown batch is ever retried.
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
                var body = new byte[16 * 1024 + 1];
                var used = 0;
                while (used < body.Length)
                {
                    var read = await stream.ReadAsync(body.AsMemory(used), timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    used += read;
                }
                if (used == body.Length) throw new JsonException("Oversize ingest receipt.");
                using var receipt = JsonDocument.Parse(body.AsMemory(0, used));
                var ingested = receipt.RootElement.GetProperty("ingested").GetInt32();
                var failed = receipt.RootElement.GetProperty("failed").GetInt32();
                if (ingested < 0 || failed < 0 || (long)ingested + failed != batch.Count)
                    throw new JsonException("Invalid ingest counts.");
                Interlocked.Add(ref _sent, ingested);
                Interlocked.Add(ref _dropped, failed);
                if (failed > 0) Warn("some cloud records were dropped");
                return;
            }
            Warn($"HTTP {(int)response.StatusCode}; batch dropped");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { Warn("request failed, timed out or returned an invalid receipt; batch dropped"); }
        Interlocked.Add(ref _dropped, batch.Count);
        // Prevent a fast-failing endpoint from causing a request storm. This worker never blocks picking.
        try { await Task.Delay(Math.Max(1000, _options.FlushIntervalMs), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { } // Batch already counted.
    }

    private void Warn(string reason, bool force = false)
    {
        var now = System.Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastWarning);
        if (!force && previous != 0 && now - previous < 30000) return;
        if (Interlocked.CompareExchange(ref _lastWarning, now, previous) != previous) return;
        try { Console.Error.WriteLine($"P2L Axiom: {reason}; sent={SentCount}, dropped={DroppedCount}."); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock) return new ValueTask(_disposeTask ??= StopAsync());
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    private async Task StopAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _queue.Writer.TryComplete();
        try { await _worker.WaitAsync(TimeSpan.FromMilliseconds(_options.ShutdownTimeoutMs)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _worker.ConfigureAwait(false); // Every worker wait/HTTP operation is cancellation-bounded.
        }
        finally { if (_ownsHttp) _http.Dispose(); _stop.Dispose(); }
    }
}

public static class AxiomLoggingExtensions
{
    public static ILoggingBuilder AddAxiom(this ILoggingBuilder builder, IConfiguration config,
        IEnumerable<string>? secrets = null)
    {
        var options = AxiomLoggingOptions.FromConfiguration(config);
        if (!options.Enabled) return builder;
        if (!options.IsValid())
        {
            try { Console.Error.WriteLine("P2L Axiom logging disabled: configure a valid HTTPS ingest endpoint and P2L_Axiom__ApiToken."); } catch { }
            return builder;
        }
        builder.Services.AddSingleton<ILoggerProvider>(_ => new AxiomLoggerProvider(options, secrets: secrets));
        // Use the same category filters as local logging; the provider applies its own minimum too.
        return builder;
    }
}
