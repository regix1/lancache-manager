using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the divisor behind the hourly section's per-day averages: how many days the downloads
/// cover, counted on the clock the reader is looking at.
/// </summary>
public class ActivityDatesQueryTests
{
    /// <summary>
    /// The zone has to be one the server is not on, or a count that ignores it and one that honours
    /// it produce the same answer and the test proves nothing. India is +05:30, which is also not a
    /// whole number of hours from UTC.
    /// </summary>
    private const string IndiaZoneId = "Asia/Kolkata";

    /// <summary>
    /// AT TIME ZONE hands date_trunc a time already on the reader's clock, so the day boundary is
    /// that reader's midnight and the offset it was resolved at is the one in force at each row's
    /// own instant.
    /// </summary>
    [Fact]
    public void ActivityDatesQuery_CountsTheDayInTheReadersZone()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=activity_dates_translation_smoke_test")
                .Options);

        var sql = DashboardBatchService
            .ActivityDatesQuery(context.Downloads.AsNoTracking(), IndiaZoneId)
            .ToQueryString();

        Assert.Contains(
            "date_trunc('day', d.\"StartTimeUtc\" AT TIME ZONE @timeZoneId)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("@timeZoneId='Asia/Kolkata'", sql, StringComparison.Ordinal);
        Assert.Contains("DISTINCT", sql, StringComparison.Ordinal);
    }
}
