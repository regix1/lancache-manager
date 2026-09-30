using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class LiveLogMonitorConcurrencyTests
{
    /// <summary>
    /// How long a line can sit inside nginx before it reaches the log file at all, in the stock
    /// lancache config (<c>buffer=128k flush=5s</c>). The manager cannot see a download sooner than
    /// this however often it looks.
    /// </summary>
    private const int NginxAccessLogFlushSeconds = 5;

    [Fact]
    public void RateLimit_UsesEachDatasourceTimestamp()
    {
        var now = new DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc);
        var lastProcessTimes = new Dictionary<string, DateTime>
        {
            ["alpha"] = now,
            ["beta"] = now.AddSeconds(-10)
        };

        Assert.Equal(0d, LiveLogMonitorService.SecondsSinceLastProcess(lastProcessTimes, "alpha", now));
        Assert.Equal(10d, LiveLogMonitorService.SecondsSinceLastProcess(lastProcessTimes, "beta", now));
    }

    [Fact]
    public void TheTrickleFlush_WaitsLongerThanNginxHoldsALine()
    {
        // The wait a small download pays before its row exists. Dropping it below nginx's own flush
        // does not make a download appear sooner, because the line is not in the file yet; it just
        // spends a Rust run on a buffer that has not been written. The margin is what keeps a wakeup
        // from landing on the boundary itself. [53]
        Assert.True(
            LiveLogMonitorService.MaxSecondsBeforeTrickleFlush > NginxAccessLogFlushSeconds,
            $"the trickle flush ({LiveLogMonitorService.MaxSecondsBeforeTrickleFlush}s) must clear "
                + $"nginx's {NginxAccessLogFlushSeconds}s access-log flush");
    }

    [Fact]
    public void TheLastProcessTimeIsStampedWhenThePassEnds()
    {
        var source = File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(),
            "Api",
            "LancacheManager",
            "Core",
            "Services",
            "Logs",
            "LiveLogMonitorService.cs"));
        const string assignment = "_lastProcessTime[datasource.Name] = DateTime.UtcNow;";

        Assert.Equal(1, source.Split("_lastProcessTime[datasource.Name] =", StringSplitOptions.None).Length - 1);
        Assert.Contains("internal const int MaxSecondsBeforeTrickleFlush = 7;", source, StringComparison.Ordinal);
        Assert.Contains("private readonly int _minSecondsBetweenProcessing = 1;", source, StringComparison.Ordinal);

        var start = source.IndexOf("StartProcessingAsync(", StringComparison.Ordinal);
        var finallyBlock = source.IndexOf("finally", start, StringComparison.Ordinal);
        var blockStart = source.IndexOf('{', finallyBlock);
        var stamp = source.IndexOf(assignment, StringComparison.Ordinal);
        var processingEnd = source.IndexOf("_isProcessing = false;", stamp, StringComparison.Ordinal);
        var blockEnd = source.IndexOf('}', processingEnd);

        Assert.True(start >= 0);
        Assert.True(finallyBlock > start);
        Assert.True(blockStart < stamp);
        Assert.True(stamp < processingEnd);
        Assert.True(processingEnd < blockEnd);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(LiveLogMonitorService.MaxConcurrentCorruptionIngestionBytes)]
    public void IncrementalBatch_BypassesCorruptionDetection_AtOrBelowLimit(long pendingBytes)
    {
        var conflict = ConflictFor(OperationType.CorruptionDetection);

        Assert.True(
            LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(conflict, pendingBytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(LiveLogMonitorService.MaxConcurrentCorruptionIngestionBytes + 1)]
    public void IncrementalBatch_DoesNotBypassCorruptionDetection_OutsideLimit(long pendingBytes)
    {
        var conflict = ConflictFor(OperationType.CorruptionDetection);

        Assert.False(
            LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(conflict, pendingBytes));
    }

    /// Two things disqualify an operation from running beside a live ingest, and each of these
    /// fails one. Log removal and a database reset rewrite what ingestion reads and writes. The
    /// eviction scan never touches access.log but flags Download rows IsEvicted while it runs, and
    /// those are the rows ingestion is inserting and updating.
    [Theory]
    [InlineData(OperationType.LogRemoval)]
    [InlineData(OperationType.DatabaseReset)]
    [InlineData(OperationType.EvictionScan)]
    public void IncrementalBatch_DoesNotBypassOperationsThatWriteWhatIngestionWrites(
        OperationType activeType)
    {
        var conflict = ConflictFor(activeType);

        Assert.False(
            LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(conflict, 10_000));
    }

    /// Neither of these writes what ingestion writes. The cache size scan only reads the cache
    /// tree, the log and Downloads, putting its totals in the snapshots. Game detection does write
    /// Downloads, but only to clear IsEvicted on rows whose probe found files, and ingestion never
    /// writes that column. Blocking a small ingest through one of them froze the dashboard at zero
    /// for the length of a scan while downloads were running.
    [Theory]
    [InlineData(OperationType.GameDetection)]
    [InlineData(OperationType.CacheSizeScan)]
    public void IncrementalBatch_BypassesOperationsThatDoNotWriteWhatIngestionWrites(
        OperationType activeType)
    {
        var conflict = ConflictFor(activeType);

        Assert.True(
            LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(conflict, 10_000));
    }

    private static OperationConflictResponse ConflictFor(OperationType activeType) => new()
    {
        ActiveOperationType = activeType.ToString(),
        StageKey = "errors.conflict.heavyOperationActive"
    };
}
