using LancacheManager.Core.Services;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class LiveLogMonitorConcurrencyTests
{
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
