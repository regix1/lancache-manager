using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the LINQ shapes the per-game metrics in <c>LancacheMetricsService.UpdateMetricsAsync</c>
/// depend on. Those queries run inside a catch, so a translation failure would be swallowed and the
/// gauges would report nothing on a real install. <c>ToQueryString()</c> compiles each shape
/// through the Npgsql provider without opening a connection, so a regression fails here instead.
///
/// The client-exclusion composition is called on the service itself, so a change to it fails these
/// tests. The grouping shapes are still mirrored, because those queries project into private nested
/// row types; what is asserted of them is the SQL surface each one has to keep - the grouping key,
/// the server-side max of the display name, the ordering, the row limit, and the absence of the
/// cache-path column.
/// </summary>
public class GameMetricsQueryTranslationTests
{
    private const int TopGameCount = 50;

    private static readonly List<string> NoHiddenClients = new();
    private static readonly List<string> NoStatsExcludedClients = new();

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=game_metrics_translation_smoke_test")
            .Options);

    [Fact]
    public void EpicGameTotalsQuery_TranslatesToSql()
    {
        using var context = CreateContext();
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        var sql = LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients)
            .Where(d => d.EpicAppId != null && d.EpicAppId != "")
            .GroupBy(d => d.EpicAppId)
            .Select(g => new
            {
                Service = g.Min(d => d.Service.ToLower()),
                EpicAppId = g.Key,
                GameName = g.Max(d => d.GameName),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Downloads = g.LongCount(),
                ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            })
            .OrderByDescending(x => x.TotalBytes)
            .Take(TopGameCount)
            .ToQueryString();

        AssertGroupedTopGameSql(sql);
    }

    [Fact]
    public void SteamGameTotalsQuery_TranslatesToSql()
    {
        using var context = CreateContext();
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        var sql = LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients)
            .Where(d => (d.EpicAppId == null || d.EpicAppId == "")
                && d.GameAppId != null
                && d.GameAppId != 0)
            .GroupBy(d => d.GameAppId)
            .Select(g => new
            {
                Service = g.Min(d => d.Service.ToLower()),
                GameAppId = g.Key,
                GameName = g.Max(d => d.GameName),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Downloads = g.LongCount(),
                ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            })
            .OrderByDescending(x => x.TotalBytes)
            .Take(TopGameCount)
            .ToQueryString();

        AssertGroupedTopGameSql(sql);
    }

    [Fact]
    public void NamedGameTotalsQuery_TranslatesToSql()
    {
        using var context = CreateContext();
        var fiveMinutesAgo = DateTime.UtcNow.AddMinutes(-5);

        var sql = LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients)
            .Where(d => (d.EpicAppId == null || d.EpicAppId == "")
                && (d.GameAppId == null || d.GameAppId == 0)
                && d.GameName != null
                && d.GameName != "")
            .GroupBy(d => new { Service = d.Service.ToLower(), d.GameName })
            .Select(g => new
            {
                g.Key.Service,
                g.Key.GameName,
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                Downloads = g.LongCount(),
                ActiveCount = g.Count(d => d.IsActive || d.EndTimeUtc >= fiveMinutesAgo)
            })
            .OrderByDescending(x => x.TotalBytes)
            .Take(TopGameCount)
            .ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both exclusion lists have to reach SQL. Honouring only the hidden list passes every build and
    /// test gate while leaving stats-only clients in the endpoint, which is the half-finished state
    /// this asserts against.
    /// </summary>
    [Fact]
    public void DownloadsQueries_ApplyBothExclusionListsInSql()
    {
        using var context = CreateContext();
        var hiddenClientIps = new List<string> { "10.0.0.2" };
        var statsExcludedOnlyIps = new List<string> { "10.0.0.3" };

        var sql = LancacheMetricsService.ExcludedClientsRemoved(context, hiddenClientIps, statsExcludedOnlyIps)
            .GroupBy(d => d.ClientIp)
            .Select(g => new { ClientIp = g.Key, TotalBytes = g.Sum(d => d.CacheHitBytes) })
            .ToQueryString();

        Assert.Contains("10.0.0.2", sql, StringComparison.Ordinal);
        Assert.Contains("10.0.0.3", sql, StringComparison.Ordinal);
        Assert.Contains("ClientIp", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The display name is resolved with a server-side max so it stays OUT of the grouping key.
    /// If it moved into the key, a game recorded under more than one name would split into several
    /// rows, each ranked on its own bytes, and the row limit could drop a genuine top-N game.
    /// </summary>
    [Fact]
    public void SteamGameTotalsQuery_KeepsTheDisplayNameOutOfTheGroupingKey()
    {
        var sql = SteamTotalsGroupingSql();

        Assert.Contains("max(", sql, StringComparison.OrdinalIgnoreCase);

        var groupByIndex = sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(groupByIndex >= 0, "The query no longer groups in SQL.");
        Assert.DoesNotContain("GameName", sql[groupByIndex..], StringComparison.Ordinal);
    }

    /// <summary>
    /// The service is resolved with a server-side min for the same reason the name uses a max: a
    /// Steam app id identifies the game on its own, so an id recorded under two service names must
    /// stay one row. Split into two, each half ranks on part of the bytes and both halves can fall
    /// outside the row limit, which drops a real top-N game from the endpoint entirely.
    /// </summary>
    [Fact]
    public void SteamGameTotalsQuery_KeepsTheServiceOutOfTheGroupingKey()
    {
        var sql = SteamTotalsGroupingSql();

        Assert.Contains("min(", sql, StringComparison.OrdinalIgnoreCase);

        var groupByIndex = sql.IndexOf("GROUP BY", StringComparison.OrdinalIgnoreCase);
        Assert.True(groupByIndex >= 0, "The query no longer groups in SQL.");
        Assert.DoesNotContain("Service", sql[groupByIndex..], StringComparison.Ordinal);
    }

    private static string SteamTotalsGroupingSql()
    {
        using var context = CreateContext();

        return LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients)
            .Where(d => d.GameAppId != null && d.GameAppId != 0)
            .GroupBy(d => d.GameAppId)
            .Select(g => new
            {
                Service = g.Min(d => d.Service.ToLower()),
                GameAppId = g.Key,
                GameName = g.Max(d => d.GameName)
            })
            .ToQueryString();
    }

    /// <summary>
    /// The games-on-disk query must project column by column. Reading the entity would pull
    /// CacheFilePathsJson, which holds every cache path a game matched and is by far the largest
    /// column in the table. It also drops rows carrying no app id, no Epic id and no name: those
    /// all key on the same bucket, which the download side never produces, so they would publish a
    /// game named after its own app id with no partner series.
    /// </summary>
    [Fact]
    public void GamesOnDiskQuery_TranslatesAndOmitsTheCachePathColumn()
    {
        using var context = CreateContext();

        var sql = context.CachedGameDetections
            .Where(g => !g.IsEvicted
                && (g.GameAppId != 0
                    || (g.EpicAppId != null && g.EpicAppId != "")
                    || (g.GameName != null && g.GameName != "")))
            .Select(g => new
            {
                g.GameAppId,
                g.GameName,
                g.Service,
                g.EpicAppId,
                g.TotalSizeBytes,
                g.CacheFilesFound
            })
            .OrderByDescending(x => x.TotalSizeBytes)
            .Take(TopGameCount)
            .ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CacheFilePathsJson", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleUrlsJson", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DetectionSummaryQuery_TranslatesToSql()
    {
        using var context = CreateContext();

        var sql = context.CachedDetectionSummaries
            .Where(s => s.Id == CachedDetectionSummary.SingletonId)
            .Select(s => new
            {
                s.GamesOnDiskBytes,
                s.GamesOnDiskCount,
                s.ComputedAtUtc
            })
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GamesOnDiskBytes", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Steam coverage counts ride on the global totals query instead of scanning Downloads a
    /// second time, so the conditional aggregates have to reach SQL as CASE expressions. Falling
    /// back to client evaluation would pull the whole table into memory on every cycle, which is
    /// the opposite of what folding them in is for. The predicate mirrors
    /// <c>DownloadsController.IsUnmappedSteam</c>: Steam rows with no game name, or whose name is
    /// just the service name echoed back. It must stay scoped to Steam, because WSUS and bare-metal
    /// traffic carries no game name by design and counting it would report a healthy install as
    /// broken. The single group is what keeps this cheap, so it stays <c>GroupBy(_ =&gt; 1)</c>.
    /// </summary>
    [Fact]
    public void GlobalTotalsQuery_TranslatesTheSteamCoverageAggregatesToSql()
    {
        using var context = CreateContext();
        var steamServiceName = "steam";

        var sql = LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Downloads = g.LongCount(),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                HitBytes = g.Sum(d => d.CacheHitBytes),
                MissBytes = g.Sum(d => d.CacheMissBytes),
                MaxSize = g.Max(d => d.CacheHitBytes + d.CacheMissBytes),
                SteamUnknownBytes = g.Sum(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName)
                    ? d.CacheHitBytes + d.CacheMissBytes
                    : 0L),
                SteamUnknownDownloads = g.LongCount(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName))
            })
            .ToQueryString();

        Assert.Contains("CASE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sum(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("count(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lower", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Service", sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// The folded aggregates have to report exactly what the separate coverage query reported, so
    /// both run over the same rows: a mapped Steam download, the two ways a Steam row can be
    /// unmapped, and a WSUS row that carries no name by design and must stay out of the count.
    /// </summary>
    [Fact]
    public async Task GlobalTotalsQuery_CountsTheSameSteamCoverageAsASeparateQuery()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();

        context.Downloads.AddRange(
            CoverageDownload(1, "steam", "Counter-Strike 2", 1000),
            CoverageDownload(2, "steam", null, 500),
            CoverageDownload(3, "steam", "steam", 250),
            CoverageDownload(4, "wsus", null, 4000));
        context.SaveChanges();

        var steamServiceName = "steam";
        var downloads = LancacheMetricsService.ExcludedClientsRemoved(context, NoHiddenClients, NoStatsExcludedClients);

        var folded = downloads
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Downloads = g.LongCount(),
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                SteamUnknownBytes = g.Sum(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName)
                    ? d.CacheHitBytes + d.CacheMissBytes
                    : 0L),
                SteamUnknownDownloads = g.LongCount(d => d.Service.ToLower() == steamServiceName
                    && (d.GameName == null
                        || d.GameName == ""
                        || d.GameName.ToLower() == steamServiceName))
            })
            .Single();

        var separate = downloads
            .Where(d => d.Service.ToLower() == steamServiceName
                && (d.GameName == null
                    || d.GameName == ""
                    || d.GameName.ToLower() == steamServiceName))
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalBytes = g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                Downloads = g.LongCount()
            })
            .Single();

        Assert.Equal(separate.TotalBytes, folded.SteamUnknownBytes);
        Assert.Equal(separate.Downloads, folded.SteamUnknownDownloads);
        Assert.Equal(750, folded.SteamUnknownBytes);
        Assert.Equal(2, folded.SteamUnknownDownloads);

        // The row count and the byte total still cover every service, so folding the Steam-only
        // columns in did not narrow the query they ride on.
        Assert.Equal(4, folded.Downloads);
        Assert.Equal(5750, folded.TotalBytes);
    }

    /// <summary>
    /// End to end against a real database: a download from an excluded client must not reach any
    /// aggregate the endpoint reports. Covers both lists separately, because one list working and
    /// the other silently missing is the defect that looks correct in casual testing.
    /// </summary>
    [Fact]
    public async Task ExcludedClientsAreAbsentFromEveryAggregate()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();

        context.Downloads.AddRange(
            SteamDownload(1, "10.0.0.1", 1000),
            SteamDownload(2, "10.0.0.2", 500),
            SteamDownload(3, "10.0.0.3", 250),
            SteamDownload(4, "10.0.0.4", 100));
        context.SaveChanges();

        var hiddenClientIps = new List<string> { "10.0.0.2" };
        var statsExcludedOnlyIps = new List<string> { "10.0.0.3" };
        var downloads = LancacheMetricsService.ExcludedClientsRemoved(context, hiddenClientIps, statsExcludedOnlyIps);

        // Global total: both excluded clients are gone, so 2 rows and 1100 bytes, not 4 and 1850.
        Assert.Equal(2, downloads.Count());
        Assert.Equal(1100, downloads.Sum(d => d.CacheHitBytes + d.CacheMissBytes));

        // Per-client: only the two clients that are not excluded.
        var clients = downloads
            .GroupBy(d => d.ClientIp)
            .Select(g => g.Key)
            .ToList();
        Assert.Equal(new[] { "10.0.0.1", "10.0.0.4" }, clients.OrderBy(c => c, StringComparer.Ordinal));

        // Per-game: the same 1100, so an excluded client cannot inflate a game's total while the
        // per-client series drops it.
        var gameTotal = downloads
            .Where(d => d.GameAppId != null && d.GameAppId != 0)
            .GroupBy(d => d.GameAppId)
            .Select(g => g.Sum(d => d.CacheHitBytes + d.CacheMissBytes))
            .Single();
        Assert.Equal(1100, gameTotal);
    }

    /// <summary>
    /// Pins the failure mode the previous test exists to catch: applying only the hidden list still
    /// counts stats-only clients. If this ever stops holding, the two lists have been conflated.
    /// </summary>
    [Fact]
    public async Task HiddenListAloneStillCountsStatsOnlyClients()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var context = database.Factory.CreateDbContext();

        context.Downloads.AddRange(
            SteamDownload(1, "10.0.0.1", 1000),
            SteamDownload(2, "10.0.0.2", 500),
            SteamDownload(3, "10.0.0.3", 250));
        context.SaveChanges();

        var hiddenOnly = LancacheMetricsService.ExcludedClientsRemoved(
            context,
            new List<string> { "10.0.0.2" },
            NoStatsExcludedClients);

        Assert.Equal(1250, hiddenOnly.Sum(d => d.CacheHitBytes + d.CacheMissBytes));
        Assert.Contains("10.0.0.3", hiddenOnly.Select(d => d.ClientIp).ToList(), StringComparer.Ordinal);
    }

    private static Download CoverageDownload(long id, string service, string? gameName, long hitBytes)
    {
        var download = SteamDownload(id, "10.0.0.1", hitBytes);
        download.Service = service;
        download.GameName = gameName;
        download.GameAppId = gameName == "Counter-Strike 2" ? 730 : null;
        return download;
    }

    private static Download SteamDownload(long id, string clientIp, long hitBytes)
    {
        var start = DateTime.UtcNow.AddHours(-2);
        return new Download
        {
            Id = id,
            Service = "steam",
            ClientIp = clientIp,
            StartTimeUtc = start,
            EndTimeUtc = start.AddMinutes(10),
            CacheHitBytes = hitBytes,
            CacheMissBytes = 0,
            IsActive = false,
            GameAppId = 730,
            GameName = "Counter-Strike 2",
            Datasource = "default",
            IsEvicted = false
        };
    }

    private static void AssertGroupedTopGameSql(string sql)
    {
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("min(", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
    }
}
