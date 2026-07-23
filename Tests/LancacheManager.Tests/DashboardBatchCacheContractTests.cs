using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Models.Responses;

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
        CacheSnapshot = new object(),
        CacheGrowth = new object()
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
    [InlineData("cacheGrowth")]
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
            case "cacheGrowth": response.CacheGrowth = null; break;
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
        var source = BatchServiceSource();

        // Allowlist of call sites whose token argument is not covered by the tokenless-idiom
        // sweep below (named arguments, custom helpers, or overloads with other parameters).
        string[] requiredCallSites =
        [
            "await GetEventDownloadIdsAsync(eventIdList, ct)",
            "await EnrichGameNamesAsync(context, downloads, ct);",
            "await q.SumAsync(d => (long?)d.CacheMissBytes, ct) ?? 0L",
            ".CountAsync(d => d.StartTimeUtc >= activeThreshold && d.EndTimeUtc == default, ct)",
            ".ToDictionaryAsync(m => m.DepotId, m => m, ct)",
            ".ToDictionaryAsync(m => m.AppId, m => m.Name, ct)",
            ".ToDictionaryAsync(m => m.ProductId, m => m.Title, ct)",
            "await _cacheSnapshotService.GetSnapshotSummaryAsync(startUtc, endUtc, ct)"
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
        var source = ReadSource("Infrastructure", "Services", "DashboardCacheWarmerService.cs");

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

    private static string BatchServiceSource()
        => ReadSource("Core", "Services", "DashboardBatchService.cs");

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
