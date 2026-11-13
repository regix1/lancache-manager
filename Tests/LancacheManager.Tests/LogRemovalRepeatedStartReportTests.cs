using System.Reflection;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using static LancacheManager.Tests.LogRemovalQuietStretchTests;

namespace LancacheManager.Tests;

/// <summary>
/// The log removal child's first report repeats the starting report the host already sent, and on a real box it
/// arrives well after the progress interval. The removal must still send its later same-stage reports during a quiet
/// stretch, instead of leaving the card one report behind until the removal ends.
/// </summary>
public sealed class LogRemovalRepeatedStartReportTests
{
    private const string StartingStageKey = "signalr.logRemoval.starting.single";
    private const string RemovingStageKey = "signalr.logRemoval.removing";

    [Fact]
    public async Task ALateRepeatOfTheStartingReportStillLetsTheNextHeldReportOutAsync()
    {
        // The removal's own harness, private to LogRemovalProgressTests, reached the way
        // LogRemovalQuietStretchTests reaches it.
        var harnessType = typeof(LogRemovalProgressTests).GetNestedType("LogStepHarness", BindingFlags.NonPublic)!;
        var created = (Task)harnessType.GetMethod("CreateAsync")!.Invoke(null, [null, false])!;
        await created;
        var harness = created.GetType().GetProperty("Result")!.GetValue(created)!;
        await using var disposal = (IAsyncDisposable)harness;
        var removal = (RustLogRemovalService)harnessType.GetProperty("Removal")!.GetValue(harness)!;
        var step = (RustProcessHelper)harnessType.GetProperty("Rust")!.GetValue(harness)!;
        var sent = (RecordingNotificationProxy)harnessType.GetProperty("RemovalNotifications")!.GetValue(harness)!;
        // The child waits past the progress interval, writes the seed report that repeats the host's starting
        // report, then two removing reports at once, and stays quiet until the test lets it go on.
        var child = new QuietAfterReports(
            step,
            [
                new LogRemovalProgress { PercentComplete = 0, Status = "running", StageKey = StartingStageKey },
                new LogRemovalProgress
                {
                    PercentComplete = 10, Status = "running", StageKey = RemovingStageKey, FilesProcessed = 1,
                    LinesProcessed = 1
                },
                new LogRemovalProgress
                {
                    PercentComplete = 20, Status = "running", StageKey = RemovingStageKey, FilesProcessed = 1,
                    LinesProcessed = 2
                }
            ],
            TimeSpan.FromMilliseconds(300));
        typeof(RustLogRemovalService)
            .GetField("_rustProcessHelper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(removal, child);

        var run = (Task<bool>)harnessType.GetMethod("RunRemovalAsync")!.Invoke(harness, null)!;
        try
        {
            await child.QuietStretchEntered.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(SentLinesProcessed(sent, 1), "The log removal's first removing report (1 line processed) was not sent.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!SentLinesProcessed(sent, 2) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(
                SentLinesProcessed(sent, 2),
                "After the child repeated the starting report late, the log removal never sent its second removing "
                + "report (2 lines processed) while it went quiet: the card still showed 1 line processed.");
        }
        finally
        {
            child.Release();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }
}
