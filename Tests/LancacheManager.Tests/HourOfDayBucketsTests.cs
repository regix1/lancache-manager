using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the decisions behind the Peak Usage Hours heatmap: which hours a download's bytes land
/// in, which hour its count lands in, and which hour is named the busiest.
/// </summary>
public class HourOfDayBucketsTests
{
    /// <summary>
    /// The zone has to be one the server is not on, or a cut that ignores it and one that honours
    /// it produce the same buckets and the test proves nothing. India is +05:30, which is also not
    /// a whole number of hours from UTC.
    /// </summary>
    private const string IndiaZoneId = "Asia/Kolkata";

    private static DateTime Utc(int hour, int minute) =>
        new(2026, 8, 10, hour, minute, 0, DateTimeKind.Utc);

    /// <summary>
    /// A download serves its bytes over its whole active window, so crediting them all to the hour
    /// it began invents a peak: one large game starting at 2:59 PM would put hours of traffic into
    /// the 2 PM cell.
    /// </summary>
    [Fact]
    public void Build_SpreadsBytesAcrossTheHoursTheDownloadWasActive()
    {
        var spans = new[] { new HourOfDayBuckets.Span(Utc(14, 30), Utc(16, 30), 4_000, 0) };

        var hours = HourOfDayBuckets.Build(spans, TimeZoneInfo.Utc, null, null);

        Assert.Equal(1_000, hours[14].BytesServed);
        Assert.Equal(2_000, hours[15].BytesServed);
        Assert.Equal(1_000, hours[16].BytesServed);
        // The count stays with the hour the download began: that is what a download count means.
        Assert.Equal(1, hours[14].Downloads);
        Assert.Equal(0, hours[15].Downloads);
    }

    /// <summary>
    /// 10:00-11:00 UTC is 15:30-16:30 in India, half in each local hour. A cut on UTC boundaries
    /// would put the whole hour into one bucket and shade the wrong cell for that reader.
    /// </summary>
    [Fact]
    public void Build_CutsAtTheReadersOwnHourBoundaries()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(IndiaZoneId);
        var spans = new[] { new HourOfDayBuckets.Span(Utc(10, 0), Utc(11, 0), 8_000, 0) };

        var hours = HourOfDayBuckets.Build(spans, zone, null, null);

        Assert.Equal(4_000, hours[15].BytesServed);
        Assert.Equal(4_000, hours[16].BytesServed);
    }

    /// <summary>
    /// Began an hour before the range and ran an hour into it: only the half served inside the
    /// range is charted, and the count belongs to the period the download started in, which this
    /// range is not.
    /// </summary>
    [Fact]
    public void Build_ClipsAStraddlerToTheRangeAndDoesNotCountIt()
    {
        var spans = new[] { new HourOfDayBuckets.Span(Utc(9, 0), Utc(11, 0), 6_000, 0) };

        var hours = HourOfDayBuckets.Build(spans, TimeZoneInfo.Utc, Utc(10, 0), Utc(20, 0));

        Assert.Equal(0, hours[9].BytesServed);
        Assert.Equal(3_000, hours[10].BytesServed);
        Assert.Equal(0, hours.Sum(h => h.Downloads));
    }

    /// <summary>
    /// An end of default(DateTime) sits before the start. The row still carries its bytes, and
    /// they land where the download began rather than being spread over a negative window.
    /// </summary>
    [Fact]
    public void Build_TreatsAnUnwrittenEndAsInstantaneous()
    {
        var spans = new[] { new HourOfDayBuckets.Span(Utc(7, 15), default, 5_000, 2_000) };

        var hours = HourOfDayBuckets.Build(spans, TimeZoneInfo.Utc, null, null);

        Assert.Equal(7_000, hours[7].BytesServed);
        Assert.Equal(5_000, hours[7].CacheHitBytes);
        Assert.Equal(1, hours[7].Downloads);
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
