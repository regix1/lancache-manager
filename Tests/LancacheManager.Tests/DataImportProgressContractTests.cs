using LancacheManager.Models;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class DataImportProgressContractTests
{
    [Fact]
    public async Task TerminalCleanupRejectsLateImportProgress()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var controller = new DataMigrationController(NullLogger<DataMigrationController>.Instance,
            notifications, tracker, null!, new ConfigurationBuilder().Build());
        var metrics = new DataImportMetrics();
        var id = tracker.RegisterOperation(OperationType.DataImport, "import", new CancellationTokenSource(),
            metadata: metrics, onTerminalCleanup: metrics.ClearProgress);
        var report = typeof(DataMigrationController).GetMethod("ReportProgressAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)report.Invoke(controller, [id, metrics, 25UL, 100UL, 20UL, 5UL, "progress"])!;
        Assert.Equal(25UL, metrics.CurrentProgress!.Context["processed"]);
        var count = notifications.Events.Count;
        tracker.CompleteOperation(id, false, error: "stopped");
        await (Task)report.Invoke(controller, [id, metrics, 90UL, 100UL, 80UL, 10UL, "late"])!;
        Assert.Null(metrics.CurrentProgress);
        Assert.Equal(count, notifications.Events.Count);
        Assert.Equal("stopped", tracker.GetOperation(id)!.Message);
    }

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
