using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A hybrid-mode game detection held for a download shows a waiting card that names its scan, incremental
/// or full. Hybrid picks full once the last full scan is a week old, and a hold can last hours, so the week
/// can run out while the card waits. The scan that runs when the download ends has to be the scan the card
/// named, or the user who read "incremental" gets a full scan.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class HybridDetectionHoldTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public enum WeekRunsOut
    {
        BeforeTheHold,
        WhileTheCardWaits
    }

    [Theory]
    [InlineData(WeekRunsOut.BeforeTheHold)]
    [InlineData(WeekRunsOut.WhileTheCardWaits)]
    public async Task HeldHybridDetectionRunsTheScanItsCardNamedAsync(WeekRunsOut weekRunsOut)
    {
        // The last full scan is six days old; a week after it hybrid mode picks a full scan. No clock is
        // moved: the stored last full scan is moved back two days instead, which is the same week running out.
        var state = DispatchProxy.Create<IStateService, HybridModeState>();
        var hybrid = (HybridModeState)(object)state;
        hybrid.LastFullScanUtc = DateTime.UtcNow.AddDays(-6);
        void RunTheWeekOut() => hybrid.LastFullScanUtc = DateTime.UtcNow.AddDays(-8);

        var snapshot = new DownloadSpeedSnapshot();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        var requested = new TaskCompletionSource<(DetectionScanType Ran, string? CardShowed)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? cardAtHold = null;

        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>((_, args) =>
            {
                var notice = (RunNotice)args![7]!;
                // What the waiting card says at the moment its run asks for the scan.
                var card = tracker.GetRuns().Runs.FirstOrDefault(run => run.OperationId == notice.PendingId);
                requested.TrySetResult((((GameDetectionRequestKey)args[5]!).ScanType, card?.ScanMode ?? cardAtHold));
                return Task.FromResult(new OperationStartResponse
                {
                    OperationId = Guid.NewGuid(),
                    Status = OperationStartStatus.Started
                });
            }),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            var registry = new ServiceScheduleRegistry(
                [loop],
                CacheScanGateHarness.VisibleClientsStateService(),
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            ScheduledServiceBase.WaitForDownloadAnswer = null;
            await loop.StartAsync(CancellationToken.None);
            try
            {
                if (weekRunsOut == WeekRunsOut.BeforeTheHold) RunTheWeekOut();

                // A download is writing, so the run is held on a waiting card.
                CacheScanGateHarness.MakeBusy(snapshot);
                await registry.TriggerRunAsync("gameDetection");
                await WaitForAsync(() => tracker.GetWaitingOperations().Any());
                var heldCard = Assert.Single(tracker.GetWaitingOperations());
                cardAtHold = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == heldCard.Id).ScanMode;

                if (weekRunsOut == WeekRunsOut.WhileTheCardWaits) RunTheWeekOut();

                // The download ends and the held run goes ahead.
                CacheScanGateHarness.MakeIdle(snapshot);
                CacheScanGateHarness.RaiseDownloadsEnded();
                var (ran, cardShowed) = await requested.Task.WaitAsync(Timeout);

                Assert.True(cardShowed == ran.ToWireString(),
                    $"The waiting card said '{cardShowed}' (when the run was held: '{cardAtHold}'), "
                    + $"and the run that went ahead asked for the {ran.ToWireString()} scan.");
            }
            finally
            {
                await loop.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The run was never held on a waiting card");
            await Task.Delay(10);
        }
    }

    /// <summary>A state whose detection mode is hybrid, with a last full scan the test sets.</summary>
    internal class HybridModeState : NullReturningProxy
    {
        public DateTime? LastFullScanUtc { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => GameDetectionScanMode.Hybrid,
            nameof(IStateService.GetGameDetectionLastFullScan) => LastFullScanUtc,
            _ => base.Invoke(targetMethod, args)
        };
    }
}
