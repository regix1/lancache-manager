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
            var queue = new RecordingOperationQueue();
            var detectionLoop = new GameDetectionService(
                detectionService: null!,
                stateService: DispatchProxy.Create<IStateService, NullReturningProxy>(),
                pathResolver: pathResolver,
                scopeFactory: null!,
                cacheReconciliationService: null!,
                operationQueue: queue,
                logger: NullLogger<GameDetectionService>.Instance,
                configuration: configuration);
            detectionLoop.SetNotificationMode(mode);
            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            tracker.RegisterOperation(
                OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
            var controller = new GamesController(
                gameCacheDetectionService: null!,
                gameDetectionService: detectionLoop,
                cacheManagementService: null!,
                notifications: null!,
                logger: NullLogger<GamesController>.Instance,
                pathResolver: pathResolver,
                operationTracker: tracker,
                conflictChecker: new OperationConflictChecker(
                    tracker, NullLogger<OperationConflictChecker>.Instance),
                operationQueue: queue,
                capabilityService: new DatasourceCapabilityService(datasourceService),
                cacheScanGate: CacheScanGateHarness.Idle());

            var result = await controller.DetectGamesAsync(cancellationToken: CancellationToken.None);

            Assert.IsType<AcceptedResult>(result);
            var notice = Assert.IsType<RunNotice>(queue.Notice);
            Assert.Equal(detectionLoop.EffectiveNotificationMode, notice.Mode);
            Assert.Equal(RunTrigger.Manual, notice.Trigger);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
            operationQueue: CacheScanGateHarness.CreateProxy<IOperationQueue>(
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

    private sealed class RecordingOperationQueue : IOperationQueue
    {
        public RunNotice? Notice { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            bool reportRefusal = false,
            RunNotice? notice = null)
        {
            Notice = notice;
            return Task.FromResult(new QueuedOperationResponse
            {
                OperationId = Guid.NewGuid(),
                Queued = true,
                Status = "waiting"
            });
        }
    }
}
