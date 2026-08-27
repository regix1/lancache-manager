using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
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

        // The confirm button must stay unreachable until the count lands, and closing the dialog
        // must stop a walk that runs for minutes.
        AssertMatches(ConfirmDisabledRegex(), source, "a confirm button disabled until the count lands");
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

        service.PublishCount(new CacheManagementService.CacheFileCountState(first, null, 5));

        // A second confirmation opens and registers its count. `_cacheLock` serializes the counts
        // themselves but not their registration, so this lands while the first dialog may not have
        // polled yet.
        service.PublishCount(new CacheManagementService.CacheFileCountState(second, null, null));

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

        service.PublishCount(new CacheManagementService.CacheFileCountState(finished, null, 12));

        // A long count publishes a tick per percent. Only the first publish of an id may age
        // anything out, or a single big walk would evict every answer queued behind it.
        for (var percent = 1; percent <= 200; percent++)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(
                running,
                new Dictionary<string, object?> { ["n"] = percent },
                null));
        }

        Assert.Equal(12, service.GetCacheFileCount(finished)?.CacheFilesFound);
    }

    [Fact]
    public void RetainedCounts_AreBounded()
    {
        var service = NewServiceForCountState();

        // One more than RetainedCacheFileCounts, which is 8.
        var ids = Enumerable.Range(0, 9).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            service.PublishCount(new CacheManagementService.CacheFileCountState(id, null, 1));
        }

        Assert.Null(service.GetCacheFileCount(ids[0]));
        Assert.NotNull(service.GetCacheFileCount(ids[^1]));
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
    private static CacheManagementService NewServiceForCountState()
    {
        var configuration = new ConfigurationBuilder().Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);

        return new CacheManagementService(
            configuration,
            NullLogger<CacheManagementService>.Instance,
            pathResolver,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            datasourceService,
            DispatchProxy.Create<IStateService, NullReturningProxy>(),
            dbContextFactory: null!,
            gameCacheDetectionService: null!,
            DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>(),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
            DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
            new DatasourceCapabilityService(datasourceService));
    }

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

    [GeneratedRegex(@"confirmDisabled=\{\s*cacheFileCount\s*===\s*null\s*\}")]
    private static partial Regex ConfirmDisabledRegex();
}
