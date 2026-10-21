using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using P2L.Mqtt.Configuration;

namespace P2L.Mqtt.Transport;

/// <summary>Warm links with one delivery path and a shared application ACK boundary.</summary>
internal sealed class BridgeTransportCoordinator : IAsyncDisposable
{
    private sealed class State(IBridgeChannel channel)
    {
        public readonly IBridgeChannel Channel = channel;
        public bool Ready;
        public DateTime HealthySince;
        public DateTime NextProbe;
        public DateTime LastFailureWarning;
        public string? ProbeId;
        public DateTime ProbeDeadline;
        public CancellationTokenSource Fault = new();
    }

    private readonly State[] _states;
    private readonly MqttSettings _settings;
    private readonly ILogger _log;
    private readonly object _sync = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _eventAcks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _probes = new();
    private readonly List<(State State, Func<ChannelMessage, Task> Handler, Action<bool> Connection)> _handlers = [];
    private readonly bool _applicationAck;
    private Task? _healthLoop;
    private State? _active;
    private int _started;
    private int _disposed;

    public event Func<ChannelMessage, Task>? CommandReceived;
    public bool RequiresApplicationAck => _applicationAck;
    public bool IsConnected { get { lock (_sync) return _states.Any(s => s.Ready && s.Channel.IsConnected); } }
    public string? ActiveChannel { get { lock (_sync) return _active?.Channel.Name; } }

    public BridgeTransportCoordinator(IReadOnlyList<IBridgeChannel> channels, MqttSettings settings, ILogger log)
    {
        if (channels.Count == 0) throw new ArgumentException("At least one bridge transport is required.");
        if (settings.ProbeIntervalMs < 50 || settings.ProbeTimeoutMs < 100 || settings.FailbackStableMs < 0)
            throw new ArgumentException("Invalid bridge transport health timing.");
        _states = channels.Select(c => new State(c)).ToArray();
        _settings = settings;
        _log = log;
        _applicationAck = settings.RequireApplicationAck || channels.Count > 1 || settings.BackupBroker?.Enabled == true;
        foreach (var state in _states)
        {
            Func<ChannelMessage, Task> handler = msg => HandleMessageAsync(state, msg);
            Action<bool> connection = connected => ConnectionChanged(state, connected);
            state.Channel.MessageReceived += handler;
            state.Channel.ConnectionChanged += connection;
            _handlers.Add((state, handler, connection));
            if (!_applicationAck && state.Channel.IsConnected) state.Ready = true;
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        await Task.WhenAll(_states.Select(s => s.Channel.StartAsync(ct)));
        if (_applicationAck) _healthLoop = Task.Run(() => HealthLoopAsync(_cts.Token));
    }

    private void ConnectionChanged(State state, bool connected)
    {
        lock (_sync)
        {
            if (!connected) { state.ProbeId = null; MarkFault(state); }
            else
            {
                state.NextProbe = DateTime.MinValue;
                if (!_applicationAck) MarkReady(state);
            }
        }
    }

    private async Task HandleMessageAsync(State state, ChannelMessage message)
    {
        if (message.Topic != _settings.CommandTopic || message.Retain) return;
        JsonDocument? document = null;
        try
        {
            try { document = JsonDocument.Parse(message.Payload); }
            catch (JsonException) { /* Existing dispatcher owns malformed command outcomes. */ }
            if (document?.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("type", out var typeValue)
                && typeValue.ValueKind == JsonValueKind.String)
            {
                var type = typeValue.GetString();
                if (type is "transport_ping" or "transport_pong" or "event_ack")
                {
                    var property = type == "event_ack" ? "eventId" : "probeId";
                    if (!document.RootElement.TryGetProperty(property, out var value)
                        || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        _log.LogWarning("[P2L] Malformed transport control ignored: {Type}", type);
                        return;
                    }
                    var id = value.GetString()!;
                    if (type == "transport_pong")
                    {
                        lock (_sync)
                            if (state.ProbeId == id && state.ProbeDeadline >= DateTime.UtcNow)
                            {
                                state.ProbeId = null;
                                MarkReady(state);
                            }
                    }
                    else if (type == "transport_ping")
                        await state.Channel.SendAsync(_settings.EventTopic, Control("transport_pong", "probeId", id), false, _cts.Token);
                    else if (_eventAcks.TryGetValue(id, out var ack)) ack.TrySetResult();
                    return;
                }
            }
        }
        finally { document?.Dispose(); }
        var handlers = CommandReceived;
        if (handlers is not null)
            foreach (Func<ChannelMessage, Task> handler in handlers.GetInvocationList()) await handler(message);
    }

    public async Task SendAsync(string topic, byte[] payload, bool retain, CancellationToken ct)
    {
        string? eventId = null;
        if (_applicationAck && !retain)
        {
            using var doc = JsonDocument.Parse(payload);
            eventId = doc.RootElement.GetProperty("eventId").GetString()
                      ?? throw new InvalidOperationException("Business event requires eventId.");
        }
        var ack = eventId is null ? null : _eventAcks.GetOrAdd(eventId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        var tried = new HashSet<State>();
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (ack?.Task.IsCompletedSuccessfully == true) return;
                State? selected;
                CancellationToken fault;
                lock (_sync)
                {
                    selected = Select(tried);
                    if (selected is null) throw new IOException("No healthy bridge transport is available.");
                    fault = selected.Fault.Token;
                    if (_active != selected)
                    {
                        _active = selected;
                        _log.LogInformation("[P2L] Active transport → {Transport}", selected.Channel.Name);
                    }
                }
                tried.Add(selected);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, fault);
                if (_applicationAck) attempt.CancelAfter(_settings.ProbeTimeoutMs);
                try
                {
                    await selected.Channel.SendAsync(topic, payload, retain, attempt.Token).WaitAsync(attempt.Token);
                    if (ack is not null) await ack.Task.WaitAsync(attempt.Token);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    // A retained topic can be denied while the command/event route
                    // remains healthy. Optional telemetry must not fence business I/O.
                    lock (_sync) if (!retain || !selected.Channel.IsConnected) MarkFault(selected);
                    lock (_sync)
                        if (DateTime.UtcNow - selected.LastFailureWarning >= TimeSpan.FromSeconds(30))
                        {
                            selected.LastFailureWarning = DateTime.UtcNow;
                            _log.LogWarning("[P2L] Transport {Transport} failed; trying warm backup: {Reason}", selected.Channel.Name, ex.Message);
                        }
                }
            }
        }
        finally
        {
            if (eventId is not null) _eventAcks.TryRemove(eventId, out _);
        }
    }

    private State? Select(HashSet<State> tried)
    {
        bool Available(State s) => s.Ready && s.Channel.IsConnected && !tried.Contains(s);
        var best = _states.FirstOrDefault(Available);
        if (_active is null || !Available(_active) || best is null) return best;
        if (Array.IndexOf(_states, best) < Array.IndexOf(_states, _active)
            && DateTime.UtcNow - best.HealthySince >= TimeSpan.FromMilliseconds(_settings.FailbackStableMs)) return best;
        return _active;
    }

    private async Task HealthLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                foreach (var state in _states)
                {
                    string? probe = null;
                    lock (_sync)
                    {
                        if (!state.Channel.IsConnected) { MarkFault(state); continue; }
                        if (state.ProbeId is not null && state.ProbeDeadline <= now)
                        {
                            state.ProbeId = null;
                            MarkFault(state);
                        }
                        if (state.ProbeId is null && state.NextProbe <= now)
                        {
                            probe = state.ProbeId = Guid.NewGuid().ToString("N");
                            state.ProbeDeadline = now.AddMilliseconds(_settings.ProbeTimeoutMs);
                            state.NextProbe = now.AddMilliseconds(_settings.ProbeIntervalMs);
                        }
                    }
                    if (probe is not null)
                    {
                        var task = SendProbeAsync(state, probe, ct);
                        _probes[probe] = task;
                        _ = ObserveProbeAsync(probe, task);
                    }
                }
                await Task.Delay(Math.Min(100, _settings.ProbeIntervalMs), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private async Task ObserveProbeAsync(string id, Task task)
    {
        try { await task; }
        finally { _probes.TryRemove(id, out _); }
    }

    private async Task SendProbeAsync(State state, string probe, CancellationToken ct)
    {
        try
        {
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(_settings.ProbeTimeoutMs);
            await state.Channel.SendAsync(_settings.EventTopic, Control("transport_ping", "probeId", probe), false, attempt.Token)
                .WaitAsync(attempt.Token);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            lock (_sync) if (state.ProbeId == probe) { state.ProbeId = null; MarkFault(state); }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }

    private void MarkFault(State state)
    {
        state.Ready = false;
        state.HealthySince = DateTime.MinValue;
        if (!state.Fault.IsCancellationRequested) state.Fault.Cancel();
    }

    private void MarkReady(State state)
    {
        if (!state.Ready)
        {
            state.Fault.Dispose();
            state.Fault = new CancellationTokenSource();
            state.HealthySince = DateTime.UtcNow;
        }
        state.Ready = true;
    }

    private static byte[] Control(string type, string name, string value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("type", type); writer.WriteString(name, value); writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _cts.CancelAsync();
        if (_healthLoop is not null) await _healthLoop;
        await Task.WhenAll(_probes.Values);
        foreach (var (state, handler, connection) in _handlers)
        {
            state.Channel.MessageReceived -= handler;
            state.Channel.ConnectionChanged -= connection;
        }
        foreach (var state in _states)
        {
            await state.Channel.DisposeAsync();
            state.Fault.Dispose();
        }
        foreach (var ack in _eventAcks.Values) ack.TrySetCanceled();
        _cts.Dispose();
    }
}
