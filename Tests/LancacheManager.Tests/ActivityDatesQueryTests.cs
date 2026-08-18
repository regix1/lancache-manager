using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
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
    /// Two downloads an hour apart across a midnight that is a midnight for the server and not for
    /// the reader. On a server running UTC+04:00 they are 23:00 on the 17th and 00:00 on the 18th,
    /// which is what StartTimeLocal carries here; in India they are 00:30 and 01:30 on the 18th.
    /// </summary>
    private static void SeedDownloadsAcrossServerMidnight(AppDbContext context)
    {
        var firstUtc = new DateTime(2026, 8, 17, 19, 0, 0, DateTimeKind.Utc);
        context.Downloads.Add(SteamDownload(10, firstUtc, firstUtc.AddHours(4), 1_000));
        var secondUtc = new DateTime(2026, 8, 17, 20, 0, 0, DateTimeKind.Utc);
        context.Downloads.Add(SteamDownload(11, secondUtc, secondUtc.AddHours(4), 2_000));
        context.SaveChanges();
    }

    [Fact]
    public void ActivityDatesQuery_WithoutAZoneCountsDaysOnTheServerClock()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        context.Database.EnsureCreated();
        SeedDownloadsAcrossServerMidnight(context);

        // A frontend cached from before the request carried a clock names no zone, and gets the
        // days the server recorded, exactly as this endpoint answered before.
        var dates = DashboardBatchService
            .ActivityDatesQuery(context.Downloads.AsNoTracking(), null)
            .ToList();

        Assert.Equal(2, dates.Count);
    }

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

    private static Download SteamDownload(long id, DateTime startUtc, DateTime startLocal, long hitBytes)
    {
        return new Download
        {
            Id = id,
            Service = "steam",
            ClientIp = "10.0.0.1",
            StartTimeUtc = startUtc,
            StartTimeLocal = startLocal,
            EndTimeUtc = startUtc.AddMinutes(10),
            EndTimeLocal = startLocal.AddMinutes(10),
            CacheHitBytes = hitBytes,
            CacheMissBytes = 0,
            IsActive = false,
            GameAppId = 730,
            GameName = "Counter-Strike 2",
            Datasource = "default",
            IsEvicted = false
        };
    }
}
