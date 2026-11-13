using System.Collections.Concurrent;
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
/// A Game Detection Run Now that waits behind a detection run already going, with the detection scan mode
/// changing before or after its waiting card goes up. The scan the run performs when its turn comes is
/// the scan its waiting card named.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class WaitingDetectionRunScanTypeTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADetectionRunWaitingBehindABusyRun_RunsTheScanItsCardNames(bool modeChangesAfterTheCard)
    {
        var mode = GameDetectionScanMode.Incremental;
        var state = CacheScanGateHarness.CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetGameDetectionScanMode) => mode,
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            nameof(IStateService.GetGlobalNotificationDisplayMode) => NotificationDisplayMode.Condensed,
            nameof(IStateService.GetHiddenPrefillRules) => new List<ClientExclusionRule>(),
            nameof(IStateService.GetPrefillRules) => new List<ClientExclusionRule>(),
            _ => NullReturningProxy.DefaultFor(method.ReturnType)
        });
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);

        // The scan each run asked the operation queue for, in order. The first run stays inside the queue
        // until the test lets it go, so the second Run Now finds the schedule busy.
        var requested = new ConcurrentQueue<DetectionScanType>();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<OperationStartResponse> EnqueueAsync(object?[] args)
        {
            requested.Enqueue(((GameDetectionRequestKey)args[5]!).ScanType);
            if (requested.Count == 1)
            {
                firstEntered.TrySetResult();
                await releaseFirst.Task;
            }
            return new OperationStartResponse { OperationId = Guid.NewGuid(), Status = OperationStartStatus.Started };
        }

        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>((method, args) =>
                method.Name == nameof(IOperationWaitlist.EnqueueAsync) ? EnqueueAsync(args!) : NullReturningProxy.DefaultFor(method.ReturnType)),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());

        var previousGate = ScheduledServiceBase.ScheduleRunGate;
        var previousWait = ScheduledServiceBase.WaitForDownloadAnswer;
        try
        {
            var schedules = new ServiceScheduleRegistry(
                [loop],
                state,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(),
                tracker,
                activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());
            ScheduledServiceBase.WaitForDownloadAnswer = null;
            await loop.StartAsync(CancellationToken.None);
            try
            {
                await schedules.TriggerRunAsync("gameDetection");
                await firstEntered.Task.WaitAsync(Timeout);

                if (!modeChangesAfterTheCard) mode = GameDetectionScanMode.Full;
                await schedules.TriggerRunAsync("gameDetection");
                var card = Assert.Single(tracker.GetWaitingOperations(), operation => operation.Type == OperationType.GameDetection);
                if (modeChangesAfterTheCard) mode = GameDetectionScanMode.Full;

                releaseFirst.TrySetResult();
                var deadline = DateTime.UtcNow + Timeout;
                while (requested.Count < 2)
                {
                    Assert.True(DateTime.UtcNow < deadline, "The waiting run never reached the operation queue");
                    await Task.Delay(10);
                }

                Assert.True(card.DetectionScanType == requested.Last(),
                    $"The waiting card named the {card.DetectionScanType} scan, but the run asked for the {requested.Last()} scan");
            }
            finally
            {
                releaseFirst.TrySetResult();
                await loop.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }
}
