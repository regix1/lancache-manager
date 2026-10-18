using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the contract behind the file count a removal confirmation shows. A row once displayed a
/// count from the last detection scan while the removal reached every URL the entity had ever
/// logged, and nothing connected the two numbers. The count closes that gap, so the field names,
/// the routes, and the operation kind it travels on are all held still here: the backend and the
/// frontend cannot see each other's types, and a silent rename on either side would put a blank
/// where the number belongs.
/// </summary>
public sealed partial class CacheFileCountContractTests
{
    [Fact]
    public void CountStatus_SerializesTheFieldNamesTheFrontendReads()
    {
        var json = JsonSerializer.Serialize(
            new CacheFileCountStatusResponse
            {
                IsProcessing = true,
                OperationId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
                PercentComplete = 42,
                StageKey = "signalr.serviceRemove.counting.progress",
                Context = new Dictionary<string, object?> { ["n"] = 5, ["total"] = 9 }
            },
            NullOmittingWireOptions);

        Assert.Contains("\"isProcessing\":true", json, StringComparison.Ordinal);
        Assert.Contains("\"operationId\":\"11111111-2222-3333-4444-555555555555\"", json, StringComparison.Ordinal);
        Assert.Contains("\"percentComplete\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"stageKey\":\"signalr.serviceRemove.counting.progress\"", json, StringComparison.Ordinal);
        Assert.Contains("\"context\":{\"n\":5,\"total\":9}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CountStatus_ReportsTheNumberOnlyWhenTheCountFinished()
    {
        var running = JsonSerializer.Serialize(
            new CacheFileCountStatusResponse { IsProcessing = true, PercentComplete = 60 },
            NullOmittingWireOptions);
        var finished = JsonSerializer.Serialize(
            new CacheFileCountStatusResponse
            {
                IsProcessing = false,
                PercentComplete = 100,
                CacheFilesFound = 733066
            },
            NullOmittingWireOptions);

        // A number from a walk still in progress is smaller than what the removal reaches, so a
        // running body must carry none at all rather than a partial one.
        Assert.DoesNotContain("cacheFilesFound", running, StringComparison.Ordinal);
        Assert.Contains("\"cacheFilesFound\":733066", finished, StringComparison.Ordinal);
    }

    [Fact]
    public void CountStatus_IsZeroFilesRatherThanNoAnswerWhenNothingIsCached()
    {
        var json = JsonSerializer.Serialize(
            new CacheFileCountStatusResponse
            {
                IsProcessing = false,
                PercentComplete = 100,
                CacheFilesFound = 0
            },
            NullOmittingWireOptions);

        // An entity with nothing on disk still answers. Omitting the field there would look
        // identical to a count that never produced one, and the dialog would refuse to confirm.
        Assert.Contains("\"cacheFilesFound\":0", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StartServiceCacheFileCountAsync", typeof(HttpPostAttribute), "count/service/{name}")]
    [InlineData("StartGameCacheFileCountAsync", typeof(HttpPostAttribute), "count/game/{appId:long}")]
    [InlineData("StartEpicGameCacheFileCountAsync", typeof(HttpPostAttribute), "count/game/epic/{gameName}")]
    [InlineData("StartNamedGameCacheFileCountAsync", typeof(HttpPostAttribute), "count/game/named/{service}/{gameName}")]
    [InlineData("StartUnmappedCacheFileCountAsync", typeof(HttpPostAttribute), "count/unmapped")]
    [InlineData("GetCacheFileCountStatus", typeof(HttpGetAttribute), "count/{operationId:guid}/status")]
    public void CountRoutes_MatchTheUrlsTheFrontendBuilds(string methodName, Type attributeType, string expectedRoute)
    {
        var method = typeof(CacheController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);

        var route = method!.GetCustomAttributes(attributeType, inherit: false)
            .Cast<IRouteTemplateProvider>()
            .Single()
            .Template;

        Assert.Equal(expectedRoute, route);
    }

    [Fact]
    public void CountCalls_TargetTheCountRoutes()
    {
        var source = ReadWebSource("services", "api.service.ts");

        AssertMatches(ServiceCountRouteRegex(), source, "the route that counts a service");
        AssertMatches(GameCountRouteRegex(), source, "the route that counts a Steam game");
        AssertMatches(EpicGameCountRouteRegex(), source, "the route that counts an Epic game");
        AssertMatches(NamedGameCountRouteRegex(), source, "the route that counts a named game");
        AssertMatches(CountStatusRouteRegex(), source, "the route that polls a count");
    }

    [Fact]
    public void CountConfirmation_WaitsForTheCountedNumber()
    {
        var source = ReadWebSource(
            "components", "features", "management", "game-detection", "GameCacheDetector.tsx");

        // The confirm button must stay unreachable while the count is still running, and closing the
        // dialog must stop a walk that runs for minutes. A count that FAILED is a different state:
        // it leaves no number, and refusing on that alone left the dialog saying the count failed
        // with a Remove button that could never be pressed.
        AssertMatches(ConfirmDisabledRegex(), source, "a confirm button disabled only while the count runs");
        Assert.Contains("ApiService.cancelOperation(", source, StringComparison.Ordinal);

        // Both confirmations are counted, not only the service one: a game removal reaches the
        // same stale number through the same modal.
        Assert.Equal(2, ConfirmDisabledRegex().Count(source));
    }

    [Fact]
    public void CountOperation_IsFoundByTheEntityScopeItConflictsOn()
    {
        using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        using var cts = new CancellationTokenSource();

        // The metadata a count endpoint registers. Scope resolution reads EntityKind and only
        // falls back to a kind for the removal operation types, so a count registered without it
        // lands under "bulk" and stops conflicting with the removal it must never run beside.
        var operationId = tracker.RegisterOperation(
            OperationType.CacheFileCount,
            "Cache file count: steam",
            cts,
            new RemovalMetrics { EntityKey = "steam", EntityName = "steam", EntityKind = "service" });

        var found = tracker.GetOperationByScope(OperationType.CacheFileCount, ConflictScope.Service("Steam"));

        Assert.NotNull(found);
        Assert.Equal(operationId, found!.Id);

        // The other half of the reason, so nobody drops EntityKind as redundant: the same
        // registration without it is unreachable by service scope.
        using var unkindedCts = new CancellationTokenSource();
        tracker.RegisterOperation(
            OperationType.CacheFileCount,
            "Cache file count: epicgames",
            unkindedCts,
            new RemovalMetrics { EntityKey = "epicgames", EntityName = "epicgames" });

        Assert.Null(tracker.GetOperationByScope(
            OperationType.CacheFileCount,
            ConflictScope.Service("epicgames")));
    }

    [Fact]
    public void OneCountStarting_DoesNotEraseAnotherCountsAnswer()
    {
        var service = NewServiceForCountState();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        service.PublishCount(new CacheManagementService.CacheFileCountState(first, null, 5, null, null));

        // A second confirmation opens and registers its count. `_cacheLock` serializes the counts
        // themselves but not their registration, so this lands while the first dialog may not have
        // polled yet.
        service.PublishCount(new CacheManagementService.CacheFileCountState(second, null, null, null, null));

        // The first dialog's next poll must still find its answer. Losing it here is not a wrong
        // number, it is the dialog reporting a failed count and refusing to confirm a removal that
        // was counted correctly.
        Assert.Equal(5, service.GetCacheFileCount(first)?.CacheFilesFound);

        var pending = service.GetCacheFileCount(second);
        Assert.NotNull(pending);
        Assert.Null(pending!.CacheFilesFound);
    }

    [Fact]
    public void CountProgressTicks_DoNotAgeOutOtherCountsAnswers()
    {
        var service = NewServiceForCountState();
        var finished = Guid.NewGuid();
        var running = Guid.NewGuid();

        service.PublishCount(new CacheManagementService.CacheFileCountState(finished, null, 12, null, null));

        // A long count publishes a tick per percent. Only the first publish of an id may age
        // anything out, or a single big walk would evict every answer queued behind it.
        for (var percent = 1; percent <= 200; percent++)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(
                running,
                new Dictionary<string, object?> { ["n"] = percent },
                null,
                null,
                null));
        }

        Assert.Equal(12, service.GetCacheFileCount(finished)?.CacheFilesFound);
    }

    [Fact]
    public void AFinishedCount_SurvivesNewerCountsOfOtherGames()
    {
        var service = NewServiceForCountState();
        var unmapped = Guid.NewGuid();
        service.PublishCount(new CacheManagementService.CacheFileCountState(unmapped, null, 4, 400, null));

        // The confirmation for the unmapped count may poll late, after other counts finished.
        for (var game = 0; game < 9; game++)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(
                Guid.NewGuid(), null, 1, null, null));
        }

        Assert.Equal(4, service.GetCacheFileCount(unmapped)?.CacheFilesFound);
    }

    [Fact]
    public void ANewerFinishedCountOfTheSameGame_KeepsTheOlderAnswerUntilItsDialogReadsIt()
    {
        var service = NewServiceForCountState();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();

        service.PublishCount(new CacheManagementService.CacheFileCountState(older, null, 1, null, null));
        service.PublishCount(new CacheManagementService.CacheFileCountState(newer, null, 2, null, null));

        // The first dialog's next poll still gets its number, then stops asking.
        Assert.Equal(1, service.GetCacheFileCount(older)?.CacheFilesFound);
        Assert.Null(service.GetCacheFileCount(older));
        Assert.Equal(2, service.GetCacheFileCount(newer)?.CacheFilesFound);
    }

    [Fact]
    public void AnUnreadAnswer_SurvivesManyNewerCountsOfTheSameGame()
    {
        var service = NewServiceForCountState();
        var counts = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();

        for (var number = 0; number < counts.Count; number++)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(counts[number], null, number, null, null));
        }

        // Every dialog reads its own number once, oldest first, then stops asking.
        for (var number = 0; number < counts.Count; number++)
        {
            Assert.Equal(number, service.GetCacheFileCount(counts[number])?.CacheFilesFound);
            Assert.Null(service.GetCacheFileCount(counts[number]));
        }
    }

    [Fact]
    public void ReadingTheNewestAnswer_DropsOnlyThatAnswer()
    {
        var service = NewServiceForCountState();
        var older = Guid.NewGuid();
        var newest = Guid.NewGuid();
        var otherGame = Guid.NewGuid();
        service.PublishCount(new CacheManagementService.CacheFileCountState(older, null, 1, null, null));
        service.PublishCount(new CacheManagementService.CacheFileCountState(newest, null, 2, null, null));
        service.PublishCount(new CacheManagementService.CacheFileCountState(otherGame, null, 3, null, null));

        Assert.Equal(2, service.GetCacheFileCount(newest)?.CacheFilesFound);
        Assert.Null(service.GetCacheFileCount(newest));

        // A count of the same game after the read replaces nothing the read already dropped.
        service.PublishCount(new CacheManagementService.CacheFileCountState(Guid.NewGuid(), null, 4, null, null));
        Assert.Equal(1, service.GetCacheFileCount(older)?.CacheFilesFound);
        Assert.Equal(3, service.GetCacheFileCount(otherGame)?.CacheFilesFound);
    }

    [Fact]
    public void AnswersNobodyReads_AreDroppedOldestFirstPastTheLimit()
    {
        var service = NewServiceForCountState();
        var counts = Enumerable.Range(0, CacheManagementService.MaxUnreadCounts + 1)
            .Select(_ => Guid.NewGuid())
            .ToList();

        // One more unread answer than the limit holds.
        for (var number = 0; number < counts.Count; number++)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(counts[number], null, 1, null, null));
        }

        Assert.Null(service.GetCacheFileCount(counts[0]));
        Assert.Equal(1, service.GetCacheFileCount(counts[1])?.CacheFilesFound);
        Assert.Equal(1, service.GetCacheFileCount(counts[^1])?.CacheFilesFound);
    }

    [Fact]
    public void AQueuedCountsAnswerReadByEitherId_DropsBothIds()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, LookupHookTracker>();
        var service = NewServiceForCountState(tracker);
        var waiting = Guid.NewGuid();
        var running = Guid.NewGuid();
        ((LookupHookTracker)(object)tracker).OnGetOperation = id => new OperationInfo
        {
            Id = id,
            Type = OperationType.CacheFileCount,
            Name = "Count",
            PreviousOperationId = id == running ? waiting : null
        };

        service.PublishCount(new CacheManagementService.CacheFileCountState(running, null, 7, 700, null));

        // The browser polls the waiting id; the read drops the running id's copy as well.
        Assert.Equal(7, service.GetCacheFileCount(waiting)?.CacheFilesFound);
        Assert.Null(service.GetCacheFileCount(running));

        // The same holds for an answer a newer count replaced, read by the running id.
        var replacedRunning = Guid.NewGuid();
        var replacedWaiting = Guid.NewGuid();
        ((LookupHookTracker)(object)tracker).OnGetOperation = id => new OperationInfo
        {
            Id = id,
            Type = OperationType.CacheFileCount,
            Name = "Count",
            PreviousOperationId = id == replacedRunning ? replacedWaiting : null
        };
        service.PublishCount(new CacheManagementService.CacheFileCountState(replacedRunning, null, 8, 800, null));
        service.PublishCount(new CacheManagementService.CacheFileCountState(Guid.NewGuid(), null, 9, 900, null));
        Assert.Equal(8, service.GetCacheFileCount(replacedRunning)?.CacheFilesFound);
        Assert.Null(service.GetCacheFileCount(replacedWaiting));
    }

    [Fact]
    public void APollThatSeesTheAnswerBeingPublished_StillDropsIt()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, LookupHookTracker>();
        var service = NewServiceForCountState(tracker);
        var count = Guid.NewGuid();
        CacheManagementService.CacheFileCountState? polled = null;
        Thread? poll = null;
        // The tracker is asked for the waiting id after the answer is stored and before it is
        // listed. A poll arriving then sees the answer and must wait for the listing.
        ((LookupHookTracker)(object)tracker).OnGetOperation = id =>
        {
            if (id == count && poll == null)
            {
                poll = new Thread(() => polled = service.GetCacheFileCount(count));
                poll.Start();
                SpinWait.SpinUntil(() => poll.ThreadState is ThreadState.WaitSleepJoin or ThreadState.Stopped);
            }

            return null;
        };

        service.PublishCount(new CacheManagementService.CacheFileCountState(count, null, 5, null, null));
        poll!.Join();

        Assert.Equal(5, polled?.CacheFilesFound);
        Assert.Null(service.GetCacheFileCount(count));
    }

    [Fact]
    public void ACountThatEndedWithoutAnAnswer_IsDroppedWhenTheNextCountStarts()
    {
        // The null tracker knows no operation, which is how a cancelled or failed count looks once
        // its operation is gone.
        var service = NewServiceForCountState();
        var stopped = Guid.NewGuid();

        service.PublishCount(new CacheManagementService.CacheFileCountState(stopped, null, null, null, null));
        service.PublishCount(new CacheManagementService.CacheFileCountState(Guid.NewGuid(), null, null, null, null));

        Assert.Null(service.GetCacheFileCount(stopped));
    }

    [Fact]
    public void AnAnswerPublishedWhileTheNextCountSweeps_IsKept()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, LookupHookTracker>();
        var service = NewServiceForCountState(tracker);
        var first = Guid.NewGuid();
        service.PublishCount(new CacheManagementService.CacheFileCountState(first, null, null, null, null));
        // The first count answers and its operation ends between the sweep reading its progress
        // value and asking the tracker about it.
        ((LookupHookTracker)(object)tracker).OnGetOperation = id =>
        {
            ((LookupHookTracker)(object)tracker).OnGetOperation = null;
            service.PublishCount(new CacheManagementService.CacheFileCountState(id, null, 12, null, null));
            return new OperationInfo
            {
                Id = id,
                Type = OperationType.CacheFileCount,
                Name = "Count",
                CompletedAt = DateTime.UtcNow
            };
        };

        service.PublishCount(new CacheManagementService.CacheFileCountState(Guid.NewGuid(), null, null, null, null));

        Assert.Equal(12, service.GetCacheFileCount(first)?.CacheFilesFound);
    }

    internal class LookupHookTracker : NullReturningProxy
    {
        public Func<Guid, OperationInfo?>? OnGetOperation { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == nameof(IUnifiedOperationTracker.GetOperation) && OnGetOperation is { } onGetOperation
                ? onGetOperation((Guid)args![0]!)
                : base.Invoke(targetMethod, args);
    }

    [Fact]
    public void CountStatus_CarriesFilesBytesAndServices()
    {
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();
        var service = NewServiceForCountState(tracker);
        var operationId = Guid.NewGuid();
        var unmapped = new UnmappedService { Service = "wsus", FileCount = 3, TotalSizeBytes = 4096 };
        service.PublishCount(new CacheManagementService.CacheFileCountState(
            operationId, null, 3, 4096, [unmapped]));

        var response = Status(NewCountController(service, tracker), operationId);

        Assert.False(response.IsProcessing);
        Assert.Equal(3, response.CacheFilesFound);
        Assert.Equal(4096, response.BytesFound);
        Assert.Equal("wsus", Assert.Single(response.UnmappedServices!).Service);
    }

    [Fact]
    public void CountStatus_AnswersAQueuedCountBeforeAndAfterItsRunIsReaped()
    {
        using var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var service = NewServiceForCountState(tracker);
        var controller = NewCountController(service, tracker);
        var metrics = new RemovalMetrics { EntityKey = string.Empty, EntityName = "unmapped files", EntityKind = "bulk" };

        // The queue's order: the waiting count W registers, the promotion registers the running
        // count R, W hands off to R and ends, and W is reaped while R still runs.
        var waiting = tracker.RegisterOperation(
            OperationType.CacheFileCount, "Cache File Count (unmapped files)", new CancellationTokenSource(), metrics);
        Guid running;
        using (tracker.BeginPromotion(waiting, OperationType.CacheFileCount))
        {
            running = tracker.RegisterOperation(
                OperationType.CacheFileCount, "Cache file count: unmapped files", new CancellationTokenSource(), metrics);
        }
        tracker.RecordHandoff(waiting, running);
        tracker.CompleteOperation(waiting, success: true);
        Reap(tracker, waiting);

        tracker.UpdateProgress(running, 40, "signalr.unmappedRemove.counting.starting");
        service.PublishCount(new CacheManagementService.CacheFileCountState(running, null, null, null, null));

        var whileRunning = Status(controller, waiting);
        Assert.True(whileRunning.IsProcessing);
        Assert.Equal(40, whileRunning.PercentComplete);
        Assert.Equal(waiting, whileRunning.OperationId);

        service.PublishCount(new CacheManagementService.CacheFileCountState(running, null, 7, 700, null));
        tracker.CompleteOperation(running, success: true);
        Reap(tracker, running);

        // The tracker has dropped both operations, as it does 10 s after the count ends.
        Assert.Null(tracker.GetOperation(waiting, followHandoff: true));
        var afterReap = Status(controller, waiting);
        Assert.False(afterReap.IsProcessing);
        Assert.Equal(7, afterReap.CacheFilesFound);
        Assert.Equal(waiting, afterReap.OperationId);
    }

    [Fact]
    public void CountOperation_TravelsOnItsOwnKind()
    {
        // Reusing ServiceRemoval or GameRemoval would list a count in
        // GET /api/cache/removals/active and raise a recovery card for a removal that is not
        // running.
        Assert.Equal("cacheFileCount", OperationType.CacheFileCount.ToWireString());
        Assert.Equal(OperationType.CacheFileCount, OperationTypeExtensions.TryParseWire("cacheFileCount"));
        Assert.NotEqual(OperationType.ServiceRemoval, OperationType.CacheFileCount);
        Assert.NotEqual(OperationType.GameRemoval, OperationType.CacheFileCount);
    }

    /// <summary>
    /// A service wired only far enough to exercise the count-state map. Every dependency the count
    /// state does not touch is left null, in the shape SteamGameRemovalCacheSweepTests already uses.
    /// </summary>
    internal static CacheManagementService NewServiceForCountState(
        IUnifiedOperationTracker? tracker = null,
        ISignalRNotificationService? notifications = null,
        IPathResolver? pathResolver = null,
        RustProcessHelper? rustProcessHelper = null,
        GameCacheDetectionService? gameCacheDetectionService = null,
        IStateService? stateService = null,
        CacheScanGate? cacheScanGate = null,
        OperationStateService? operationStateService = null,
        ILogger<CacheManagementService>? logger = null,
        DatasourceService? datasourceService = null,
        RustLogProcessorService? logProcessor = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        pathResolver ??= DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        datasourceService ??= new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);

        return new CacheManagementService(
            configuration,
            logger ?? NullLogger<CacheManagementService>.Instance,
            pathResolver,
            rustProcessHelper: rustProcessHelper!,
            nginxLogRotationService: null!,
            datasourceService,
            stateService ?? DispatchProxy.Create<IStateService, NullReturningProxy>(),
            dbContextFactory: null!,
            gameCacheDetectionService: gameCacheDetectionService!,
            tracker ?? DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>(),
            notifications ?? DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
            new DatasourceCapabilityService(datasourceService),
            cacheScanGate ?? CacheScanGateHarness.Idle(),
            operationStateService ?? (OperationStateService)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(OperationStateService)),
            logProcessor!);
    }

    [Fact]
    public async Task CacheSizeProgressCannotReplaceSuccessorRecoveryContext()
    {
        using var processes = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processes, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = new ScheduledRunReporterTests.CapturingNotificationService();
        var service = NewServiceForCountState(tracker, notifications);
        var owner = typeof(CacheManagementService).GetField("_cacheSizeScanId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var report = typeof(CacheManagementService).GetMethod("ReportProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var progressType = typeof(CacheManagementService).GetNestedType("CacheScanProgress", BindingFlags.NonPublic)!;
        var first = tracker.RegisterOperation(OperationType.CacheSizeScan, "first", new CancellationTokenSource());
        owner.SetValue(service, first);
        await (Task)report.Invoke(service, [first, JsonSerializer.Deserialize(
            """{"stageKey":"signalr.cacheSizeScan.scanning","percentComplete":20,"totalFiles":2}""", progressType)])!;
        Assert.Equal(2L, service.CurrentCacheSizeScanProgressContext!["totalFiles"]);
        tracker.CompleteOperation(first, false, cancelled: true);
        var next = tracker.RegisterOperation(OperationType.CacheSizeScan, "next", new CancellationTokenSource());
        owner.SetValue(service, next);
        await (Task)report.Invoke(service, [next, JsonSerializer.Deserialize(
            """{"stageKey":"signalr.cacheSizeScan.starting","percentComplete":10,"totalFiles":7}""", progressType)])!;
        var current = service.CurrentCacheSizeScanProgressContext;
        var count = notifications.Events.Count;
        await (Task)report.Invoke(service, [first, JsonSerializer.Deserialize(
            """{"stageKey":"signalr.cacheSizeScan.scanning","percentComplete":99,"totalFiles":99}""", progressType)])!;
        Assert.Same(current, service.CurrentCacheSizeScanProgressContext);
        Assert.Equal(7L, current!["totalFiles"]);
        Assert.Equal(count, notifications.Events.Count);
        tracker.CompleteOperation(next, true);
    }

    private static CacheController NewCountController(CacheManagementService service, IUnifiedOperationTracker tracker) =>
        new(
            cacheService: service,
            cacheClearingService: null!,
            corruptionDetectionService: null!,
            logger: NullLogger<CacheController>.Instance,
            pathResolver: null!,
            notifications: null!,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            operationTracker: tracker,
            datasourceService: null!,
            dbContextFactory: null!,
            reconciliationService: null!,
            conflictChecker: null!,
            operationQueue: null!,
            capabilityService: null!,
            cacheScanGate: CacheScanGateHarness.Idle(),
            cacheSizeScan: null!,
            operationStateService: null!);

    private static CacheFileCountStatusResponse Status(CacheController controller, Guid operationId) =>
        Assert.IsType<CacheFileCountStatusResponse>(
            Assert.IsType<OkObjectResult>(controller.GetCacheFileCountStatus(operationId)).Value);

    private static void Reap(UnifiedOperationTracker tracker, Guid operationId) =>
        typeof(UnifiedOperationTracker)
            .GetMethod("ReapOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(tracker, [operationId]);

    /// <summary>
    /// Matches with whitespace tolerance wherever the formatter can rewrap a line, so reformatting
    /// a Web source file cannot fail a backend test.
    /// </summary>
    private static void AssertMatches(Regex pattern, string source, string expected) =>
        Assert.True(
            pattern.IsMatch(source),
            $"The frontend no longer contains {expected}. If it moved or was renamed, this guard "
                + "must be pointed at its new home rather than deleted.");

    private static string ReadWebSource(params string[] pathSegments) =>
        File.ReadAllText(Path.Combine([FindRepositoryRoot(), "Web", "src", .. pathSegments]));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Web")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    private static readonly JsonSerializerOptions NullOmittingWireOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"/cache/count/service/\$\{\s*encodeURIComponent\(\s*serviceName\s*\)\s*\}")]
    private static partial Regex ServiceCountRouteRegex();

    [GeneratedRegex(@"/cache/count/game/\$\{\s*gameAppId\s*\}")]
    private static partial Regex GameCountRouteRegex();

    [GeneratedRegex(@"/cache/count/game/epic/\$\{\s*encodeURIComponent\(\s*gameName\s*\)\s*\}")]
    private static partial Regex EpicGameCountRouteRegex();

    [GeneratedRegex(@"/cache/count/game/named/\$\{\s*encodeURIComponent\(\s*service\s*\)\s*\}/\$\{\s*encodeURIComponent\(\s*gameName\s*\)\s*\}")]
    private static partial Regex NamedGameCountRouteRegex();

    [GeneratedRegex(@"/cache/count/\$\{\s*encodeURIComponent\(\s*operationId\s*\)\s*\}/status")]
    private static partial Regex CountStatusRouteRegex();

    [GeneratedRegex(@"confirmDisabled=\{\s*cacheFileCount\s*===\s*null\s*&&\s*!countFailed\s*\}")]
    private static partial Regex ConfirmDisabledRegex();
}
