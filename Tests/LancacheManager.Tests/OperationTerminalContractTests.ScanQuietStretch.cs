using LancacheManager.Models;

namespace LancacheManager.Tests;

public sealed partial class OperationTerminalContractTests
{
    /// <summary>
    /// The scanner's last checkpoint lands within the send interval of the one before it on the same stage, and the
    /// scan then goes quiet before its completion reaches the browser. The live card must still get that checkpoint
    /// in the quiet stretch, instead of showing the older count until the completion arrives.
    /// </summary>
    [Fact]
    public async Task ACorruptionScanSendsItsLastSameStageCheckpointBeforeAQuietStretchEndsAsync()
    {
        const CorruptionDetectionMethod method = CorruptionDetectionMethod.RepeatedMiss;
        const string stage = "detection.final";
        await using var run = await TerminalRun.CreateAsync();
        var connection = await run.StartDetectionAsync(method);
        var entry = run.Observer.Entries[connection.Id];
        // The completion is held from the browser, so the scan's ending is a quiet stretch the test can watch.
        entry.HoldEmit = true;

        // A checkpoint for 1 of 4 files goes out live, then the last one (4 of 4) on the same stage follows at once.
        await run.DetectionProgressAsync(connection, method, stage, false);
        var sentBeforeLast = run.Messages.ProgressCount(connection.Id);
        var finish = run.FinishDetectionAsync(connection, method);
        try
        {
            await entry.EmitEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (run.Messages.ProgressCount(connection.Id) == sentBeforeLast && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            Assert.True(
                run.Messages.ProgressCount(connection.Id) > sentBeforeLast,
                "The scan's last checkpoint (4 of 4 files) was never sent live: the card still showed 1 of 4 files "
                + "while the completion had not reached the browser.");
        }
        finally
        {
            entry.EmitRelease.TrySetResult();
            await finish;
        }

        run.AssertHealthy();
    }
}
