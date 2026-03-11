using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace P2L.IntegrationTests.Infrastructure;

/// <summary>
/// Routes Microsoft.Extensions.Logging output to xUnit's ITestOutputHelper
/// so log lines appear inline with each test's output panel.
/// </summary>
public sealed class XunitLogger : ILogger
{
    private readonly ITestOutputHelper _out;
    private readonly string            _category;

    public XunitLogger(ITestOutputHelper output, string category)
    {
        _out      = output;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = $"[{DateTime.Now:HH:mm:ss.fff}] [{level,-11}] [{_category}] {formatter(state, exception)}";
        if (exception != null) msg += $"\n  {exception.GetType().Name}: {exception.Message}";
        try { _out.WriteLine(msg); } catch { /* xUnit output may be closed after test */ }
    }
}

public sealed class XunitLoggerFactory : ILoggerFactory
{
    private readonly ITestOutputHelper _out;
    public XunitLoggerFactory(ITestOutputHelper output) => _out = output;
    public ILogger CreateLogger(string categoryName) => new XunitLogger(_out, categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

public static class LoggerFactoryExtensions
{
    public static ILogger<T> CreateLogger<T>(this ITestOutputHelper output) =>
        new XunitLoggerFactory(output).CreateLogger<T>();
}
