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

    [Theory]
    [InlineData(OperationType.LogRemoval)]
    [InlineData(OperationType.GameDetection)]
    [InlineData(OperationType.CacheSizeScan)]
    [InlineData(OperationType.DatabaseReset)]
    public void IncrementalBatch_DoesNotBypassOtherOperationTypes(OperationType activeType)
    {
        var conflict = ConflictFor(activeType);

        Assert.False(
            LiveLogMonitorService.CanBypassConflictForIncrementalIngestion(conflict, 10_000));
    }

    private static OperationConflictResponse ConflictFor(OperationType activeType) => new()
    {
        ActiveOperationType = activeType.ToString()
    };
}
