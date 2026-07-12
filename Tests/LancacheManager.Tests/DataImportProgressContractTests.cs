using LancacheManager.Models;

namespace LancacheManager.Tests;

public class DataImportProgressContractTests
{
    [Fact]
    public void StructuredSnapshotsCarryRealProcessedAndTotalValues()
    {
        var metrics = new DataImportMetrics();
        var starting = metrics.CaptureProgress(
            "signalr.dataImport.starting", 0, new Dictionary<string, object?>());
        var progress = metrics.CaptureProgress(
            "signalr.dataImport.progress",
            25,
            new Dictionary<string, object?> { ["processed"] = 25UL, ["total"] = 100UL });

        Assert.Equal("signalr.dataImport.starting", starting.StageKey);
        Assert.Equal(25UL, progress.Context["processed"]);
        Assert.Equal(100UL, progress.Context["total"]);
        Assert.Same(progress, metrics.CurrentProgress);

        metrics.ClearProgress();
        Assert.Null(metrics.CurrentProgress);
    }
}
