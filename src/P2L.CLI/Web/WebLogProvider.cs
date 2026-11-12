using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace P2L.CLI.Web;

internal sealed class WebLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<(string Time, string Level, string Message)> _entries = new();
    public event Action<JsonObject>? EntryAdded;
    public ILogger CreateLogger(string categoryName) => new EntryLogger(this);
    public void Clear() => _entries.Clear();
    public JsonArray Snapshot() => new(_entries.Select(entry => (JsonNode)new JsonObject
        {["time"] = entry.Time, ["level"] = entry.Level, ["message"] = entry.Message}).ToArray());
    public void Dispose() { }
    private sealed class EntryLogger(WebLogProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        {
            if (!IsEnabled(level)) return;
            var entry = (Time: DateTime.UtcNow.ToString("O"), Level: level.ToString().ToLowerInvariant(), Message: format(state, error));
            owner._entries.Enqueue(entry);
            owner.EntryAdded?.Invoke(new JsonObject {["time"] = entry.Time, ["level"] = entry.Level, ["message"] = entry.Message});
            while (owner._entries.Count > 500) owner._entries.TryDequeue(out _);
        }
    }
}
