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
/// A Game Detection run held for a client download, a second own run of the schedule while the download
/// goes on, and the detection scan mode changing at some point around them. The person sees one waiting
/// card whatever the order, and the second run joins the first.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
public sealed class HeldDetectionRunJoinTests
{
    // Where the scan mode changes from Incremental to Full: 0 before the first run is held, 1 between the
    // two runs, 2 after the second run.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ASecondDetectionRunDuringADownload_JoinsTheHeldRunsCard(int modeChangeAt)
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
        var snapshot = new DownloadSpeedSnapshot();
        CacheScanGateHarness.MakeBusy(snapshot);
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        using var loop = new GameDetectionService(
            detectionService: null!,
            stateService: state,
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: DispatchProxy.Create<IOperationWaitlist, NullReturningProxy>(),
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
                cacheScanGate: CacheScanGateHarness.With(snapshot));
            ScheduledServiceBase.WaitForDownloadAnswer = null;

            if (modeChangeAt == 0) mode = GameDetectionScanMode.Full;
            await schedules.TriggerRunAsync("gameDetection");
            if (modeChangeAt == 1) mode = GameDetectionScanMode.Full;
            await schedules.TriggerRunAsync("gameDetection");
            if (modeChangeAt == 2) mode = GameDetectionScanMode.Full;

            var cards = tracker.GetWaitingOperations()
                .Where(operation => operation.Type == OperationType.GameDetection)
                .ToList();
            Assert.True(cards.Count == 1,
                $"Expected one waiting detection card, found {cards.Count}: "
                + string.Join(", ", cards.Select(card => card.DetectionScanType?.ToString() ?? "none")));
        }
        finally
        {
            ScheduledServiceBase.ScheduleRunGate = previousGate;
            ScheduledServiceBase.WaitForDownloadAnswer = previousWait;
        }
    }
}
