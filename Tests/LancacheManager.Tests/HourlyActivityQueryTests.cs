using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the two decisions behind the Peak Usage Hours heatmap: which hour a download is counted
/// into, and which hour is named the busiest.
/// </summary>
public class HourlyActivityQueryTests
{
    /// <summary>
    /// The zone has to be one the server is not on, or a grouping that ignores it and one that
    /// honours it produce the same buckets and the test proves nothing. India is +05:30, which is
    /// also not a whole number of hours from UTC.
    /// </summary>
    private const string IndiaZoneId = "Asia/Kolkata";

    /// <summary>
    /// .NET settles "Eastern Standard Time" into a real zone, so resolving a name is not enough to
    /// say Postgres will take it: it knows only the IANA spelling. UTC is spelled the same in both
    /// and rides on every UTC reader's request, so it must survive untouched. [65]
    /// </summary>
    [Fact]
    public void KnownTimeZoneId_NamesAZoneTheWayTheDatabaseReadsIt()
    {
        Assert.Equal("America/New_York", DashboardBatchService.KnownTimeZoneId("Eastern Standard Time"));
        Assert.Equal("UTC", DashboardBatchService.KnownTimeZoneId("UTC"));
        Assert.Equal(IndiaZoneId, DashboardBatchService.KnownTimeZoneId(IndiaZoneId));
    }

    /// <summary>
    /// A zone name this server's tzdata does not carry is rejected by the database and would fail
    /// every section of the batch, so it has to be refused here and replaced with the server's own
    /// zone before the query is built. Nothing but null can signal that to the caller. [58]
    /// </summary>
    [Fact]
    public void KnownTimeZoneId_RefusesAZoneTheDatabaseCannotResolve()
    {
        Assert.Null(DashboardBatchService.KnownTimeZoneId("Not/AZone"));
    }

    /// <summary>
    /// The zone rides as a parameter to AT TIME ZONE, which resolves the offset at each row's own
    /// instant. One offset taken at request time is wrong for rows past a daylight-saving change.
    /// </summary>
    [Fact]
    public void HourlyActivityQuery_GroupsOnTheHourInTheReadersZone()
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=hourly_activity_translation_smoke_test")
                .Options);

        var sql = DashboardBatchService
            .HourlyActivityQuery(context.Downloads.AsNoTracking(), IndiaZoneId)
            .ToQueryString();

        Assert.Contains(
            "date_part('hour', d.\"StartTimeUtc\" AT TIME ZONE @timeZoneId)",
            sql,
            StringComparison.Ordinal);
        Assert.Contains("@timeZoneId='Asia/Kolkata'", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PeakHour_NamesTheHourThatServedTheMostBytes()
    {
        var hours = Enumerable.Range(0, 24)
            .Select(hour => new HourlyActivityItem { Hour = hour })
            .ToList();
        // One 90 GB game against fifty small updates: the heatmap shades hour 3 darkest, so that is
        // the hour the readout has to name.
        hours[3].Downloads = 1;
        hours[3].BytesServed = 90L * 1024 * 1024 * 1024;
        hours[14].Downloads = 50;
        hours[14].BytesServed = 50L * 1024 * 1024;

        Assert.Equal(3, DashboardBatchService.PeakHour(hours));
    }

    [Fact]
    public void PeakHour_KeepsTheEarliestHourWhenTwoServedTheSame()
    {
        var hours = Enumerable.Range(0, 24)
            .Select(hour => new HourlyActivityItem { Hour = hour })
            .ToList();
        hours[9].BytesServed = 4_096;
        hours[21].BytesServed = 4_096;

        Assert.Equal(9, DashboardBatchService.PeakHour(hours));
    }
}
