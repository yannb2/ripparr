namespace Ripparr.Logging;

public sealed record LogLine(long Id, DateTime Time, LogLevel Level, string Source, string Message, string? Exception);

/// <summary>
/// The app's own log (not the jobs': each job has its log file), the last lines in memory for the
/// Logs page. Lost on restart; the container log (docker logs) has everything.
/// </summary>
public sealed class MemoryLog
{
    public const int Capacity = 2000;

    private readonly LinkedList<LogLine> _lines = new();
    private readonly object _lock = new();
    private long _next;

    /// <summary>Raised after each new line (from any thread).</summary>
    public event Action? Changed;

    public void Add(LogLevel level, string source, string message, Exception? exception)
    {
        lock (_lock)
        {
            _lines.AddLast(new LogLine(++_next, DateTime.UtcNow, level, source, message, exception?.ToString()));
            while (_lines.Count > Capacity)
            {
                _lines.RemoveFirst();
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Newest first.</summary>
    public List<LogLine> Lines()
    {
        lock (_lock)
        {
            return _lines.Reverse().ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _lines.Clear();
        }

        Changed?.Invoke();
    }

    /// <summary>"Ripparr.Jobs.JobRunner" → "JobRunner": the class is enough on the page.</summary>
    public static string ShortSource(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 ? category : category[(dot + 1)..];
    }
}

/// <summary>Feeds <see cref="MemoryLog"/> from the normal logging, with the usual level filters.</summary>
public sealed class MemoryLoggerProvider(MemoryLog log) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new MemoryLogger(log, categoryName);

    public void Dispose()
    {
    }

    private sealed class MemoryLogger(MemoryLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                log.Add(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
