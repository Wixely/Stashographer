using Microsoft.Extensions.Logging;

namespace Stashographer.Services.Diagnostics;

public sealed record TemporaryLogEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>
/// Keeps a small, short-lived copy of application logs for administrator diagnostics.
/// Entries are memory-only, bounded, and disappear on restart.
/// </summary>
public sealed class TemporaryLogStore : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly List<TemporaryLogEntry> _entries = [];
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retention;
    private readonly int _capacity;
    private long _sequence;

    public TemporaryLogStore(TimeProvider timeProvider, TimeSpan retention, int capacity)
    {
        _timeProvider = timeProvider;
        _retention = retention;
        _capacity = capacity;
    }

    public ILogger CreateLogger(string categoryName) => new StoreLogger(this, categoryName);

    public IReadOnlyList<TemporaryLogEntry> GetEntries()
    {
        lock (_gate)
        {
            RemoveExpired(_timeProvider.GetUtcNow());
            return _entries.OrderByDescending(entry => entry.Sequence).ToArray();
        }
    }

    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    internal void Add(LogLevel level, string category, string message, Exception? exception)
    {
        if (!category.StartsWith("Stashographer", StringComparison.Ordinal)) return;

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            RemoveExpired(now);
            _entries.Add(new TemporaryLogEntry(
                ++_sequence,
                now,
                level,
                category,
                message,
                exception?.ToString()));
            if (_entries.Count > _capacity)
                _entries.RemoveRange(0, _entries.Count - _capacity);
        }
    }

    private void RemoveExpired(DateTimeOffset now) =>
        _entries.RemoveAll(entry => now - entry.Timestamp > _retention);

    public void Dispose() { }

    private sealed class StoreLogger(TemporaryLogStore store, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) store.Add(logLevel, category, formatter(state, exception), exception);
        }
    }
}
