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
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

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
        DownloadTotals = new object(),
        FilteredDownloadTotals = new object(),
        ServiceOptions = new object(),
        ClientOptions = new object(),
        RecentDownloads = new object(),
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
    [InlineData("downloadTotals")]
    [InlineData("filteredDownloadTotals")]
    [InlineData("serviceOptions")]
    [InlineData("clientOptions")]
    [InlineData("recentDownloads")]
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
            case "downloadTotals": response.DownloadTotals = null; break;
            case "filteredDownloadTotals": response.FilteredDownloadTotals = null; break;
            case "serviceOptions": response.ServiceOptions = null; break;
            case "clientOptions": response.ClientOptions = null; break;
            case "recentDownloads": response.RecentDownloads = null; break;
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

        Assert.Equal(1, occurrences);

        var removeIndex = source.IndexOf(removeText, StringComparison.Ordinal);
        var finallyIndex = source.LastIndexOf("finally", removeIndex, StringComparison.Ordinal);
        var ifMine = source.LastIndexOf("if (mine)", removeIndex, StringComparison.Ordinal);

        Assert.True(
            finallyIndex >= 0 && ifMine > finallyIndex,
            "the single removal must sit in a finally guarded by ownership, so the caller that created the flight retires it on success, on fault and on its own cancellation, a joiner never removes a flight that is still running for someone else, and the exact stored key+value pair is what gets retired");
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
    public void RecentDownloadsHotPathObservesTheRequestToken()
    {
        var source = BatchServiceSource();
        Assert.True(
            source.Contains("await BuildRecentGroupedQuery(query).ToListAsync(ct);", StringComparison.Ordinal),
            "the aggregate behind the recent section must observe the request token");
        Assert.False(
            source.Contains(".Skip(scanned)", StringComparison.Ordinal),
            "the aggregate must be read once for the range: paging it recomputed the grouping per "
            + "page and left a game with rows outside the window understated");
    }

    /// <summary>
    /// The batch used to ship the whole visible Downloads table to every page that mounts the
    /// provider. Nothing may put it back: the aggregates below exist so that the array does not
    /// have to travel, and a reinstated array would defeat all of them at once.
    /// </summary>
    [Fact]
    public void TheBatchDoesNotShipTheWholeDownloadsTable()
    {
        var source = BatchServiceSource();

        Assert.DoesNotContain("GetLatestDownloadsAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("int.MaxValue", source, StringComparison.Ordinal);
        Assert.Null(typeof(DashboardBatchResponse).GetProperty("Downloads"));
    }

    /// <summary>
    /// A sum written over <c>TotalBytes</c> or <c>CacheHitPercent</c> compiles and then throws at
    /// runtime: both are computed off the entity and neither has a column behind it, so EF cannot
    /// translate them. Every server-side aggregate has to name the two stored columns.
    /// </summary>
    [Fact]
    public void ServerSideAggregatesNameTheStoredByteColumns()
    {
        var source = BatchServiceSource();

        Assert.DoesNotContain("Sum(d => d.TotalBytes)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Max(d => d.TotalBytes)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("d.CacheHitPercent", source, StringComparison.Ordinal);
        Assert.Contains("g.Max(d => d.CacheHitBytes + d.CacheMissBytes)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The totals, the filter options and the recent slice must see the same rows as each other,
    /// which they only do while they all compose the shared visibility filters instead of
    /// restating a predicate of their own.
    /// </summary>
    [Fact]
    public void TheDownloadSectionsShareOneVisibilityQuery()
    {
        var source = BatchServiceSource();

        Assert.Contains("private static IQueryable<Download> BuildVisibleDownloadsQuery(", source, StringComparison.Ordinal);
        Assert.Contains(".ApplyHiddenClientFilter(hiddenClientIps)", source, StringComparison.Ordinal);
        Assert.Contains(".ApplyEmptySessionFilter()", source, StringComparison.Ordinal);

        var sections = new[]
        {
            "GetDownloadTotalsAsync",
            "GetFilteredDownloadTotalsAsync",
            "GetServiceOptionsAsync",
            "GetClientOptionsAsync",
            "GetRecentDownloadsAsync"
        };
        foreach (var section in sections)
        {
            var declaration = source.IndexOf($"private async Task<object> {section}(", StringComparison.Ordinal);
            Assert.True(declaration >= 0, $"missing download sub-query: {section}");
            // Bounded to this section's own body. The five sections run in the order listed, so a
            // search to the end of the file is satisfied for all of them by the last one's call.
            var body = source[declaration..source.IndexOf("\n    }", declaration, StringComparison.Ordinal)];
            Assert.True(
                body.Contains("BuildVisibleDownloadsQuery(context", StringComparison.Ordinal),
                $"{section} must compose the shared visibility query");
        }
    }

    /// <summary>
    /// The bytes and the row count come out of one grouping. Splitting them costs a second round
    /// trip for a figure the first one already had.
    /// </summary>
    [Fact]
    public void TheDownloadTotalsAndTheirCountShareOneRoundTrip()
    {
        var source = BatchServiceSource();

        var declaration = source.IndexOf(
            "private static async Task<DownloadTotals> QueryDownloadTotalsAsync(IQueryable<Download> query, CancellationToken ct)",
            StringComparison.Ordinal);
        var grouping = source.IndexOf(".GroupBy(d => 1)", declaration, StringComparison.Ordinal);
        var count = source.IndexOf("Count = g.Count()", grouping, StringComparison.Ordinal);
        var materialize = source.IndexOf(".FirstOrDefaultAsync(ct)", grouping, StringComparison.Ordinal);

        Assert.True(declaration >= 0, "both totals passes must share one projection");
        Assert.True(grouping > declaration, "the totals must come from a single grouping");
        Assert.True(count > grouping && count < materialize, "the count must sit in the same projection as the bytes");
    }

    /// <summary>
    /// The service dropdown lists every service the log parser wrote, placeholders included: the
    /// aggregate service rows drop <c>localhost</c> and <c>ip-address</c> because no files on disk
    /// can be attributed to them, but a reader still filters the download list by both.
    /// </summary>
    [Fact]
    public void TheServiceOptionsKeepThePlaceholderServices()
    {
        var source = BatchServiceSource();

        var declaration = source.IndexOf("private async Task<object> GetServiceOptionsAsync(", StringComparison.Ordinal);
        var nextSection = source.IndexOf("private async Task<object> GetClientOptionsAsync(", declaration, StringComparison.Ordinal);
        var body = source[declaration..nextSection];

        Assert.DoesNotContain("ApplyPlaceholderServiceFilter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("NormalizeXboxService", body, StringComparison.Ordinal);
        Assert.Contains(".GroupBy(d => d.Service)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prefill that has been running for hours has an old start time and is still active. That
    /// row is what retires the live preview drawn beside it, so the raw array carries every active
    /// row whatever its age; the five-minute window the active COUNT uses would drop it and leave a
    /// duplicate row on screen for good. Beside them rides a short tail of freshly finished rows,
    /// without which a download that completed between two polls vanishes with its preview still
    /// drawn.
    /// </summary>
    [Fact]
    public void TheRecentSliceCarriesEveryActiveRowWhateverItsAge()
    {
        var source = BatchServiceSource();

        var declaration = source.IndexOf("private async Task<object> GetRecentDownloadsAsync(", StringComparison.Ordinal);
        var nextSection = source.IndexOf("private static IQueryable<DashboardGroupRow> BuildRecentGroupedQuery", declaration, StringComparison.Ordinal);
        var body = source[declaration..nextSection];

        Assert.Contains("d.IsActive || d.EndTimeUtc >= finishedCutoff", body, StringComparison.Ordinal);
        Assert.Contains("DateTime.UtcNow - _recentFinishedTail", body, StringComparison.Ordinal);
        Assert.DoesNotContain("activeThreshold", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The recent panel's footer is computed after its own dropdowns and the Downloads page header
    /// is computed over everything, and the two pages read one shared response. So the narrowed
    /// totals travel beside the plain ones rather than replacing them: one field cannot be both,
    /// and a single narrowed field would make a dropdown on the Dashboard move a number on the
    /// Downloads page. The dropdown contents stay unfiltered too, otherwise choosing one service
    /// would empty the list it was chosen from.
    /// </summary>
    [Fact]
    public void OnlyTheFilteredTotalsAndTheSliceTakeTheDownloadFilters()
    {
        var source = BatchServiceSource();

        Assert.Contains(
            "private static IQueryable<Download> ApplyDownloadFilters(IQueryable<Download> query, string? service, string? client)",
            source,
            StringComparison.Ordinal);

        foreach (var section in new[] { "GetFilteredDownloadTotalsAsync", "GetRecentDownloadsAsync" })
        {
            var declaration = source.IndexOf($"private async Task<object> {section}(", StringComparison.Ordinal);
            Assert.True(declaration >= 0, $"missing download sub-query: {section}");
            var body = source[declaration..source.IndexOf("\n    }", declaration, StringComparison.Ordinal)];
            Assert.True(
                body.Contains("ApplyDownloadFilters(", StringComparison.Ordinal),
                $"{section} must honor the service and client filters");
        }

        foreach (var section in new[] { "GetDownloadTotalsAsync", "GetServiceOptionsAsync", "GetClientOptionsAsync" })
        {
            var declaration = source.IndexOf($"private async Task<object> {section}(", StringComparison.Ordinal);
            var body = source[declaration..source.IndexOf("\n    }", declaration, StringComparison.Ordinal)];
            Assert.DoesNotContain("ApplyDownloadFilters(", body, StringComparison.Ordinal);
            Assert.DoesNotContain("string? service", body, StringComparison.Ordinal);
        }

        // The filters change the body, so they have to change the key the body is stored under.
        Assert.Contains(":{includeClientHostnames}:{service}:{client}", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A client dropdown entry can name a group, which stands for several addresses. Matching one
    /// address would make every group selection return nothing at all, so the value arrives as a
    /// list and the query tests membership.
    /// </summary>
    [Fact]
    public void TheClientFilterMatchesEveryAddressAGroupCovers()
    {
        var source = BatchServiceSource();

        Assert.Contains("clientIps.Contains(d.ClientIp)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("d.ClientIp == client", source, StringComparison.Ordinal);
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
            "await GameNameResolver.ResolveAsync(context, [.. groupRows, .. rows, .. activeGroupRows], ct);",
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

    /// <summary>
    /// Hiding a client, or excluding one from stats only, changes the clients, services, dashboard,
    /// sparklines and hourly sections. Saving either list raises the live generation, and a ranged
    /// entry's key does not carry that generation, so the two lists have to be in the key itself or
    /// a reader on a fixed window keeps the pre-change body for the rest of that entry's window.
    /// </summary>
    [Fact]
    public void TheCacheKeyCarriesTheClientVisibilityLists()
    {
        var source = BatchServiceSource();

        Assert.Contains(
            "{string.Join(\",\", hiddenClientIps)}|{string.Join(\",\", statsExcludedOnlyIps)}",
            source,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The detection section is the one sub-query that queues on a shared lock, and the load in
    /// front of it can run for seconds. A batch whose caller has gone away has to be able to leave
    /// that queue rather than wait for someone else's load and then run its own.
    /// </summary>
    [Fact]
    public void TheDetectionSubQueryObservesTheRequestToken()
    {
        var batchSource = BatchServiceSource();
        var detectionSource = ReadSource("Core", "Services", "Detection", "GameCacheDetectionService.cs");

        Assert.Contains("GetCachedDetectionAsync(actualCacheSize, ct)", batchSource, StringComparison.Ordinal);
        Assert.Contains("_gameCacheDetectionService.GetCachedDetectionAsync(ct)", batchSource, StringComparison.Ordinal);
        Assert.Contains("_detectionCacheLock.WaitAsync(cancellationToken)", detectionSource, StringComparison.Ordinal);
        Assert.Contains(
            "LoadDetectionAsync(cancellationToken, includeCacheFilePaths: false)",
            detectionSource,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The retro endpoint resolves game names under HttpContext.RequestAborted, so a reader that
    /// navigates away cancels it. Reporting that as an empty page hands the client a success with
    /// no rows in it; the cancellation belongs to the middleware, which answers 499 for it.
    /// </summary>
    [Fact]
    public void TheRetroEndpointDoesNotReportACanceledRequestAsAnEmptyPage()
    {
        var source = ReadSource("Controllers", "Downloads", "DownloadsController.cs");

        var logIndex = source.IndexOf("Error getting retro downloads", StringComparison.Ordinal);
        Assert.True(logIndex > 0, "the retro handler no longer logs under this message");

        var catchIndex = source.LastIndexOf("catch (Exception ex)", logIndex, StringComparison.Ordinal);
        Assert.True(catchIndex > 0, "the retro handler no longer catches Exception");

        Assert.StartsWith(
            "catch (Exception ex) when (ex is not OperationCanceledException)",
            source[catchIndex..],
            StringComparison.Ordinal);
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
    /// paid on a schedule anyway.
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
    /// rename to "Etc/UTC" on the reader's side alone would split the two keys.
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

    /// <summary>
    /// A service whose downloads only ever cached metadata is still selectable, it just sits below
    /// the dropdown's own divider. That split needs a per-service maximum, and the name it is
    /// reported under stays raw so the client can fold the aliases itself.
    /// </summary>
    [Fact]
    public async Task ServiceOptionsMarkOnlyTheServicesThatCachedALargeDownload()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"service-options-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NewDownload("steam", "10.0.0.1", DateTime.UtcNow.AddMinutes(-10), cacheHitBytes: 500 * 1024));
            seed.Downloads.Add(NewDownload("xboxlive", "10.0.0.2", DateTime.UtcNow.AddMinutes(-10), cacheHitBytes: 2 * 1024 * 1024));
            await seed.SaveChangesAsync();
        }

        var serviceOptions = Assert.IsType<List<ServiceFilterOption>>(
            await InvokeSubQuery(BatchServiceOver(options), "GetServiceOptionsAsync",
                [null, null, new List<long>(), null, new List<string>(), "show", CancellationToken.None]));

        Assert.False(serviceOptions.Single(o => o.Service == "steam").HasLargeFiles);
        Assert.True(serviceOptions.Single(o => o.Service == "xboxlive").HasLargeFiles);
    }

    /// <summary>
    /// The row of a download that has been running for hours is older than every completed row
    /// beside it, and it is the row whose advancing byte count retires the live preview drawn over
    /// it. Dropping it out of the slice leaves that preview beside the recorded row for good.
    /// </summary>
    [Fact]
    public async Task TheRecentSliceKeepsAnActiveRowOlderThanEveryCompletedRow()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"recent-slice-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NewDownload("steam", "10.0.0.1", DateTime.UtcNow.AddHours(-6), cacheHitBytes: 4096, isActive: true));
            for (var minute = 1; minute <= 5; minute++)
            {
                seed.Downloads.Add(NewDownload("steam", "10.0.0.2", DateTime.UtcNow.AddMinutes(-minute), cacheHitBytes: 4096));
            }
            await seed.SaveChangesAsync();
        }

        var section = await InvokeSubQuery(BatchServiceOver(options), "GetRecentDownloadsAsync",
            [null, null, new List<long>(), null, new List<string>(), "show", null, null, CancellationToken.None]);
        var rows = (List<DashboardBatchService.DashboardDownloadRow>)section
            .GetType().GetProperty("rows")!.GetValue(section)!;

        Assert.Single(rows, row => row.IsActive);
        // The array is the active rows plus the ones that finished in the last thirty seconds, so
        // the newest completed row rides along and the four older ones do not.
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// The recent panel's footer is summed after its client dropdown, so a selected client has to
    /// narrow those totals. The Downloads page header sums everything and sits on another page
    /// reading the same response, so the plain totals must stay whole while that happens: a reader
    /// changing a dropdown on the Dashboard cannot be allowed to move a number on the Downloads
    /// page.
    /// </summary>
    [Fact]
    public async Task TheFilteredTotalsFollowTheSelectedClientAndThePlainTotalsDoNot()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"download-totals-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NewDownload("steam", "10.0.0.1", DateTime.UtcNow.AddMinutes(-10), cacheHitBytes: 100, cacheMissBytes: 20));
            seed.Downloads.Add(NewDownload("steam", "10.0.0.1", DateTime.UtcNow.AddMinutes(-9), cacheHitBytes: 300, cacheMissBytes: 80));
            seed.Downloads.Add(NewDownload("steam", "10.0.0.2", DateTime.UtcNow.AddMinutes(-8), cacheHitBytes: 600, cacheMissBytes: 900));
            await seed.SaveChangesAsync();
        }

        var filtered = Assert.IsType<DownloadTotals>(
            await InvokeSubQuery(BatchServiceOver(options), "GetFilteredDownloadTotalsAsync",
                [null, null, new List<long>(), null, new List<string>(), "show", null, "10.0.0.1", CancellationToken.None]));

        Assert.Equal(400, filtered.CacheHitBytes);
        Assert.Equal(100, filtered.CacheMissBytes);
        Assert.Equal(2, filtered.Count);

        var plain = Assert.IsType<DownloadTotals>(
            await InvokeSubQuery(BatchServiceOver(options), "GetDownloadTotalsAsync",
                [null, null, new List<long>(), null, new List<string>(), "show", CancellationToken.None]));

        Assert.Equal(1000, plain.CacheHitBytes);
        Assert.Equal(1000, plain.CacheMissBytes);
        Assert.Equal(3, plain.Count);
    }

    /// <summary>
    /// A client group covers several addresses and the dropdown offers it as one entry, so the
    /// filter has to admit every member. Matching a single address would return nothing for every
    /// group selection, emptying the panel and zeroing its footer.
    /// </summary>
    [Fact]
    public async Task TheFilteredTotalsCoverEveryAddressInASelectedClientGroup()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"client-group-totals-{Guid.NewGuid():N}")
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(NewDownload("steam", "10.0.0.1", DateTime.UtcNow.AddMinutes(-10), cacheHitBytes: 100));
            seed.Downloads.Add(NewDownload("steam", "10.0.0.2", DateTime.UtcNow.AddMinutes(-9), cacheHitBytes: 300));
            seed.Downloads.Add(NewDownload("steam", "10.0.0.3", DateTime.UtcNow.AddMinutes(-8), cacheHitBytes: 900));
            await seed.SaveChangesAsync();
        }

        var totals = Assert.IsType<DownloadTotals>(
            await InvokeSubQuery(BatchServiceOver(options), "GetFilteredDownloadTotalsAsync",
                [null, null, new List<long>(), null, new List<string>(), "show", null, "10.0.0.1,10.0.0.2", CancellationToken.None]));

        Assert.Equal(400, totals.CacheHitBytes);
        Assert.Equal(2, totals.Count);
    }

    private static Download NewDownload(
        string service,
        string clientIp,
        DateTime startTimeUtc,
        long cacheHitBytes = 0,
        long cacheMissBytes = 0,
        bool isActive = false) => new()
        {
            Service = service,
            ClientIp = clientIp,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = startTimeUtc.AddMinutes(1),
            CacheHitBytes = cacheHitBytes,
            CacheMissBytes = cacheMissBytes,
            IsActive = isActive
        };

    private static DashboardBatchService BatchServiceOver(DbContextOptions<AppDbContext> options)
    {
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        typeof(DashboardBatchService)
            .GetField("_dbContextFactory", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new TopServiceDbContextFactory(options));
        return service;
    }

    private static Task<object> InvokeSubQuery(DashboardBatchService service, string name, object?[] arguments)
        => (Task<object>)typeof(DashboardBatchService)
            .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments)!;

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

    /// <summary>
    /// The eviction handle a request captures has to outlive the build it travels through.
    /// A live batch can take seconds to assemble and an ingest tick invalidates about once a second
    /// while one is running, so the source captured at the start is routinely cancelled and
    /// disposed before the entry options are built at the end. Reading
    /// <see cref="CancellationTokenSource.Token"/> off a disposed source throws
    /// <see cref="ObjectDisposedException"/>, which would turn a slow live request into a 500.
    /// Capturing the token itself sidesteps that: it stays readable, reports itself already
    /// cancelled, and the cache declines the entry, which is the wanted outcome for a response
    /// built under a superseded generation.
    /// </summary>
    [Fact]
    public void AnEvictionTokenCapturedBeforeAnInvalidationDeclinesTheEntryInsteadOfThrowing()
    {
        var service = (DashboardBatchService)RuntimeHelpers.GetUninitializedObject(typeof(DashboardBatchService));
        SetEvictionSource(service, "_liveCacheEviction", new CancellationTokenSource());
        SetEvictionSource(service, "_detectionCacheEviction", new CancellationTokenSource());

        // The handle taken at the point GetBatchAsync captures it, before any work starts.
        var capturedToken = EvictionSource(service, "_liveCacheEviction").Token;

        // The invalidation that lands while the build is still running.
        service.InvalidateLiveCache();

        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 500 * 1024 * 1024 });
        var entryOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))
            .SetSize(50_000)
            .SetPriority(CacheItemPriority.High)
            .AddExpirationToken(new CancellationChangeToken(capturedToken));

        cache.Set("dashboard-batch:eviction-capture", new object(), entryOptions);

        Assert.False(cache.TryGetValue("dashboard-batch:eviction-capture", out _));
    }

    /// <summary>
    /// Pins the shape the test above depends on: the request captures the token struct, carries
    /// that through the build, and never reads <c>.Token</c> off a source at the point the entry
    /// options are built.
    /// </summary>
    [Fact]
    public void TheEvictionHandleThatTravelsThroughABuildIsATokenNotItsSource()
    {
        var source = BatchServiceSource();

        Assert.Contains(
            "var liveCacheEviction = Volatile.Read(ref _liveCacheEviction).Token;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "var detectionCacheEviction = Volatile.Read(ref _detectionCacheEviction).Token;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CancellationToken liveCacheEviction, CancellationToken detectionCacheEviction,",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationChangeToken(liveCacheEviction.Token)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CancellationChangeToken(detectionCacheEviction.Token)", source, StringComparison.Ordinal);
    }

    private static CancellationTokenSource EvictionSource(DashboardBatchService service, string fieldName)
        => (CancellationTokenSource)typeof(DashboardBatchService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;

    private static void SetEvictionSource(DashboardBatchService service, string fieldName, CancellationTokenSource source)
        => typeof(DashboardBatchService)
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, source);

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
