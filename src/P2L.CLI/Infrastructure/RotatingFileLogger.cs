using Microsoft.Extensions.Logging;
using System.Text;

namespace P2L.CLI.Infrastructure;

/// <summary>
/// A minimal AOT-compatible rotating file logger for hardware TX/RX audit trails.
///
/// Creates files named p2l_YYYYMMDD_HH.log in the configured directory.
/// Rolls to a new file when the size limit is reached or the hour changes.
/// </summary>
public sealed class RotatingFileLogger : ILogger
{
    private readonly string _category;
    private readonly RotatingFileLoggerProvider _provider;

    public RotatingFileLogger(string category, RotatingFileLoggerProvider provider)
    {
        _category = category;
        _provider = provider;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= _provider.MinLevel;

    public void Log<TState>(LogLevel level, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(level)) return;
        var message = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level,-11}] [{_category}] {formatter(state, exception)}";
        if (exception is not null)
            message += $"\n{exception}";
        _provider.Write(message);
    }
}

public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly string  _directory;
    private readonly long    _maxBytes;
    public  LogLevel MinLevel { get; }

    private StreamWriter? _writer;
    private string        _currentPath = "";
    private long          _currentSize;
    private string        _hour = "";
    private int           _segment;
    private readonly object _lock = new();

    public RotatingFileLoggerProvider(string directory, long maxBytes, LogLevel minLevel = LogLevel.Debug)
    {
        _directory = directory;
        _maxBytes  = Math.Max(1, maxBytes);
        MinLevel   = minLevel;
        // Best-effort: log directory may be created later (e.g. first write).
        // Do NOT throw here — a failure would crash the process before SCM handshake.
        try { Directory.CreateDirectory(directory); } catch { }
    }

    public ILogger CreateLogger(string categoryName) =>
        new RotatingFileLogger(categoryName, this);

    public void Write(string line)
    {
        lock (_lock)
        {
            EnsureWriter();
            _writer!.WriteLine(line);
            _writer.Flush();
            _currentSize += Encoding.UTF8.GetByteCount(line + Environment.NewLine);

            if (_currentSize >= _maxBytes)
            {
                _writer.Dispose();
                _writer = null;
            }
        }
    }

    private void EnsureWriter()
    {
        var hour = DateTime.Now.ToString("yyyyMMdd_HH");
        if (_hour != hour) { _hour = hour; _segment = 0; }
        string path;
        do
        {
            var suffix = _segment == 0 ? "" : $"_{_segment}";
            path = Path.Combine(_directory, $"p2l_{hour}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maxBytes) break;
            _segment++;
        } while (true);

        if (_writer is not null && path == _currentPath) return;

        _writer?.Dispose();
        _currentPath = path;
        Directory.CreateDirectory(_directory);  // retry every roll in case dir was unavailable at startup
        // FileShare.ReadWrite: multiple processes (dashboard + one-shot commands)
        // can safely append to the same log file concurrently.
        var stream   = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer      = new StreamWriter(stream) { AutoFlush = false };
        _currentSize = File.Exists(path) ? new FileInfo(path).Length : 0;
    }

    public void Dispose()
    {
        lock (_lock) _writer?.Dispose();
    }
}
