using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the projection the dashboard's hourly section and the Prometheus hourly gauges both
/// read. Spreading a download's bytes over the hours it was active needs each row's own window,
/// which no GROUP BY carries, so the query has to come back as rows and has to translate to SQL
/// rather than pulling every column and slicing in memory.
/// </summary>
public class HourlySpanProjectionTests
{
    [Fact]
    public void SpanProjection_ReadsOnlyTheFourColumnsTheBucketsNeed()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=hourly_span_translation_smoke_test")
                .Options);

        var sql = context.Downloads.AsNoTracking()
            .Select(d => new HourOfDayBuckets.Span(d.StartTimeUtc, d.EndTimeUtc, d.CacheHitBytes, d.CacheMissBytes))
            .ToQueryString();

        Assert.Contains("\"StartTimeUtc\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"EndTimeUtc\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"CacheHitBytes\"", sql, StringComparison.Ordinal);
        Assert.Contains("\"CacheMissBytes\"", sql, StringComparison.Ordinal);
        // A projection that fell back to the whole entity would carry columns the buckets never
        // read, and these tables are the widest rows in the schema.
        Assert.DoesNotContain("\"GameName\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LastUrl\"", sql, StringComparison.Ordinal);
    }
}
