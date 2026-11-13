using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A user force stops a run while its code waits in a step that does not see the stop, so the run's card has ended
/// while its code still runs. The same run asked for again in that time, the way its controller asks, must wait on a
/// card and start once the stopped run's code has ended: an unmapped-file removal and a single interactive log run.
/// </summary>
public sealed class ForceStoppedUnmappedRemovalAndLogRunTests
{
    [Fact]
    public async Task AnUnmappedFileRemovalAskedForWhileAForceStoppedRemovalStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var state = DispatchProxy.Create<IStateService, HeldFullScanRead>();
        var read = (HeldFullScanRead)(object)state;
        read.Inner = host.Services.GetRequiredService<IStateService>();
        var management = host.Create<CacheManagementService>(state);
        var limits = new UnmappedFilesLimits(10, 1_000_000);

        // The removal's worker, holding the cache lock, is held in its read of the last full scan.
        read.Arm();
        var stopped = Assert.IsType<Guid>(await management.StartUnmappedFileRemovalAsync(limits));
        try
        {
            await read.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
            await host.ForceStopAsync(stopped);

            var (outcome, started) = await host.AskThroughTheQueueAsync(
                OperationType.UnmappedFileRemoval, ConflictScope.Bulk(), "Unmapped File Removal",
                () => management.StartUnmappedFileRemovalAsync(limits),
                new UnmappedFileRemovalRequestKey(limits));
            ForceStoppedOwnerHost.AssertWaits("The unmapped-file removal", "unmapped-file removal", outcome);

            read.Release();
            await ForceStoppedOwnerHost.AssertRunsAfterAsync("unmapped-file removal", "unmapped-file removal", stopped, started);
        }
        finally
        {
            read.Release();
        }
    }

    [Fact]
    public async Task ALogRunAskedForWhileAForceStoppedLogRunStillRuns_WaitsOnACardAndRunsAfterIt()
    {
        await using var host = await ForceStoppedOwnerHost.CreateAsync();
        var processor = host.Create<RustLogProcessorService>(TimeProvider.System);
        var logs = Path.Combine(host.Root, "logs", "alpha");

        // The single interactive run is held in its started send, after it registered and took its hold.
        host.Notifications.Hold(SignalREvents.LogProcessingStarted);
        var stopped = Assert.IsType<Guid>(await processor.StartInBackgroundAsync(logs, datasourceName: "alpha"));
        await host.Notifications.Entered.Task.WaitAsync(ForceStoppedOwnerHost.Timeout);
        await host.ForceStopAsync(stopped);

        // Asked as LogsController.ProcessDatasourceLogsAsync asks.
        var (outcome, started) = await host.AskThroughTheQueueAsync(
            OperationType.LogProcessing, ConflictScope.Bulk(), "Log Processing (alpha)",
            () => processor.StartInBackgroundAsync(logs, datasourceName: "alpha"));
        ForceStoppedOwnerHost.AssertWaits("The log run", "log run", outcome);

        host.Notifications.Release();
        await ForceStoppedOwnerHost.AssertRunsAfterAsync("log run", "log run", stopped, started);
    }

    /// <summary>
    /// The app state, except that once armed, the next read of the last full detection scan waits for
    /// <see cref="Release"/> without seeing the run's stop, as a slow read does. Not sealed, for DispatchProxy.
    /// </summary>
    internal class HeldFullScanRead : DispatchProxy
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public IStateService Inner { get; set; } = null!;

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm() => Volatile.Write(ref _armed, 1);

        public void Release() => _release.TrySetResult();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IStateService.GetGameDetectionLastFullScan))
            {
                if (Interlocked.Exchange(ref _armed, 0) == 1)
                {
                    Entered.TrySetResult();
                    // A synchronous read, as the state's own is; the worker's thread waits here.
                    _release.Task.Wait();
                }

                return DateTime.UtcNow;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(exception.InnerException);
                throw;
            }
        }
    }
}
