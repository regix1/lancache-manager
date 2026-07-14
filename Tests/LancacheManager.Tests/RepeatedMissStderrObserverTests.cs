using LancacheManager.Core.Services;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

public sealed class RepeatedMissStderrObserverTests
{
    [Fact]
    public void CoverageSummaryIsPromotedWithoutRawChildDetails()
    {
        var logger = new CapturingLogger();
        var observer = new RepeatedMissStderrObserver(logger, "primary");

        observer.Observe(
            "WARNING: repeated-MISS scan coverage gaps: malformed_lines=2 unsupported_ranges=3 unreadable_log_files=4 qualified_without_safe_file=5");
        observer.Observe("WARNING: Skipping unreadable log file /logs/private/access.log");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("malformed lines=2", entry.Message);
        Assert.Contains("unsupported ranges=3", entry.Message);
        Assert.Contains("unreadable logs=4", entry.Message);
        Assert.Contains("qualified misses without a safe file=5", entry.Message);
        Assert.DoesNotContain("/logs/private", entry.Message);
    }

    [Theory]
    [InlineData("WARNING: repeated-MISS scan coverage gaps: malformed_lines=-1 unsupported_ranges=0 unreadable_log_files=0 qualified_without_safe_file=0")]
    [InlineData("WARNING: repeated-MISS scan coverage gaps: malformed_lines=1 unsupported_ranges=2")]
    [InlineData("raw path /cache/private")]
    public void MalformedAndUnknownLinesAreNotPromoted(string line)
    {
        var logger = new CapturingLogger();
        var observer = new RepeatedMissStderrObserver(logger, "primary");

        observer.Observe(line);

        Assert.Empty(logger.Entries);
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
