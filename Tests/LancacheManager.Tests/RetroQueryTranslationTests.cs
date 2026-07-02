using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public class RetroQueryTranslationTests
{
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
            .GroupBy(d => new { d.DepotId, d.ClientIp, RowKey = d.DepotId == null ? d.Id : 0L })
            .Select(g => new
            {
                g.Key.DepotId,
                g.Key.ClientIp,
                g.Key.RowKey,
                Service = g.Max(d => d.Service),
                Datasource = g.Max(d => d.Datasource),
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
