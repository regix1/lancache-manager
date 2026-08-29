using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the dashboard batch caching contract: a response with a failed (null) section is
/// never written to the memory cache, concurrent misses for one cache key share a single
/// recompute, cache hits never mutate the shared cached instance, cancellation surfaces as
/// cancellation instead of a soft-nulled section, and every EF sub-query call site forwards
/// the request token.
/// </summary>
public sealed class DashboardBatchCacheContractTests
{
    private static DashboardBatchResponse FullyPopulatedResponse() => new()
    {
        Cache = new CacheInfo(),
        Clients = new object(),
        Services = new object(),
        Dashboard = new object(),
        Downloads = new object(),
        Detection = new object(),
        Sparklines = new object(),
        HourlyActivity = new object(),
        CacheSnapshot = new object()
    };

    [Fact]
    public void HasFailedSection_FalseWhenEverySectionIsPresent()
    {
        Assert.False(DashboardBatchService.HasFailedSection(FullyPopulatedResponse()));
    }

    [Theory]
    [InlineData("cache")]
    [InlineData("clients")]
    [InlineData("services")]
    [InlineData("dashboard")]
    [InlineData("downloads")]
    [InlineData("detection")]
    [InlineData("sparklines")]
    [InlineData("hourlyActivity")]
    [InlineData("cacheSnapshot")]
    public void HasFailedSection_TrueWhenAnySingleSectionIsNull(string section)
    {
        var response = FullyPopulatedResponse();
        switch (section)
        {
            case "cache": response.Cache = null; break;
            case "clients": response.Clients = null; break;
            case "services": response.Services = null; break;
            case "dashboard": response.Dashboard = null; break;
            case "downloads": response.Downloads = null; break;
            case "detection": response.Detection = null; break;
            case "sparklines": response.Sparklines = null; break;
            case "hourlyActivity": response.HourlyActivity = null; break;
            case "cacheSnapshot": response.CacheSnapshot = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(section));
        }

        Assert.True(DashboardBatchService.HasFailedSection(response));
    }

    [Fact]
    public void IsCancellation_TrueForDirectCancellationExceptions()
    {
        Assert.True(DashboardBatchService.IsCancellation(new OperationCanceledException(), CancellationToken.None));
        Assert.True(DashboardBatchService.IsCancellation(new TaskCanceledException(), CancellationToken.None));
    }

    [Fact]
    public void IsCancellation_TrueForCancellationWrappedInAggregateException()
    {
        // The shape a cancelled inner task produces when its Result is read through a
        // continuation instead of being awaited.
        var wrapped = new AggregateException(new InvalidOperationException(), new TaskCanceledException());
        Assert.True(DashboardBatchService.IsCancellation(wrapped, CancellationToken.None));
    }

    [Fact]
    public void IsCancellation_TrueWhenTheRequestTokenIsAlreadyCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.True(DashboardBatchService.IsCancellation(new InvalidOperationException(), cts.Token));
    }

    [Fact]
    public void IsCancellation_FalseForOrdinaryFailuresWithALiveToken()
    {
        Assert.False(DashboardBatchService.IsCancellation(new InvalidOperationException(), CancellationToken.None));
        Assert.False(DashboardBatchService.IsCancellation(new AggregateException(new InvalidOperationException()), CancellationToken.None));
    }

    [Fact]
    public void CacheWriteIsSkippedWhenAnySectionFailed()
    {
        var source = BatchServiceSource();

        var gate = source.IndexOf("if (generationsAreCurrent && !HasFailedSection(response))", StringComparison.Ordinal);
        var set = source.IndexOf("_memoryCache.Set(cacheKey, response, cacheOptions);", StringComparison.Ordinal);

        Assert.True(gate >= 0, "the cache write must be gated on every section having succeeded");
        Assert.True(set > gate, "the cache write must sit inside the failed-section gate");
    }

    [Fact]
    public void ConcurrentMissesForOneKeyShareASingleRecompute()
    {
        var source = BatchServiceSource();

        var loopStart = source.IndexOf("while (true)", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "the miss compute must run inside a retry loop so a follower can rejoin after a faulted flight");

        var lookup = source.IndexOf("_memoryCache.TryGetValue(cacheKey", StringComparison.Ordinal);
        Assert.True(lookup > loopStart, "the cache must be re-checked on every pass through the single-flight loop");

        var myLazyCtor = source.IndexOf("new Lazy<Task<DashboardBatchResponse>>(", StringComparison.Ordinal);
        Assert.True(myLazyCtor > lookup, "the Lazy must be constructed - inertly - before any dictionary lookup, never inside a GetOrAdd value factory");
        Assert.True(
            source.Contains("LazyThreadSafetyMode.ExecutionAndPublication", StringComparison.Ordinal),
            "the Lazy must use ExecutionAndPublication so exactly one thread ever runs the factory and every other caller blocks on the same result");

        var getOrAdd = source.IndexOf("_inflight.GetOrAdd(cacheKey, myLazy)", StringComparison.Ordinal);
        Assert.True(getOrAdd > myLazyCtor, "GetOrAdd must be called with the already-constructed Lazy via the plain-value overload, never a factory delegate that could run more than once");

        var ownershipCheck = source.IndexOf("ReferenceEquals(stored, myLazy)", StringComparison.Ordinal);
        Assert.True(ownershipCheck > getOrAdd, "ownership must be determined deterministically by comparing the stored Lazy against this caller's own, never inferred from a factory side effect");
    }

    [Fact]
    public void SingleFlightCleanupRemovesOnlyTheCompletedFlightsOwnEntry()
    {
        var source = BatchServiceSource();
        const string removeText = "_inflight.TryRemove(new KeyValuePair<string, Lazy<Task<DashboardBatchResponse>>>(cacheKey, stored));";

        var occurrences = 0;
        var searchFrom = 0;
        while (true)
        {
            var idx = source.IndexOf(removeText, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            occurrences++;
            searchFrom = idx + 1;
        }

        Assert.True(
            occurrences >= 2,
            "both the success path and the failure path must retire the exact stored key+value pair they observed, so a newer flight for the same key is never removed early");
    }

    [Fact]
    public void FollowerWithALiveTokenRetriesAfterTheCreatorsFlightIsCancelled()
    {
        var source = BatchServiceSource();

        Assert.True(
            source.Contains("await stored.Value.WaitAsync(ct)", StringComparison.Ordinal),
            "every caller must wait on its own token via WaitAsync instead of awaiting the shared flight directly");

        var ownCancelCatch = source.IndexOf("catch (OperationCanceledException) when (ct.IsCancellationRequested)", StringComparison.Ordinal);
        Assert.True(ownCancelCatch >= 0, "a caller's OWN cancellation must be distinguished from a foreign one and rethrown immediately");

        var ifMine = source.IndexOf("if (mine)", ownCancelCatch, StringComparison.Ordinal);
        Assert.True(ifMine > ownCancelCatch, "the failure branch must distinguish whether this caller owns the failed flight before deciding to rethrow or retry");

        var rethrow = source.IndexOf("throw;", ifMine, StringComparison.Ordinal);
        Assert.True(rethrow > ifMine, "a caller whose OWN fresh flight failed must rethrow directly instead of retrying forever on a repeatable fault");
    }

    [Fact]
    public void SingleFlightLoopTerminatesViaDirectAttemptAfterContentionCap()
    {
        var source = BatchServiceSource();

        Assert.True(
            source.Contains("const int MaxContestedFlightAttempts = 2;", StringComparison.Ordinal),
            "the single-flight loop must cap contested attempts so a caller cannot be handed an unbounded sequence of failing flights under continued contention");

        var capCheck = source.IndexOf("if (attempt >= MaxContestedFlightAttempts)", StringComparison.Ordinal);
        Assert.True(capCheck >= 0, "the loop must check the attempt cap before contending for the shared flight again");

        var directCall = source.IndexOf("return await RunSingleFlightAsync(", capCheck, StringComparison.Ordinal);
        Assert.True(directCall > capCheck, "once the cap is reached the caller must run its own attempt directly and unregistered, bypassing _inflight, so it is guaranteed to terminate");

        var attemptIncrement = source.IndexOf("attempt++;", StringComparison.Ordinal);
        Assert.True(attemptIncrement > capCheck, "every failed contested iteration must advance the attempt counter, and that increment must sit after the cap check so a subsequent pass through the loop observes the updated count");
    }

    [Fact]
    public void CacheHitsNeverMutateTheSharedCachedInstance()
    {
        var source = BatchServiceSource();
        Assert.False(
            source.Contains("cachedResponse.Cache =", StringComparison.Ordinal),
            "a cache hit must refresh Cache on a copy, never by writing into the shared cached instance");
    }

    [Fact]
    public void CancelledSubQueriesSurfaceAsCancellationNotSoftNull()
    {
        var source = BatchServiceSource();

        Assert.True(
            source.Contains("catch (OperationCanceledException)", StringComparison.Ordinal),
            "SafeExecuteAsync must rethrow direct cancellations");
        Assert.True(
            source.Contains("when (IsCancellation(ex, ct))", StringComparison.Ordinal),
            "SafeExecuteAsync must rethrow wrapped cancellations instead of soft-nulling them");
        Assert.False(
            source.Contains(".ContinueWith(t => t.Result", StringComparison.Ordinal),
            "reading Result through a continuation turns a cancellation into an AggregateException");
    }

    [Fact]
    public void LiveDownloadsHotPathObservesTheRequestToken()
    {
        var source = BatchServiceSource();
        Assert.True(
            source.Contains("statsService.GetLatestDownloadsAsync(int.MaxValue, cancellationToken: ct)", StringComparison.Ordinal),
            "the live downloads query is the heaviest sub-query and must observe the request token");
    }

    [Fact]
    public void EverySubQueryCallSiteForwardsTheRequestToken()
    {
        // The name-resolution queries live in the shared GameNameResolver (also used by the
        // retro endpoint), so its source is swept under the same token contract.
        var source = BatchServiceSource() + GameNameResolverSource();

        // Allowlist of call sites whose token argument is not covered by the tokenless-idiom
        // sweep below (named arguments, custom helpers, or overloads with other parameters).
        string[] requiredCallSites =
        [
            "await GetEventDownloadIdsAsync(eventIdList, ct)",
            "await GameNameResolver.ResolveAsync(context, downloads, ct);",
            "await activeQuery.CountAsync(ct)",
            ".Where(m => m.IsOwner && depotIds.Contains(m.DepotId))",
            ".ToListAsync(ct)",
            ".ToDictionaryAsync(m => m.AppId, m => m.Name, ct)",
            ".ToDictionaryAsync(m => m.ProductId, m => m.Title, ct)",
            "await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc, ct)",
            "await ClientStatsAggregationHelper.QueryIpAggregatesAsync(query, ct)"
        ];
        foreach (var callSite in requiredCallSites)
        {
            Assert.True(
                source.Contains(callSite, StringComparison.Ordinal),
                $"expected token-forwarding call site is missing: {callSite}");
        }

        // No EF call may drop the token: a zero-argument overload here is a query that a
        // disconnected client cannot release from the pool.
        string[] forbiddenTokenlessCalls =
        [
            "CreateDbContextAsync()",
            ".ToListAsync()",
            ".CountAsync()",
            ".FirstOrDefaultAsync()"
        ];
        foreach (var tokenlessCall in forbiddenTokenlessCalls)
        {
            Assert.False(
                source.Contains(tokenlessCall, StringComparison.Ordinal),
                $"tokenless EF call found: {tokenlessCall}");
        }
    }

    [Fact]
    public void WarmerRetriesOnceAndReportsPartialWarms()
    {
        var source = ReadSource("Infrastructure", "Services", "Cache", "DashboardCacheWarmerService.cs");

        var firstCheck = source.IndexOf("DashboardBatchService.HasFailedSection(response)", StringComparison.Ordinal);
        Assert.True(firstCheck >= 0, "the warm result must be checked for failed sections");

        var recheck = source.IndexOf("DashboardBatchService.HasFailedSection(response)", firstCheck + 1, StringComparison.Ordinal);
        Assert.True(recheck > firstCheck, "a partial warm must be retried and the retry re-checked");

        Assert.True(
            source.Contains("LogWarning", StringComparison.Ordinal),
            "a partial warm must be logged as a warning");
        Assert.True(
            source.Contains("success: warmedFully", StringComparison.Ordinal),
            "the reported warm success must reflect whether every section was warmed");
    }

    /// <summary>
    /// A warm that names no zone writes a key whose zone segment is empty, while every browser asks
    /// for a key carrying its zone name. That entry is read by nobody and the fan-out behind it is
    /// paid on a schedule anyway. [70]
    /// </summary>
    [Fact]
    public void WarmRequestsTheZoneADefaultReaderSends()
    {
        var source = ReadSource("Infrastructure", "Services", "Cache", "DashboardCacheWarmerService.cs");

        Assert.Contains("ServerTimeZone.IanaId(_configuration)", source, StringComparison.Ordinal);
        Assert.Contains("GetBatchAsync(null, null, null, serverZone,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBatchAsync(null, null, null, null,", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The warm and the reader only share a cache entry if they name the zone with the same string.
    /// /api/system/config answers <see cref="ServerTimeZone.IanaId(IConfiguration)"/>, a reader on the
    /// default server clock sends that id straight back, and the batch puts whatever arrives through
    /// KnownTimeZoneId before keying on it - so that round trip has to return the id unchanged. A
    /// rename to "Etc/UTC" on the reader's side alone would split the two keys. [70]
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("UTC")]
    [InlineData("Europe/Berlin")]
    [InlineData("Eastern Standard Time")]
    public void TheServerZoneSurvivesTheRoundTripIntoTheCacheKey(string? configuredZone)
    {
        var settings = new Dictionary<string, string?>();
        if (configuredZone is not null)
        {
            settings["TZ"] = configuredZone;
        }

        var reported = ServerTimeZone.IanaId(new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build());

        Assert.Equal(reported, DashboardBatchService.KnownTimeZoneId(reported));
    }

    /// <summary>
    /// The hourly queries group on a zone name the database resolves per row, so every reader needs
    /// one. A reader that sends nothing, or a zone this server cannot resolve, gets the server's own
    /// zone rather than an empty cache-key segment and a grouping the reader never asked for.
    /// </summary>
    [Fact]
    public void AReaderWithoutAZoneIsGroupedOnTheServerZone()
    {
        var source = BatchServiceSource();

        Assert.Contains(
            "KnownTimeZoneId(timeZoneId) ?? ServerTimeZone.IanaId(_configuration)",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Locks the fix for the TopService disagreement: <see cref="StatsController"/> used to pick
    /// the top service from its own un-folded group-by (raw "xboxlive"), while
    /// <see cref="DashboardBatchService"/> already read it off the xbox-folded breakdown
    /// (canonical "xbox") - same rows, two different answers. Both must now agree.
    /// </summary>
    [Fact]
    public async Task DashboardStatsTopService_AgreesBetweenControllerAndBatchService()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"topservice-agreement-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.1",
                StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
                EndTimeUtc = DateTime.UtcNow.AddMinutes(-9),
                CacheHitBytes = 100,
                CacheMissBytes = 0,
                IsActive = false
            });
            seed.Downloads.Add(new Download
            {
                Service = "xboxlive",
                ClientIp = "10.0.0.2",
                StartTimeUtc = DateTime.UtcNow.AddMinutes(-10),
                EndTimeUtc = DateTime.UtcNow.AddMinutes(-9),
                CacheHitBytes = 1_000_000,
                CacheMissBytes = 0,
                IsActive = false
            });
            await seed.SaveChangesAsync();
        }

        var batchService =
            (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_dbContextFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(batchService, new TopServiceDbContextFactory(options));

        var getDashboardStats = typeof(DashboardBatchService).GetMethod(
            "GetDashboardStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batchTask = (Task<object>)getDashboardStats.Invoke(
            batchService,
            [null, null, new List<long>(), null, new List<string>(), "show", new List<string>(), CancellationToken.None])!;
        var batchResponse = Assert.IsType<DashboardStatsResponse>(await batchTask);

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        typeof(StatsController)
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, new AppDbContext(options));
        typeof(StatsController)
            .GetField("_stateRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, stateService);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var controllerResult = await controller.DashboardStatsAsync(startTime: null, endTime: null, eventId: null, ct: CancellationToken.None);
        var controllerResponse = Assert.IsType<DashboardStatsResponse>(
            Assert.IsType<OkObjectResult>(controllerResult.Result).Value);

        Assert.Equal("xbox", batchResponse.TopService);
        Assert.Equal(batchResponse.TopService, controllerResponse.TopService);
    }

    /// <summary>
    /// Pins the empty-state string both endpoints answer when the period holds no download rows.
    /// It is a response-body value an API caller can compare against, and the two endpoints reached
    /// it by different routes, so the pair had drifted apart once already (one answered "none").
    /// </summary>
    [Fact]
    public async Task DashboardStatsTopService_IsNotAvailableOnBothEndpointsWhenThereAreNoDownloads()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"topservice-empty-{Guid.NewGuid():N}")
            .Options;

        var batchService =
            (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_dbContextFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(batchService, new TopServiceDbContextFactory(options));

        var getDashboardStats = typeof(DashboardBatchService).GetMethod(
            "GetDashboardStatsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var batchTask = (Task<object>)getDashboardStats.Invoke(
            batchService,
            [null, null, new List<long>(), null, new List<string>(), "show", new List<string>(), CancellationToken.None])!;
        var batchResponse = Assert.IsType<DashboardStatsResponse>(await batchTask);

        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetHiddenClientIps) => new List<string>(),
            nameof(IStateService.GetStatsExcludedOnlyClientIps) => new List<string>(),
            nameof(IStateService.GetEvictedDataMode) => "show",
            _ => DefaultReturn(method.ReturnType)
        });

        var controller = (StatsController)RuntimeHelpers.GetUninitializedObject(typeof(StatsController));
        typeof(StatsController)
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, new AppDbContext(options));
        typeof(StatsController)
            .GetField("_stateRepository", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(controller, stateService);
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var controllerResult = await controller.DashboardStatsAsync(startTime: null, endTime: null, eventId: null, ct: CancellationToken.None);
        var controllerResponse = Assert.IsType<DashboardStatsResponse>(
            Assert.IsType<OkObjectResult>(controllerResult.Result).Value);

        Assert.Equal("N/A", batchResponse.TopService);
        Assert.Equal(batchResponse.TopService, controllerResponse.TopService);
    }

    private sealed class TopServiceDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new AppDbContext(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(options));
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }

    private static string BatchServiceSource()
        => ReadSource("Core", "Services", "Dashboard", "DashboardBatchService.cs");

    private static string GameNameResolverSource()
        => ReadSource("Core", "Services", "Detection", "GameNameResolver.cs");

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        var path = Path.Combine([root, "Api", "LancacheManager", .. pathSegments]);
        return File.ReadAllText(path);
    }
}
