using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A log removal reports two lines on the same stage one right after the other, then goes quiet on a large log.
/// The card must show the second report during that stretch, instead of the first one until the removal ends.
/// </summary>
public sealed class LogRemovalQuietStretchTests
{
    private const string RemovingStageKey = "signalr.logRemoval.removing";

    [Fact]
    public async Task ALogRemovalSendsItsSecondSameStageReportBeforeAQuietStretchEndsAsync()
    {
        // The removal's own harness, private to LogRemovalProgressTests, reached the way RemovalProgressGateTests
        // reaches CorruptionRemovalContractTests.RemovalRun.
        var harnessType = typeof(LogRemovalProgressTests).GetNestedType("LogStepHarness", BindingFlags.NonPublic)!;
        var created = (Task)harnessType.GetMethod("CreateAsync")!.Invoke(null, [null, false])!;
        await created;
        var harness = created.GetType().GetProperty("Result")!.GetValue(created)!;
        await using var disposal = (IAsyncDisposable)harness;
        var removal = (RustLogRemovalService)harnessType.GetProperty("Removal")!.GetValue(harness)!;
        var step = (RustProcessHelper)harnessType.GetProperty("Rust")!.GetValue(harness)!;
        var sent = (RecordingNotificationProxy)harnessType.GetProperty("RemovalNotifications")!.GetValue(harness)!;
        var child = new QuietAfterReports(
            step,
            [.. new long[] { 1, 2 }.Select(lines => new LogRemovalProgress
            {
                PercentComplete = 10 * lines,
                Status = "running",
                StageKey = RemovingStageKey,
                FilesProcessed = 1,
                LinesProcessed = lines,
                LinesRemoved = 0
            })],
            TimeSpan.Zero);
        typeof(RustLogRemovalService)
            .GetField("_rustProcessHelper", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(removal, child);

        var run = (Task<bool>)harnessType.GetMethod("RunRemovalAsync")!.Invoke(harness, null)!;
        try
        {
            await child.QuietStretchEntered.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(SentLinesProcessed(sent, 1), "The log removal's first report (1 line processed) was not sent.");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (!SentLinesProcessed(sent, 2) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(
                SentLinesProcessed(sent, 2),
                "The log removal never sent its second report (2 lines processed) while it went quiet: the card "
                + "still showed 1 line processed.");
        }
        finally
        {
            child.Release();
            await run.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    internal static bool SentLinesProcessed(RecordingNotificationProxy sent, long linesProcessed)
    {
        // Read by index: the removal appends from its own thread, which an enumerator would refuse.
        var calls = sent.Invocations;
        for (var index = 0; index < calls.Count; index++)
        {
            if (calls[index].Args is not [SignalREvents.LogRemovalProgress, { } payload, ..])
            {
                continue;
            }

            var type = payload.GetType();
            if (Equals(type.GetProperty("StageKey")?.GetValue(payload), RemovingStageKey)
                && Equals(type.GetProperty("LinesProcessed")?.GetValue(payload), linesProcessed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The removal's child as the harness runs it, except that it first waits <paramref name="wait"/>, writes
    /// <paramref name="reports"/> one right after the other and then stays quiet until the test lets it go on.
    /// </summary>
    internal sealed class QuietAfterReports(
        RustProcessHelper step,
        IReadOnlyList<LogRemovalProgress> reports,
        TimeSpan wait) : RustProcessHelper(
        NullLogger<RustProcessHelper>.Instance,
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        (IPathResolver)Field(step, "_pathResolver"),
        (IUnifiedOperationTracker)Field(step, "_operationTracker"))
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task QuietStretchEntered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            // The quoted arguments are the log directory, the service and the progress file.
            var progressPath = start.Arguments.Split('"')[5];
            Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
            await Task.Delay(wait, cancellationToken);
            foreach (var report in reports)
            {
                await File.WriteAllTextAsync(progressPath, JsonSerializer.Serialize(report), cancellationToken);
                await onProgressEvent!(new RustProgressEvent());
            }

            _entered.TrySetResult();
            await _released.Task.WaitAsync(cancellationToken);
            return await step.ExecuteTrackedProcessWithProgressEventsAsync(
                start, operationId, cancellationToken, onProgressEvent, processLabel);
        }

        private static object Field(RustProcessHelper helper, string name) => typeof(RustProcessHelper)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(helper)!;
    }
}
