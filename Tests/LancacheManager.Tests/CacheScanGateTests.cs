using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// A cache scan walks a directory tree a client download is still writing into, so every scan
/// entry point asks one shared question first. These tests pin the three behaviors that are easy
/// to get wrong: a dead speed tracker must read as idle rather than blocking scans forever, the
/// ordinary cache-size read must keep answering while a download runs because the Cache card calls
/// it on mount and on reconnect, and an eviction request parked behind another operation must ask
/// again when it is promoted.
/// </summary>
public sealed class CacheScanGateTests
{
    [Fact]
    public void EmptySnapshotReadsAsIdle()
    {
        Assert.Null(Idle().CheckDownloadInProgress());
    }

    [Fact]
    public void ActiveDownloadIsRefusedWithAReason()
    {
        var reason = Downloading().CheckDownloadInProgress();

        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void ScanIsRefusedWhileTheTrackerHasNotReportedYet()
    {
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);
        SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);

        // The snapshot is empty because nothing has been published, not because the cache is quiet.
        Assert.NotNull(GateOver(tracker).CheckDownloadInProgress());
    }

    [Fact]
    public void ScanProceedsOnceTheTrackerHasHadLongEnoughToReport()
    {
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);
        SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow.AddMinutes(-5));

        // A tracker that never starts, or that dies for good, must not block scans for the life
        // of the process.
        Assert.Null(GateOver(tracker).CheckDownloadInProgress());
    }

    [Fact]
    public void ScanIsRefusedAgainAfterAPublishingTrackerDies()
    {
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);
        SetField(tracker, "_unreportedSinceUtc", null);

        // While it is publishing, an empty snapshot is a real answer.
        Assert.Null(GateOver(tracker).CheckDownloadInProgress());

        // The death path empties the snapshot and re-arms the clock, so the same empty snapshot
        // stops counting as an answer.
        SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);
        Assert.NotNull(GateOver(tracker).CheckDownloadInProgress());
    }

    /// <summary>
    /// The tracker writes the snapshot and the no-answer clock together under one lock, so a
    /// reader that takes them in two separate reads can see the clock from before a transition and
    /// the snapshot from after it. Both states the writer alternates between must refuse a scan:
    /// publishing with a download running, and just died with the clock armed. A single null
    /// answer means the reader saw half of each.
    /// </summary>
    [Fact]
    public async Task ScanIsRefusedWhileTheTrackerFlipsBetweenBusyAndDeadAsync()
    {
        var snapshotLock = new object();
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);
        SetField(tracker, "_snapshotLock", snapshotLock);

        var busy = new DownloadSpeedSnapshot();
        MakeBusy(busy);
        var dead = new DownloadSpeedSnapshot { WindowSeconds = 2 };

        // Start in the publishing state, so the reader never sees the empty snapshot the harness
        // builds a tracker with. That one reads as idle for the honest reason: nothing was written.
        SetField(tracker, "_currentSnapshot", busy);

        var gate = GateOver(tracker);
        var stop = new CancellationTokenSource();
        var allowed = false;

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (gate.CheckDownloadInProgress() == null)
                {
                    allowed = true;
                    return;
                }
            }
        });

        var flipper = Task.Run(() =>
        {
            for (var i = 0; i < 20_000 && !allowed; i++)
            {
                // The publishing state: a download is running and the clock is clear.
                lock (snapshotLock)
                {
                    SetField(tracker, "_currentSnapshot", busy);
                    SetField(tracker, "_unreportedSinceUtc", null);
                }

                // The death state: the snapshot is emptied and the clock is armed, exactly as
                // RunTrackerAsync does when the child process exits.
                lock (snapshotLock)
                {
                    SetField(tracker, "_currentSnapshot", dead);
                    SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);
                }
            }
        });

        await flipper;
        await stop.CancelAsync();
        await reader;

        Assert.False(allowed);
    }

    /// <summary>
    /// A startup run asks at the one moment the tracker cannot have answered yet, so the ask waits
    /// for it first. Both directions have to survive that wait: a restart with nothing downloading
    /// runs the startup scan, and a restart during a download still refuses it. Asked without the
    /// wait, both answer "the tracker has not reported yet", which is a refusal either way.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupAsksTheTrackerRatherThanItsSilenceAsync(bool downloading)
    {
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);

        // The state every restart begins in: the clock was armed when the tracker was built and
        // nothing has been published yet, which on its own refuses.
        SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow);
        var gate = GateOver(tracker);
        Assert.NotNull(gate.CheckDownloadInProgress());

        var published = new DownloadSpeedSnapshot();
        if (downloading)
        {
            MakeBusy(published);
        }

        var publish = Task.Run(async () =>
        {
            await Task.Delay(150);
            // Snapshot before clock, the order the tracker writes them in, so a reader that sees
            // the clock cleared can never be looking at the placeholder that preceded it.
            SetField(tracker, "_currentSnapshot", published);
            SetField(tracker, "_unreportedSinceUtc", null);
        });

        await gate.WaitForDownloadAnswerAsync(CancellationToken.None);

        // Asked the instant the wait returns, which is the answer the startup run acts on. Asking
        // before the wait would still be inside the window and would refuse in both directions.
        Assert.Equal(downloading, gate.CheckDownloadInProgress() is not null);

        await publish;
    }

    /// <summary>
    /// The gate stops refusing the moment the no-answer window expires, and nothing the tracker
    /// does marks that moment: no spawn, no parsed line and no death lines up with it. Without an
    /// announcement booked for it the browser keeps the refusal it was last told about, so the
    /// scan buttons stay disabled while the server would accept a scan.
    /// </summary>
    [Fact]
    public async Task ScanBlockedIsAnnouncedWhenTheNoAnswerWindowExpiresAsync()
    {
        var tracker = TrackerWith(new DownloadSpeedSnapshot(), []);

        // The clock was armed long enough ago that the gate now allows a scan, while the refusal
        // the arming announced is still the last thing the browser was told.
        SetField(tracker, "_unreportedSinceUtc", DateTime.UtcNow.AddMinutes(-5));
        SetField(tracker, "_previouslyScanBlocked", true);

        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        SetField(tracker, "_notifications", notifications);

        var gate = GateOver(tracker);
        var previousAnswer = RustSpeedTrackerService.ScanBlockedAnswer;
        var previousDelay = RustSpeedTrackerService.ScanBlockedRecheckDelay;
        try
        {
            // Both are process-wide, so they are pointed at this tracker's gate here rather than
            // relying on the construction above, and put back for whatever else is running.
            RustSpeedTrackerService.ScanBlockedAnswer = gate.CheckDownloadInProgress;
            RustSpeedTrackerService.ScanBlockedRecheckDelay = TimeSpan.Zero;

            await tracker.AnnounceScanBlockedWhenWindowExpiresAsync(CancellationToken.None);
        }
        finally
        {
            RustSpeedTrackerService.ScanBlockedAnswer = previousAnswer;
            RustSpeedTrackerService.ScanBlockedRecheckDelay = previousDelay;
        }

        Assert.Equal(
            [SignalREvents.CacheScanBlockedChanged],
            ((RecordingNotifications)(object)notifications).Sent);
    }

    /// <summary>
    /// The delay handed to the tracker has to outlast the window the gate refuses over. A re-check
    /// landing on the boundary would read the window as still running, announce nothing, and leave
    /// nothing to ask again.
    /// </summary>
    [Fact]
    public void TheRecheckDelayOutlastsTheWindowItWaitsFor()
    {
        var window = (TimeSpan)typeof(CacheScanGate)
            .GetField("_trackerStartupWindow", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        var previousAnswer = RustSpeedTrackerService.ScanBlockedAnswer;
        var previousDelay = RustSpeedTrackerService.ScanBlockedRecheckDelay;
        try
        {
            // Built directly rather than through the harness, which puts the hooks back: what this
            // test reads is the value the real constructor installs.
            _ = new CacheScanGate(
                TrackerWith(new DownloadSpeedSnapshot(), []), NullLogger<CacheScanGate>.Instance);

            Assert.True(RustSpeedTrackerService.ScanBlockedRecheckDelay > window);
        }
        finally
        {
            RustSpeedTrackerService.ScanBlockedAnswer = previousAnswer;
            RustSpeedTrackerService.ScanBlockedRecheckDelay = previousDelay;
        }
    }

    /// <summary>
    /// A download refusal and a configuration failure leave by the same door: a 400 whose body is a
    /// sentence. One has to render as "try again later" and the other as something broken, so the
    /// refusal carries a code and the failure does not. The same code has to appear whether the
    /// refusal was returned by a route or thrown from a service, because both paths exist and the
    /// caller cannot be asked to know which one answered it.
    /// </summary>
    [Fact]
    public async Task TheDownloadRefusalIsCodedOnTheWireAndAConfigurationFailureIsNotAsync()
    {
        // Returned directly, which is what the four scan-start routes do.
        Assert.Equal(
            ErrorResponse.DownloadInProgressCode,
            ApiResponse.DownloadInProgress("A client download is writing to the cache right now.").Code);
        Assert.Null(ApiResponse.Error("Every datasource must use one key scheme").Code);

        // Thrown, which is what the services behind the queue do. That body is built by the
        // middleware from the exception, so the marker has to survive the trip through it.
        Assert.Equal(
            ErrorResponse.DownloadInProgressCode,
            await ErrorBodyCodeAsync(new DownloadInProgressException("A client download is writing")));
        Assert.Null(await ErrorBodyCodeAsync(new ValidationException("Detection method must be repeated_miss")));
    }

    /// <summary>
    /// A refused eviction scan announces the gate's own sentence and sends no stage key beside it.
    /// The reader prefers the sentence, so a key there could never render, and one that can never
    /// render still obliges every locale to carry a translated line nobody sees.
    /// </summary>
    [Fact]
    public async Task RefusedEvictionScanAnnouncesTheReasonAndNoStageKeyAsync()
    {
        var service = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(typeof(CacheReconciliationService));
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var operationTracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();

        SetField(service, "_operationTracker", operationTracker);
        SetField(service, "_notifications", notifications);
        SetEmptyInstance(service, "_evictionScanTerminalStates");

        // The terminal is raised from inside CompleteOperation, so the registration that wires it
        // is what has to be driven here rather than the event being built by hand.
        var register = typeof(CacheReconciliationService).GetMethod(
            "RegisterEvictionScanOperation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operationId = (Guid)register.Invoke(
            service, ["Eviction Scan", new CancellationTokenSource(), false])!;

        const string reason = "A client download is writing to the cache right now.";
        operationTracker.CompleteOperation(operationId, success: true, error: reason, skipped: true);

        var terminal = Assert.IsType<EvictionScanComplete>(
            await ((RecordingNotifications)(object)notifications).FirstPayload.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(terminal.Skipped);
        Assert.Equal(reason, terminal.Error);
        Assert.Null(terminal.StageKey);
    }

    /// <summary>
    /// Runs one exception through the middleware that builds every thrown error body, and returns
    /// the code it wrote, or null when it wrote none.
    /// </summary>
    private static async Task<string?> ErrorBodyCodeAsync(Exception thrown)
    {
        Task Throw(HttpContext context) => throw thrown;

        var middleware = new GlobalExceptionMiddleware(
            Throw,
            NullLogger<GlobalExceptionMiddleware>.Instance,
            DispatchProxy.Create<IHostEnvironment, NullReturningProxy>());

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.Body.Position = 0;
        using var body = await System.Text.Json.JsonDocument.ParseAsync(httpContext.Response.Body);
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    [Fact]
    public void RefusedEvictionScanTellsTheBrowserItWasSkipped()
    {
        var payload = new EvictionScanComplete(
            Success: true,
            OperationId: Guid.NewGuid(),
            // Built the way the service builds it: the reason below is what the card renders, so
            // there is no stage key beside it.
            StageKey: null,
            Processed: 0,
            Evicted: 0,
            UnEvicted: 0,
            Error: "A client download is writing to the cache right now.",
            Skipped: true);

        var json = System.Text.Json.JsonSerializer.Serialize(
            payload, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        // Without both of these the card reads the run as a completed scan that found nothing.
        Assert.Contains("\"status\":\"skipped\"", json);
        Assert.Contains("\"skipped\":true", json);
    }

    [Fact]
    public void ScanBlockedRouteAnswersFromTheSameGateTheScanRoutesUse()
    {
        var snapshot = new DownloadSpeedSnapshot();
        var controller = (SpeedsController)RuntimeHelpers.GetUninitializedObject(typeof(SpeedsController));
        SetField(controller, "_cacheScanGate", With(snapshot));

        var idle = Assert.IsType<CacheScanBlockedResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCacheScanBlocked().Result).Value);
        Assert.False(idle.Blocked);
        Assert.Null(idle.Reason);

        // A hidden client's download must block the buttons, which is the whole point of reading
        // this route rather than the client-visible speeds.
        MakeBusy(snapshot);
        var busy = Assert.IsType<CacheScanBlockedResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCacheScanBlocked().Result).Value);
        Assert.True(busy.Blocked);
        Assert.False(string.IsNullOrWhiteSpace(busy.Reason));
    }

    [Fact]
    public void HidingAClientDoesNotHideItsDownloadFromTheGate()
    {
        var snapshot = new DownloadSpeedSnapshot();
        MakeBusy(snapshot);
        var tracker = TrackerWith(snapshot, ["10.0.0.5"]);

        // The dashboard projection drops the hidden client and recounts, so it reports quiet.
        Assert.False(tracker.GetCurrentSnapshot().HasActiveDownloads);

        // The gate must not, because those bytes are still being written to the cache.
        Assert.NotNull(GateOver(tracker).CheckDownloadInProgress());
    }

    [Fact]
    public async Task CacheSizeReadWithoutForceStillAnswersDuringADownloadAsync()
    {
        var cacheService = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        SetField(cacheService, "_logger", NullLogger<CacheManagementService>.Instance);
        SetField(cacheService, "_cachedScanFilePath", Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json"));

        var controller = (CacheController)RuntimeHelpers.GetUninitializedObject(typeof(CacheController));
        SetField(controller, "_cacheService", cacheService);
        SetField(controller, "_cacheScanGate", Downloading());
        SetField(controller, "_operationTracker", OperationTrackerWithNothingRunning());

        var result = await controller.GetCacheSizeAsync(datasource: null, force: false, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.IsType<CacheSizeUnavailableResponse>(ok.Value);
    }

    [Fact]
    public async Task PromotedEvictionScanRechecksTheDownloadStateAsync()
    {
        var snapshot = new DownloadSpeedSnapshot();
        var reconciliationService = ReconciliationServiceWith(With(snapshot));

        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        (bool Success, string? Error)? promotedOutcome = null;
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Guid?> StartScanAsync()
        {
            promotedOutcome = await RunReconcileAsync(reconciliationService);
            promoted.TrySetResult();
            return Guid.NewGuid();
        }

        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan, ConflictScope.Bulk(), "Eviction Scan", StartScanAsync, CancellationToken.None);
        Assert.True(queued.Queued);

        // Nothing was downloading when the request was accepted. The download starts while the
        // request sits in the queue, which is the whole point of asking again at promotion.
        MakeBusy(snapshot);

        tracker.CompleteOperation(blockerId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(promotedOutcome);
        Assert.False(promotedOutcome!.Value.Success);
        Assert.False(string.IsNullOrWhiteSpace(promotedOutcome.Value.Error));
    }

    [Fact]
    public async Task PromotedCacheSizeScanRechecksTheDownloadStateAsync()
    {
        var snapshot = new DownloadSpeedSnapshot();
        var cacheService = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(typeof(CacheManagementService));
        SetField(cacheService, "_logger", NullLogger<CacheManagementService>.Instance);
        SetField(cacheService, "_cacheScanGate", With(snapshot));

        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.GameDetection, "Game Detection", new CancellationTokenSource());

        Exception? promotionError = null;
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Guid?> StartScanAsync()
        {
            try
            {
                return await cacheService.StartCacheSizeScanInBackgroundAsync();
            }
            catch (Exception ex)
            {
                promotionError = ex;
                throw;
            }
            finally
            {
                promoted.TrySetResult();
            }
        }

        var queued = await queue.EnqueueAsync(
            OperationType.CacheSizeScan, ConflictScope.Bulk(), "Cache File Scan", StartScanAsync, CancellationToken.None);
        Assert.True(queued.Queued);

        // Nothing was downloading when Refresh cache size was accepted. The download starts while
        // the request sits in the queue, so only a re-check at promotion can catch it.
        MakeBusy(snapshot);

        tracker.CompleteOperation(blockerId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The distinct type is what lets the queue tell a refusal apart from the other stated
        // preconditions that throw the ValidationException base type.
        var refusal = Assert.IsType<DownloadInProgressException>(promotionError);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Message));
        Assert.IsAssignableFrom<ValidationException>(refusal);
    }

    [Fact]
    public async Task PromotedGameDetectionRechecksTheDownloadStateAsync()
    {
        var snapshot = new DownloadSpeedSnapshot();
        var detectionService = (GameCacheDetectionService)RuntimeHelpers.GetUninitializedObject(typeof(GameCacheDetectionService));
        SetField(detectionService, "_cacheScanGate", With(snapshot));

        var refusal = await PromoteAndCaptureAsync(
            OperationType.GameDetection,
            "Game Detection",
            snapshot,
            () => detectionService.StartDetectionAsync(incremental: true));

        Assert.IsType<DownloadInProgressException>(refusal);
    }

    [Fact]
    public async Task PromotedCorruptionDetectionRechecksTheDownloadStateAsync()
    {
        var snapshot = new DownloadSpeedSnapshot();
        var corruptionService = (CorruptionDetectionService)RuntimeHelpers.GetUninitializedObject(typeof(CorruptionDetectionService));
        SetField(corruptionService, "_cacheScanGate", With(snapshot));

        var refusal = await PromoteAndCaptureAsync(
            OperationType.CorruptionDetection,
            "Corruption Detection",
            snapshot,
            async () => (Guid?)await corruptionService.StartDetectionAsync());

        Assert.IsType<DownloadInProgressException>(refusal);
    }

    /// <summary>
    /// A scheduled run refused the moment it tries to start has nobody waiting on a response, so
    /// the announcement is the only thing the reader ever sees. The scheduled callers opt in with
    /// <c>reportRefusal</c>; without it the refusal reaches only the log.
    /// </summary>
    [Fact]
    public async Task RefusedScheduledRunIsAnnouncedAsSkippedAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        // The tracker raises OperationTerminal off the completing stack, so the announcement has to
        // be awaited rather than read straight after the call returns.
        var announced = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation => announced.TrySetResult(operation);

        Task<Guid?> RefuseAsync() => throw new DownloadInProgressException("A download is in progress");

        await Assert.ThrowsAsync<DownloadInProgressException>(() => queue.EnqueueAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            "Eviction Scan",
            RefuseAsync,
            CancellationToken.None,
            reportRefusal: true));

        var skipped = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Eviction Scan", skipped.Name);
        Assert.Equal(OperationStatus.Skipped, skipped.Status);
    }

    /// <summary>
    /// Parks a request behind a live blocker with nothing downloading, starts a download while it
    /// waits, then releases the blocker and returns whatever the promoted delegate threw. This is
    /// the promotion path the queue actually drives, not a direct call to the service.
    /// </summary>
    private static async Task<Exception?> PromoteAndCaptureAsync(
        OperationType type, string name, DownloadSpeedSnapshot snapshot, Func<Task<Guid?>> start)
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.LogProcessing, "Blocker", new CancellationTokenSource());

        Exception? promotionError = null;
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Guid?> StartAsync()
        {
            try
            {
                return await start();
            }
            catch (Exception ex)
            {
                promotionError = ex;
                throw;
            }
            finally
            {
                promoted.TrySetResult();
            }
        }

        var queued = await queue.EnqueueAsync(type, ConflictScope.Bulk(), name, StartAsync, CancellationToken.None);
        Assert.True(queued.Queued);

        MakeBusy(snapshot);
        tracker.CompleteOperation(blockerId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        return promotionError;
    }

    private static CacheReconciliationService ReconciliationServiceWith(CacheScanGate gate)
    {
        var service = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(typeof(CacheReconciliationService));
        SetField(service, "_logger", NullLogger<CacheReconciliationService>.Instance);
        SetField(service, "_cacheScanGate", gate);
        SetEmptyInstance(service, "_evictionScanTerminalStates");
        SetField(service, "_stateService", VisibleClientsStateService());
        return service;
    }

    /// <summary>
    /// The outcome record is private to the service, so the run is driven and read reflectively.
    /// </summary>
    private static async Task<(bool Success, string? Error)> RunReconcileAsync(CacheReconciliationService service)
    {
        var reconcile = typeof(CacheReconciliationService).GetMethod(
            "ReconcileCacheFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var run = (Task)reconcile.Invoke(service, [null, Guid.NewGuid(), CancellationToken.None, false])!;
        await run;

        var outcome = run.GetType().GetProperty("Result")!.GetValue(run)!;
        return (
            (bool)outcome.GetType().GetProperty("Success")!.GetValue(outcome)!,
            (string?)outcome.GetType().GetProperty("Error")!.GetValue(outcome));
    }

    private static IUnifiedOperationTracker OperationTrackerWithNothingRunning()
    {
        var proxy = DispatchProxy.Create<IUnifiedOperationTracker, EmptyOperationTracker>();
        return proxy;
    }

    private class EmptyOperationTracker : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod!.Name == nameof(IUnifiedOperationTracker.GetActiveOperations)
                ? new List<OperationInfo>()
                : null;
    }

    /// <summary>
    /// Records the event names broadcast to every client, so a test can assert on which
    /// announcement was made rather than on a count of calls, and hands back the first payload for
    /// the terminals that are raised off the completing stack rather than returned.
    /// </summary>
    public class RecordingNotifications : DispatchProxy
    {
        private readonly TaskCompletionSource<object?> _firstPayload =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal List<string> Sent { get; } = [];

        internal Task<object?> FirstPayload => _firstPayload.Task;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Sent.Add((string)args![0]!);
            _firstPayload.TrySetResult(args.Length > 1 ? args[1] : null);
            return Task.CompletedTask;
        }
    }
}
