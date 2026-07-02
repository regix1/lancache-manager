using LancacheManager.Core.Constants;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public class RetroQueryTranslationTests
{
    private static Download MakeDownload(string clientIp, string datasource, long? appId, long hit, long miss)
        => new()
        {
            ClientIp = clientIp,
            Datasource = datasource,
            Service = "steam",
            GameAppId = appId,
            DepotId = appId,
            CacheHitBytes = hit,
            CacheMissBytes = miss,
            IsActive = false
        };

    /// <summary>
    /// ApplyPrefillFilter excludes Datasource='prefill' rows when excludePrefill is true (the default
    /// stats behavior) and keeps them when false (the showPrefillTraffic opt-in). This is the switch
    /// the ExcludePrefillTrafficFromStats setting and the retro showPrefillTraffic param flip.
    /// </summary>
    [Fact]
    public void ApplyPrefillFilter_ExcludesPrefillRows_OnlyWhenRequested()
    {
        var rows = new List<Download>
        {
            MakeDownload("10.0.0.5", "default", 730, hit: 100, miss: 0),           // real client, all hit
            MakeDownload("10.0.0.5", DownloadKindConstants.PrefillToken, 730, hit: 0, miss: 100), // prefill, all miss
        }.AsQueryable();

        var excluded = rows.ApplyPrefillFilter(excludePrefill: true).ToList();
        Assert.Single(excluded);
        Assert.All(excluded, d => Assert.NotEqual(DownloadKindConstants.PrefillToken, d.Datasource));

        var included = rows.ApplyPrefillFilter(excludePrefill: false).ToList();
        Assert.Equal(2, included.Count);
    }

    /// <summary>
    /// Repro for the reported "54-57% hit rate after a fresh prefill" symptom: a prefill session's
    /// all-miss traffic and a real install's all-hit traffic share the same appId. With prefill
    /// excluded (default), the group-by-game card is computed from the client rows only and shows the
    /// true 100% hit rate — not the 50% blend. Mirrors DownloadsController.GetRetroDownloadsAsync's
    /// ApplyPrefillFilter + group-by-game merge (the merge lives inline in the controller).
    /// </summary>
    [Fact]
    public void GroupByGame_PrefillExcludedBase_CardShowsRealClientHitPercent()
    {
        var rows = new List<Download>
        {
            MakeDownload("10.0.0.5", "default", 730, hit: 100, miss: 0),
            MakeDownload("172.17.0.4", DownloadKindConstants.PrefillToken, 730, hit: 0, miss: 100),
        }.AsQueryable();

        // Default retro base excludes prefill, then group-by-game merges by appId.
        var visible = rows.ApplyPrefillFilter(excludePrefill: true).ToList();
        var mergedHit = visible.Sum(d => d.CacheHitBytes);
        var mergedTotal = visible.Sum(d => d.CacheHitBytes + d.CacheMissBytes);
        var cardHitPercent = mergedTotal > 0 ? (mergedHit * 100.0) / mergedTotal : 0;

        Assert.Equal(100.0, cardHitPercent, precision: 6);
    }

    /// <summary>
    /// When showPrefillTraffic is on, prefill and real-client rows for the same appId must NOT merge
    /// into one card: the mergeKey prefixes prefill rows so each side keeps its own hit rate and badge.
    /// Mirrors the mergeKey construction in DownloadsController.GetRetroDownloadsAsync.
    /// </summary>
    [Fact]
    public void GroupByGame_PrefillAndClient_SameApp_DoNotMerge()
    {
        var dtos = new List<RetroDownloadDto>
        {
            new() { Service = "steam", SteamAppId = 730, ClientIp = "10.0.0.5", Datasource = "default",
                    CacheHitBytes = 100, CacheMissBytes = 0, TotalBytes = 100 },
            new() { Service = "steam", SteamAppId = 730, ClientIp = "172.17.0.4", Datasource = DownloadKindConstants.PrefillToken,
                    CacheHitBytes = 0, CacheMissBytes = 100, TotalBytes = 100 },
        };

        static string MergeKey(RetroDownloadDto r)
        {
            var key = r.SteamAppId.HasValue && r.SteamAppId.Value != 0
                ? $"{r.Service}-app-{r.SteamAppId.Value}"
                : $"{r.Service}-unknown-0-{r.ClientIp}";
            return r.IsPrefill ? $"prefill-{key}" : key;
        }

        var buckets = dtos.GroupBy(MergeKey).ToList();
        Assert.Equal(2, buckets.Count);

        var realCard = buckets.Single(b => !b.First().IsPrefill);
        var realHit = realCard.Sum(r => r.CacheHitBytes);
        var realTotal = realCard.Sum(r => r.TotalBytes);
        Assert.Equal(100.0, realTotal > 0 ? realHit * 100.0 / realTotal : 0, precision: 6);

        var prefillCard = buckets.Single(b => b.First().IsPrefill);
        Assert.All(prefillCard, r => Assert.True(r.IsPrefill));
        Assert.Equal(0.0, prefillCard.Sum(r => r.CacheHitBytes));
    }

    /// <summary>
    /// Regression test for the SQL GroupBy re-merge bug: BuildRetroBaseQuery's GroupBy key used to be
    /// {DepotId, ClientIp, RowKey} - Datasource was NOT part of it. A prefill-daemon row and a
    /// real-client row that share the same DepotId+ClientIp (possible under host-networking, where the
    /// prefill container shares the host IP - see log_processor.rs's "_prefill" grouping suffix) would
    /// collapse into ONE SQL group: hit/miss bytes summed together and Max(Datasource) picking
    /// "prefill" (alphabetically greater than "default"), mis-badging and polluting the real client's
    /// card. With Datasource in the key (the fix), the two rows aggregate into separate groups.
    /// Mirrors the GroupBy + Select in DownloadsController.GetRetroDownloadsAsync.
    /// </summary>
    [Fact]
    public void RetroGroupBy_PrefillAndRealClient_SameDepotAndClientIp_ProduceSeparateGroups()
    {
        var rows = new List<Download>
        {
            MakeDownload("172.17.0.4", "default", 730, hit: 100, miss: 0),
            MakeDownload("172.17.0.4", DownloadKindConstants.PrefillToken, 730, hit: 0, miss: 100),
        }.AsQueryable();

        // showPrefillTraffic=true keeps both rows in the base query (ApplyPrefillFilter excludePrefill:false).
        var visible = rows.ApplyPrefillFilter(excludePrefill: false);

        var groups = visible
            .GroupBy(d => new { d.DepotId, d.ClientIp, d.Datasource, RowKey = d.DepotId == null ? d.Id : 0L })
            .Select(g => new
            {
                g.Key.DepotId,
                g.Key.ClientIp,
                Datasource = g.Key.Datasource,
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes)
            })
            .ToList();

        Assert.Equal(2, groups.Count);

        var realGroup = groups.Single(g => !string.Equals(g.Datasource, DownloadKindConstants.PrefillToken, StringComparison.OrdinalIgnoreCase));
        var totalBytes = realGroup.CacheHitBytes + realGroup.CacheMissBytes;
        var realHitPercent = totalBytes > 0 ? (realGroup.CacheHitBytes * 100.0) / totalBytes : 0;
        Assert.Equal(100.0, realHitPercent, precision: 6);

        var prefillGroup = groups.Single(g => string.Equals(g.Datasource, DownloadKindConstants.PrefillToken, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, prefillGroup.CacheHitBytes);
        Assert.Equal(100, prefillGroup.CacheMissBytes);
    }

    /// <summary>
    /// Regression test for a second bug that the Datasource-key fix would otherwise introduce:
    /// FillDownloadIdsAsync matched download rows back to page items by (DepotId, ClientIp) alone.
    /// Once a depot+IP pair can produce two separate group rows (default + prefill), keying by the pair
    /// alone would let the prefill row's download id leak into the real client's DownloadIds (and vice
    /// versa). Datasource must be part of the pair key throughout - pairsByRowId, neededPairs, and
    /// idsByPair - so each card only ever collects its own rows' ids.
    /// </summary>
    [Fact]
    public void FillDownloadIds_PrefillAndRealClient_SameDepotAndClientIp_DoNotShareDownloadIds()
    {
        var detailRows = new[]
        {
            new { Id = 501L, DepotId = 730L, ClientIp = "172.17.0.4", Datasource = "default" },
            new { Id = 502L, DepotId = 730L, ClientIp = "172.17.0.4", Datasource = DownloadKindConstants.PrefillToken },
        };

        var realPair = (DepotId: 730L, ClientIp: "172.17.0.4", Datasource: "default");
        var prefillPair = (DepotId: 730L, ClientIp: "172.17.0.4", Datasource: DownloadKindConstants.PrefillToken);
        var neededPairs = new HashSet<(long DepotId, string ClientIp, string Datasource)> { realPair, prefillPair };

        var idsByPair = new Dictionary<(long DepotId, string ClientIp, string Datasource), List<long>>();
        foreach (var row in detailRows)
        {
            var pair = (row.DepotId, row.ClientIp, row.Datasource);
            if (!neededPairs.Contains(pair)) continue;
            if (!idsByPair.TryGetValue(pair, out var ids))
            {
                ids = new List<long>();
                idsByPair[pair] = ids;
            }
            ids.Add(row.Id);
        }

        Assert.Equal(new List<long> { 501L }, idsByPair[realPair]);
        Assert.Equal(new List<long> { 502L }, idsByPair[prefillPair]);
    }

    /// <summary>
    /// Mirrors the GroupBy + aggregate projection used by
    /// DownloadsController.GetRetroDownloadsAsync. ToQueryString compiles the
    /// query through the Npgsql provider without opening a connection, so a
    /// translation regression (e.g. TimeSpan.TotalSeconds inside grouped
    /// conditional sums) fails here instead of returning empty pages at runtime.
    /// </summary>
    [Fact]
    public void RetroGroupedAggregateQuery_TranslatesToSql()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_smoke_test")
            .Options;

        using var context = new AppDbContext(options);

        var query = context.Downloads
            .AsNoTracking()
            .Where(d => !d.IsActive)
            .GroupBy(d => new { d.DepotId, d.ClientIp, d.Datasource, RowKey = d.DepotId == null ? d.Id : 0L })
            .Select(g => new
            {
                g.Key.DepotId,
                g.Key.ClientIp,
                g.Key.Datasource,
                g.Key.RowKey,
                Service = g.Max(d => d.Service),
                GameName = g.Max(d => d.GameName),
                GameAppId = g.Max(d => d.GameAppId),
                EpicAppId = g.Max(d => d.EpicAppId),
                CacheHitBytes = g.Sum(d => d.CacheHitBytes),
                CacheMissBytes = g.Sum(d => d.CacheMissBytes),
                StartTimeUtc = g.Min(d => d.StartTimeUtc),
                EndTimeUtc = g.Max(d => d.EndTimeUtc),
                RequestCount = g.Count(),
                WeightedSpeedSum = g.Sum(d =>
                    (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds > 0
                        ? ((d.CacheHitBytes + d.CacheMissBytes)
                           / (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds)
                          * (d.CacheHitBytes + d.CacheMissBytes)
                        : 0),
                SpeedBytesSum = g.Sum(d =>
                    (d.EndTimeUtc - d.StartTimeUtc).TotalSeconds > 0
                    && (d.CacheHitBytes + d.CacheMissBytes) > 0
                        ? (double)(d.CacheHitBytes + d.CacheMissBytes)
                        : 0)
            });

        var sql = query.ToQueryString();

        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
