using LancacheManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

public class StructuralStderrObserverTests
{
    [Fact]
    public void MilestonesAreSanitizedAndUnknownLinesAreNotPromoted()
    {
        var logger = new CapturingLogger();
        var observer = new StructuralStderrObserver(logger, "primary");

        observer.Observe("[structural] scan starting");
        observer.Observe("[structural] enumeration progress eligible_files=100");
        observer.Observe("[structural] inspection starting total=100 workers=4 task_capacity=4 result_capacity=4");
        observer.Observe("[structural] inspection progress processed=12/100 suspects=2 files_per_second=31.5 eta_seconds=3");
        observer.Observe("raw /cache/secret/key URL=https://example.invalid/private");

        Assert.Equal(4, logger.Entries.Count);
        Assert.All(logger.Entries, entry => Assert.Equal(LogLevel.Information, entry.Level));
        var combined = string.Join(" ", logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("/cache/secret", combined);
        Assert.DoesNotContain("https://", combined);
        Assert.Contains("processed=12/100", combined);
        Assert.Contains("workers=4", combined);
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
