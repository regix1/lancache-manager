using LancacheManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly object _sync = new();
    private readonly List<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);

        lock (_sync)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }
}

internal sealed class CapturingLogger : ILogger<SteamDaemonService>
{
    internal sealed record LogEntry(LogLevel Level, string Message);

    private readonly object _sync = new();
    private readonly List<LogEntry> _entries = [];

    public IReadOnlyList<LogEntry> Entries
    {
        get { lock (_sync) return _entries.ToArray(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_sync)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
