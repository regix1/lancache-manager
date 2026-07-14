using System.Collections.Concurrent;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class RustProcessHelperProgressTests
{
    [Fact]
    public async Task StalePollCallbackDrainsBeforeTerminalProgressIsDeliveredAsync()
    {
        using var pollCts = new CancellationTokenSource();
        var progressPath = Path.GetTempFileName();
        await File.WriteAllTextAsync(progressPath, """{"status":"completed"}""");
        var callbacks = new ConcurrentQueue<string>();
        var callbackStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pollTask = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, pollCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Model a poll that already read an older snapshot and entered its callback.
            }

            callbackStarted.SetResult();
            await releaseCallback.Task;
            callbacks.Enqueue("stale");
        });

        try
        {
            var helper = new RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                processManager: null!,
                pathResolver: null!,
                operationTracker: null!);
            var delivering = helper.StopProgressPollingAndDeliverTerminalProgressAsync<ProgressState>(
                pollCts,
                pollTask,
                progressPath,
                progress =>
                {
                    callbacks.Enqueue(progress.Status);
                    return Task.CompletedTask;
                });
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(delivering.IsCompleted);
            releaseCallback.SetResult();
            await delivering.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(["stale", "completed"], callbacks);
        }
        finally
        {
            File.Delete(progressPath);
        }
    }

    private sealed record ProgressState(string Status);
}
