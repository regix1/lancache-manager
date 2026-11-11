using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class GamesControllerDetectionNoticeTests
{
    [Theory]
    [InlineData(NotificationMode.All)]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Silent)]
    [InlineData(NotificationMode.Hidden)]
    public async Task DetectGames_WhenConflict_QueuesManualNoticeFromScheduleAsync(NotificationMode mode)
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-detection-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));

        try
        {
            var queue = new RecordingOperationWaitlist();
            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            tracker.RegisterOperation(
                OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
            var (controller, detectionLoop) = CreateController(root, queue, tracker);
            detectionLoop.SetNotificationMode(mode);

            var result = await controller.DetectGamesAsync(cancellationToken: CancellationToken.None);

            Assert.IsType<AcceptedResult>(result);
            var notice = Assert.IsType<RunNotice>(queue.Notice);
            Assert.Equal(detectionLoop.EffectiveNotificationMode, notice.Mode);
            Assert.Equal(RunTrigger.Manual, notice.Trigger);
            // The quick scan the page asked for travels on the notice, so a hold keeps it.
            Assert.Equal(DetectionScanType.Incremental, notice.RequestedScanType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The route hands its start to the queue, which checks conflicts and starts under one lock, so a
    /// cache clear sent at the same moment cannot start beside it. The route checks nothing outside the
    /// queue and keeps its two answers: the started scan's id, or the queue's waiting answer.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectGames_HandsItsStartToTheQueueAndKeepsItsAnswerAsync(bool waiting)
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-detection-notice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));

        try
        {
            var operationId = Guid.NewGuid();
            var response = new OperationStartResponse
            {
                OperationId = operationId,
                Status = waiting ? OperationStartStatus.Waiting : OperationStartStatus.Started
            };
            var queue = new RecordingOperationWaitlist { Response = response };
            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            var (controller, _) = CreateController(root, queue, tracker);

            var result = await controller.DetectGamesAsync(forceRefresh: true, cancellationToken: CancellationToken.None);

            var accepted = Assert.IsType<AcceptedResult>(result);
            Assert.Equal(OperationType.GameDetection, queue.Type);
            Assert.Equal(ConflictScope.Bulk(), queue.Scope);
            Assert.Equal(DetectionScanType.Full, queue.ScanType);
            Assert.Equal(DetectionScanType.Full, Assert.IsType<RunNotice>(queue.Notice).RequestedScanType);
            if (waiting)
            {
                Assert.Same(response, accepted.Value);
            }
            else
            {
                var started = Assert.IsType<GameDetectionStartResponse>(accepted.Value);
                Assert.Equal(operationId, started.OperationId);
                Assert.Equal(OperationStatus.Running, started.Status);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (GamesController Controller, GameDetectionService DetectionLoop) CreateController(
        string root, RecordingOperationWaitlist queue, UnifiedOperationTracker tracker)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = "cache",
                ["LanCache:DataSources:0:LogPath"] = "logs",
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = root;
        var datasourceService = new DatasourceService(
            configuration, pathResolver, NullLogger<DatasourceService>.Instance);
        var detectionLoop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: pathResolver,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: queue,
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: configuration);
        var controller = new GamesController(
            gameCacheDetectionService: null!,
            gameDetectionService: detectionLoop,
            cacheManagementService: null!,
            notifications: null!,
            logger: NullLogger<GamesController>.Instance,
            pathResolver: pathResolver,
            operationTracker: tracker,
            operationQueue: queue,
            capabilityService: new DatasourceCapabilityService(datasourceService),
            cacheScanGate: CacheScanGateHarness.Idle());
        return (controller, detectionLoop);
    }

    [Theory]
    [InlineData(RunTrigger.Manual, true)]
    [InlineData(RunTrigger.Scheduled, false)]
    [InlineData(RunTrigger.RunAll, false)]
    public async Task ARefusedRunNowReachesTheScheduleAndAnAutomaticRefusalIsOnlyLogged(RunTrigger trigger, bool reachesSchedule)
    {
        var detectionLoop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>(
                (_, _) => throw new InvalidOperationException("capability refused")),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        detectionLoop.SelectRunNotice(new RunNotice(NotificationMode.All, trigger));

        var run = (Task)typeof(GameDetectionService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(detectionLoop, [CancellationToken.None])!;

        // The schedule's run step turns a thrown run into its kept failure card.
        if (reachesSchedule) await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        else await run;
    }

    // A Run Now the gate let through, then refused by a download that began before the detection started:
    // the queue has recorded the decline and the schedule holds the run, so the run step ends without an
    // error, and the schedule raises no failure card for it whichever of the run's end and the hold comes first.
    [Fact]
    public async Task ARunNowRefusedByADownloadThatStartedAfterTheGate_IsNotAFailure()
    {
        var detectionLoop = new GameDetectionService(
            detectionService: null!,
            stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
            pathResolver: null!,
            scopeFactory: null!,
            cacheReconciliationService: null!,
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationWaitlist>(
                (_, _) => throw new DownloadInProgressException(
                    "A download is in progress", "errors.unmappedRemoval.downloadInProgress")),
            logger: NullLogger<GameDetectionService>.Instance,
            configuration: new ConfigurationBuilder().Build());
        detectionLoop.SelectRunNotice(new RunNotice(NotificationMode.All, RunTrigger.Manual));

        var run = (Task)typeof(GameDetectionService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic, [typeof(CancellationToken)])!
            .Invoke(detectionLoop, [CancellationToken.None])!;

        await run;
    }

    private sealed class RecordingOperationWaitlist : IOperationWaitlist
    {
        public RunNotice? Notice { get; private set; }
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public DetectionScanType? ScanType { get; private set; }

        public OperationStartResponse Response { get; init; } = new()
        {
            OperationId = Guid.NewGuid(),
            Status = OperationStartStatus.Waiting
        };

        public Task<OperationStartResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            OperationRequestKey requestKey,
            bool reportRefusal = false,
            RunNotice? notice = null,
            string? target = null)
        {
            Notice = notice;
            Type = type;
            Scope = scope;
            ScanType = (requestKey as GameDetectionRequestKey)?.ScanType;
            return Task.FromResult(Response);
        }

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type, ConflictScope scope, Func<Task<Guid?>> start, CancellationToken ct, OperationRequestKey requestKey)
            => throw new NotSupportedException();
    }
}
