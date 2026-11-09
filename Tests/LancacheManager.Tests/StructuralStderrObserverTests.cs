using LancacheManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

public class StructuralStderrObserverTests
{
    [Fact]
    public void UnknownLinesAreNotPromoted()
    {
        var logger = new CapturingLogger();
        var observer = new StructuralStderrObserver(logger, "primary");

        observer.Observe("raw /cache/secret/key URL=https://example.invalid/private");
        observer.Complete();

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void WarningFloodIsRateLimitedAndFinalTotalIsReported()
    {
        var ticks = 1_000L;
        var logger = new CapturingLogger();
        var observer = new StructuralStderrObserver(logger, "primary", () => ticks);

        observer.Observe("WARNING: structural inspection I/O error (sample 1/5); path details suppressed");
        observer.Observe("WARNING: structural inspection I/O error (sample 2/5); path details suppressed");
        ticks += 30_000;
        observer.Observe("WARNING: structural inspection I/O errors suppressed after 5 samples; total=12");
        observer.Complete();

        var warnings = logger.Entries.Where(entry => entry.Level == LogLevel.Warning).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.All(warnings, warning => Assert.DoesNotContain("/cache/", warning.Message));
        Assert.Contains("12", warnings[^1].Message);
    }

    private sealed class CapturingLogger : ILogger
    {
        internal List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
