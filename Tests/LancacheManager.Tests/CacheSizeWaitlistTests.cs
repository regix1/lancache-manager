using System.Reflection;
using System.Runtime.CompilerServices;
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

public sealed class CacheSizeWaitlistTests
{
    [Fact]
    public async Task ForceFullScan_AlwaysEntersOperationWaitlistAsync()
    {
        var waitingResponse = new OperationStartResponse
        {
            OperationId = Guid.NewGuid(),
            Status = OperationStartStatus.Waiting
        };
        var queue = new RecordingOperationWaitlist(waitingResponse);

        var result = await CreateController(queue, NotificationMode.All, datasourceService: null!).GetCacheSizeAsync(
            datasource: null,
            force: true,
            CancellationToken.None);

        var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        Assert.Same(waitingResponse, accepted.Value);
        Assert.Equal(OperationType.CacheSizeScan, queue.Type);
        Assert.Equal(ConflictScope.Bulk(), queue.Scope);
        Assert.Equal("Cache File Scan", queue.DisplayName);
        Assert.NotNull(queue.Start);
    }

    // The Storage page's scan button is a person's run of the cache file scan schedule, so it
    // carries that schedule's mode with a manual trigger.
    [Theory]
    [InlineData(NotificationMode.All)]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Silent)]
    [InlineData(NotificationMode.Hidden)]
    public async Task ForceFullScan_CarriesTheScheduleModeWithAManualTrigger(NotificationMode mode)
    {
        var queue = new RecordingOperationWaitlist(new OperationStartResponse { Status = OperationStartStatus.Started });

        await CreateController(queue, mode, datasourceService: null!).GetCacheSizeAsync(datasource: null, force: true, CancellationToken.None);

        var notice = Assert.IsType<RunNotice>(queue.Notice);
        Assert.Equal(mode, notice.Mode);
        Assert.Equal(RunTrigger.Manual, notice.Trigger);
    }

    // A clear hands its start to the queue, which checks and starts under one lock, so a clear and a
    // waiting job sent at the same moment never both start. The route checks nothing itself: the conflict
    // checker, cache service and clearing service are all null here, so a check or a start the route ran
    // on its own would throw.
    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public async Task CacheClear_EntersTheQueueAndParksThereAsync(string? datasourceName)
    {
        var root = CreateDatasourceRoot();
        try
        {
            var waiting = new OperationStartResponse { OperationId = Guid.NewGuid(), Status = OperationStartStatus.Waiting };
            var queue = new RecordingOperationWaitlist(waiting);

            var result = await ClearAsync(CreateController(queue, NotificationMode.All, CreateDatasourceService(root)), datasourceName);

            var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            Assert.Same(waiting, accepted.Value);
            Assert.Equal(OperationType.CacheClearing, queue.Type);
            Assert.Equal(ConflictScope.Bulk(), queue.Scope);
            Assert.Equal(datasourceName, queue.Target);
            Assert.NotNull(queue.Start);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A clear with nothing in its way starts at once and answers with the running operation's id.
    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    public async Task CacheClear_ThatStartsAtOnceAnswersWithTheRunningIdAsync(string? datasourceName)
    {
        var root = CreateDatasourceRoot();
        try
        {
            var operationId = Guid.NewGuid();
            var queue = new RecordingOperationWaitlist(new OperationStartResponse { OperationId = operationId, Status = OperationStartStatus.Started });

            var result = await ClearAsync(CreateController(queue, NotificationMode.All, CreateDatasourceService(root)), datasourceName);

            var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            var response = Assert.IsType<CacheOperationResponse>(accepted.Value);
            Assert.Equal(operationId, response.OperationId);
            Assert.Equal(OperationStatus.Running, response.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A count hands its start to the queue, so it never starts beside a removal of the same files or a
    // clear; the route checks nothing itself (the conflict checker and tracker are null here).
    [Fact]
    public async Task CacheFileCount_EntersTheQueueAndParksThereAsync()
    {
        var root = CreateDatasourceRoot();
        try
        {
            var waiting = new OperationStartResponse { OperationId = Guid.NewGuid(), Status = OperationStartStatus.Waiting };
            var queue = new RecordingOperationWaitlist(waiting);

            var result = await CreateController(queue, NotificationMode.All, CreateDatasourceService(root))
                .StartUnmappedCacheFileCountAsync(CancellationToken.None);

            var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            Assert.Same(waiting, accepted.Value);
            Assert.Equal(OperationType.CacheFileCount, queue.Type);
            Assert.Equal(ConflictScope.Bulk(), queue.Scope);
            Assert.NotNull(queue.Start);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A count with nothing in its way starts at once and answers with the running count's id.
    [Fact]
    public async Task CacheFileCount_ThatStartsAtOnceAnswersWithTheRunningIdAsync()
    {
        var root = CreateDatasourceRoot();
        try
        {
            var operationId = Guid.NewGuid();
            var queue = new RecordingOperationWaitlist(new OperationStartResponse { OperationId = operationId, Status = OperationStartStatus.Started });

            var result = await CreateController(queue, NotificationMode.All, CreateDatasourceService(root))
                .StartUnmappedCacheFileCountAsync(CancellationToken.None);

            var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
            Assert.Equal(202, accepted.StatusCode);
            var response = Assert.IsType<CacheOperationResponse>(accepted.Value);
            Assert.Equal(operationId, response.OperationId);
            Assert.Equal(OperationStatus.Running, response.Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A scan that has not reported its first step has no stage key; the operation's name is display text
    // and never stands in for one.
    [Theory]
    [InlineData(CorruptionDetectionMethod.RepeatedMiss)]
    [InlineData(CorruptionDetectionMethod.Structural)]
    public void CorruptionStatus_BeforeTheFirstStepAnswersNoStageKey(CorruptionDetectionMethod method)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var scanMode = method == CorruptionDetectionMethod.Structural ? StructuralScanMode.Full : (StructuralScanMode?)null;
        var name = CorruptionDetectionService.DetectionOperationName(method, scanMode);
        tracker.RegisterOperation(
            OperationType.CorruptionDetection,
            name,
            new CancellationTokenSource(),
            metadata: new CorruptionDetectionMetrics { DetectionMethod = method, ScanMode = scanMode });
        var detection = (CorruptionDetectionService)RuntimeHelpers.GetUninitializedObject(typeof(CorruptionDetectionService));
        typeof(CorruptionDetectionService)
            .GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(detection, tracker);
        var controller = new CacheController(null!, null!, detection, NullLogger<CacheController>.Instance,
            null!, null!, null!, null!, tracker, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);

        var result = Assert.IsType<OkObjectResult>(controller.GetCorruptionDetectionStatus());

        var status = Assert.IsType<CorruptionDetectionStatusResponse>(result.Value);
        Assert.True(status.IsRunning);
        // The run's message is the tracker's starting key; the route's stage key is the scan's own step.
        Assert.Equal("signalr.generic.starting", status.Message);
        Assert.Null(status.StageKey);
    }

    private static Task<IActionResult> ClearAsync(CacheController controller, string? datasourceName) =>
        datasourceName is null
            ? controller.ClearAllCacheAsync(CancellationToken.None)
            : controller.ClearDatasourceCacheAsync(datasourceName, CancellationToken.None);

    private static string CreateDatasourceRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "cache-queue-routes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        return root;
    }

    /// <summary>One enabled datasource named "default" over writable cache and log folders.</summary>
    private static DatasourceService CreateDatasourceService(string root)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = root;
        return new DatasourceService(configuration, pathResolver, NullLogger<DatasourceService>.Instance);
    }

    private static CacheController CreateController(IOperationWaitlist queue, NotificationMode mode, DatasourceService datasourceService)
    {
        // The button reads only the schedule's effective mode, so the service needs no loop behind it.
        var cacheSizeScan = (CacheSizeScanScheduledService)RuntimeHelpers.GetUninitializedObject(typeof(CacheSizeScanScheduledService));
        cacheSizeScan.SetNotificationMode(mode);
        return new CacheController(
            cacheService: null!,
            cacheClearingService: null!,
            corruptionDetectionService: null!,
            logger: NullLogger<CacheController>.Instance,
            pathResolver: null!,
            notifications: null!,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            operationTracker: null!,
            datasourceService: datasourceService,
            dbContextFactory: null!,
            reconciliationService: null!,
            conflictChecker: null!,
            operationQueue: queue,
            capabilityService: new DatasourceCapabilityService(datasourceService),
            cacheScanGate: CacheScanGateHarness.Idle(),
            cacheSizeScan: cacheSizeScan,
            operationStateService: null!,
            stateService: null!);
    }

    private sealed class RecordingOperationWaitlist(OperationStartResponse response) : IOperationWaitlist
    {
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }
        public RunNotice? Notice { get; private set; }
        public string? Target { get; private set; }

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
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            Notice = notice;
            Target = target;
            return Task.FromResult(response);
        }

        public Task<OperationConflictResponse?> StartUnlessBusyAsync(
            OperationType type,
            ConflictScope scope,
            Func<Task<Guid?>> start,
            CancellationToken ct, OperationRequestKey requestKey) => throw new NotSupportedException();
    }
}
