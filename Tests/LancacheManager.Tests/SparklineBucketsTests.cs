using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Tests;

public class SparklineBucketsTests
{
    [Theory]
    [InlineData(0, 15)]
    [InlineData(2, 15)]
    [InlineData(2.1, 30)]
    [InlineData(13, 30)]
    [InlineData(13.1, 60)]
    [InlineData(25, 60)]
    [InlineData(25.1, 180)]
    [InlineData(240, 180)]
    [InlineData(240.1, 1440)]
    public void ResolveMinutes_UsesExistingShortTiersAndThreeHoursUpToTenDays(
        double rangeHours,
        int expectedMinutes
    )
    {
        Assert.Equal(expectedMinutes, SparklineBuckets.ResolveMinutes(rangeHours));
    }

    [Fact]
    public void AlignStart_SnapsThreeHourSlotsInUtc()
    {
        var input = new DateTime(2026, 8, 16, 17, 44, 0, DateTimeKind.Utc);

        var aligned = SparklineBuckets.AlignStart(input, 180);

        Assert.Equal(new DateTime(2026, 8, 16, 15, 0, 0, DateTimeKind.Utc), aligned);
    }

    [Fact]
    public void AlignStart_SnapsDaysAndQuarterHoursInUtc()
    {
        var input = new DateTime(2026, 8, 16, 17, 44, 0, DateTimeKind.Utc);

        Assert.Equal(
            new DateTime(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc),
            SparklineBuckets.AlignStart(input, 1440));
        Assert.Equal(
            new DateTime(2026, 8, 16, 17, 30, 0, DateTimeKind.Utc),
            SparklineBuckets.AlignStart(input, 15));
        Assert.Equal(
            DateTimeKind.Utc,
            SparklineBuckets.AlignStart(
                new DateTime(2026, 8, 16, 17, 44, 0, DateTimeKind.Unspecified),
                1440).Kind);
    }

    [Fact]
    public void Fill_InsertsEmptySlotsSoAShortEventKeepsItsWindow()
    {
        var present = new[]
        {
            new SparklineBuckets.Bucket(
                new DateTime(2026, 8, 16, 15, 0, 0, DateTimeKind.Utc),
                100,
                20
            )
        };

        var filled = SparklineBuckets.Fill(
            new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 16, 21, 0, 0, DateTimeKind.Utc),
            180,
            present
        );

        Assert.Equal(3, filled.Count);
        Assert.Equal(0, filled[0].CacheHitBytes);
        Assert.Equal(100, filled[1].CacheHitBytes);
        Assert.Equal(0, filled[2].CacheHitBytes);
    }

    [Fact]
    public void Fill_LeavesOutTheBucketTheWindowEndsPartWayThrough()
    {
        var present = new[]
        {
            new SparklineBuckets.Bucket(
                new DateTime(2026, 8, 16, 15, 0, 0, DateTimeKind.Utc),
                100,
                20
            )
        };
        var windowEnd = new DateTime(2026, 8, 16, 19, 10, 0, DateTimeKind.Utc);

        var filled = SparklineBuckets.Fill(
            new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc),
            windowEnd,
            180,
            present
        );

        Assert.Equal(2, filled.Count);
        Assert.Equal(new DateTime(2026, 8, 16, 15, 0, 0, DateTimeKind.Utc), filled[^1].Start);
        Assert.True(filled[^1].Start.AddMinutes(180) <= windowEnd);
    }

    [Fact]
    public void Fill_ReportsTheBucketStillFillingWhenNoWholeOneHasClosed()
    {
        // A ten-day party two hours in. The width is 180, so the 15:30 and 16:00 downloads sit in a
        // bucket that will not close until 18:00, and it is the only bucket the window has.
        var start = new DateTime(2026, 8, 17, 15, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 8, 17, 17, 0, 0, DateTimeKind.Utc);
        var bucketMinutes = SparklineBuckets.ResolveMinutes((start.AddDays(10) - start).TotalHours);
        var present = new[] { new SparklineBuckets.Bucket(start, 400, 100) };

        var elapsed = SparklineBuckets.SharedElapsedMinutes([(start, now)], bucketMinutes);
        var filled = SparklineBuckets.Fill(start, now, bucketMinutes, present);
        var served = SparklineBuckets.ProjectElapsed(
            filled,
            start,
            bucketMinutes,
            elapsed,
            b => b.CacheHitBytes + b.CacheMissBytes
        );

        Assert.Equal(180, bucketMinutes);
        Assert.Single(filled);
        Assert.Equal(start, filled[0].Start);
        Assert.Equal(new[] { 0 }, elapsed);
        Assert.Equal(500, served[0]);
    }

    [Fact]
    public void Fill_KeepsAShortEventOnTheWidthALongerOneSelected()
    {
        // A finished two-hour party beside a ten-day one: the shared width is 180, so the short one
        // has no whole bucket of its own and groups into the bucket that opened before it started.
        var start = new DateTime(2026, 8, 17, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc);
        var present = new[]
        {
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 9, 0, 0, DateTimeKind.Utc), 300, 200)
        };

        var filled = SparklineBuckets.Fill(start, end, 180, present);
        var served = SparklineBuckets.ProjectElapsed(
            filled,
            start,
            180,
            [0, 180, 360],
            b => b.CacheHitBytes + b.CacheMissBytes
        );

        Assert.Single(filled);
        Assert.Equal(500, served[0]);
        Assert.Null(served[1]);
        Assert.Null(served[2]);
    }

    [Fact]
    public void ResolveCompareBucketMinutes_MeasuresTheRunAndNotTheBooking()
    {
        // A ten-day event four hours in. Measured on the run that is eight closed half-hour
        // buckets; measured on the booking, no whole bucket fits and it collapses to one point.
        var start = new DateTime(2026, 8, 17, 10, 30, 0, DateTimeKind.Utc);
        var windows = new[] { (Start: start, End: start.AddHours(4)) };
        var present = new[]
        {
            new SparklineBuckets.Bucket(start, 10, 0),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 14, 0, 0, DateTimeKind.Utc), 20, 0)
        };

        var bucketMinutes = DashboardBatchService.ResolveCompareBucketMinutes(windows);
        var filled = SparklineBuckets.Fill(start, windows[0].End, bucketMinutes, present);

        Assert.Equal(30, bucketMinutes);
        Assert.Equal(8, filled.Count);
        Assert.Equal(start, filled[0].Start);
        Assert.Equal(10, filled[0].CacheHitBytes);
        Assert.Equal(20, filled[^1].CacheHitBytes);

        var booked = SparklineBuckets.ResolveMinutes(240);
        Assert.Single(SparklineBuckets.Fill(start, windows[0].End, booked, present));
    }

    [Fact]
    public void CoveringWindow_KeepsAThirtyDayPickToTheBucketsHoldingTheDownloads()
    {
        // A thirty-day pick whose downloads all fall inside ninety minutes: width and window both
        // come from those ninety minutes.
        var first = new DateTime(2026, 8, 17, 12, 7, 0, DateTimeKind.Utc);
        var last = first.AddMinutes(90);
        var bucketMinutes = SparklineBuckets.ResolveMinutes((last - first).TotalHours);
        var present = new[]
        {
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), 100, 20),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 13, 30, 0, DateTimeKind.Utc), 300, 40)
        };

        var (windowStart, windowEnd) = SparklineBuckets.CoveringWindow(first, last, bucketMinutes);
        var filled = SparklineBuckets.Fill(windowStart, windowEnd, bucketMinutes, present);

        Assert.Equal(15, bucketMinutes);
        Assert.Equal(7, filled.Count);
        Assert.Equal(new DateTime(2026, 8, 17, 12, 0, 0, DateTimeKind.Utc), filled[0].Start);
        Assert.Equal(300, filled[^1].CacheHitBytes);
    }

    [Fact]
    public void Fill_StopsAtTheBucketCapAndKeepsTheNewestBuckets()
    {
        var windowStart = new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var windowEnd = windowStart.AddDays(1500);
        var newest = windowEnd.AddDays(-1);
        var present = new[] { new SparklineBuckets.Bucket(newest, 900, 100) };

        var filled = SparklineBuckets.Fill(windowStart, windowEnd, 1440, present);

        Assert.Equal(1000, filled.Count);
        Assert.Equal(newest, filled[^1].Start);
        Assert.Equal(900, filled[^1].CacheHitBytes);
        Assert.Equal(newest.AddDays(-999), filled[0].Start);
    }

    [Fact]
    public void SharedElapsedMinutes_UsesTheLongestWindow()
    {
        var shortParty = (
            new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 8, 1, 18, 0, 0, DateTimeKind.Utc)
        );
        var longParty = (
            new DateTime(2025, 1, 10, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 10, 12, 0, 0, DateTimeKind.Utc)
        );

        var elapsed = SparklineBuckets.SharedElapsedMinutes([shortParty, longParty], 180);

        Assert.Equal(new[] { 0, 180, 360, 540 }, elapsed);
    }

    [Fact]
    public void SharedElapsedMinutes_GivesEveryEventAWholeFirstBucket()
    {
        // Two weekend parties three-hour buckets apart on the clock: one opens on a bucket boundary
        // and the other two and a half hours into one.
        var onTheHour = (
            Start: new DateTime(2026, 8, 14, 18, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 8, 16, 23, 0, 0, DateTimeKind.Utc)
        );
        var halfPast = (
            Start: new DateTime(2026, 8, 14, 20, 30, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 8, 16, 21, 0, 0, DateTimeKind.Utc)
        );
        var present = new[]
        {
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 14, 18, 0, 0, DateTimeKind.Utc), 10, 0),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 14, 21, 0, 0, DateTimeKind.Utc), 20, 0)
        };

        var elapsed = SparklineBuckets.SharedElapsedMinutes([onTheHour, halfPast], 180);
        var onTheHourFilled = SparklineBuckets.Fill(onTheHour.Start, onTheHour.End, 180, present);
        var halfPastFilled = SparklineBuckets.Fill(halfPast.Start, halfPast.End, 180, present);

        Assert.Equal(17, elapsed.Count);
        Assert.Equal(2880, elapsed[^1]);
        Assert.Equal(onTheHour.Start, onTheHourFilled[0].Start);
        Assert.Equal(new DateTime(2026, 8, 14, 21, 0, 0, DateTimeKind.Utc), halfPastFilled[0].Start);
        Assert.True(halfPastFilled[0].Start >= halfPast.Start);
    }

    [Fact]
    public void ProjectElapsed_PutsTheFirstWholeBucketAtElapsedZero()
    {
        var eventStart = new DateTime(2026, 8, 14, 20, 30, 0, DateTimeKind.Utc);
        var eventEnd = new DateTime(2026, 8, 15, 3, 0, 0, DateTimeKind.Utc);
        var present = new[]
        {
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 14, 18, 0, 0, DateTimeKind.Utc), 10, 0),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 14, 21, 0, 0, DateTimeKind.Utc), 20, 0),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc), 30, 0)
        };

        var filled = SparklineBuckets.Fill(eventStart, eventEnd, 180, present);
        var served = SparklineBuckets.ProjectElapsed(
            filled,
            eventStart,
            180,
            [0, 180, 360],
            b => b.CacheHitBytes
        );

        Assert.Equal(20, served[0]);
        Assert.Equal(30, served[1]);
        Assert.Null(served[2]);
    }

    [Fact]
    public void ProjectElapsed_StopsAfterTheShorterEvent()
    {
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var end = start.AddHours(6);
        var present = new[]
        {
            new SparklineBuckets.Bucket(start, 50, 10),
            new SparklineBuckets.Bucket(start.AddHours(3), 80, 20)
        };

        var shared = SparklineBuckets.SharedElapsedMinutes(
            [(start, end), (start, start.AddHours(12))],
            180);
        var filled = SparklineBuckets.Fill(start, end, 180, present);
        var served = SparklineBuckets.ProjectElapsed(
            filled,
            start,
            180,
            shared,
            b => b.CacheHitBytes + b.CacheMissBytes
        );

        Assert.Equal(new[] { 0, 180, 360, 540 }, shared);
        Assert.Equal(60, served[0]);
        Assert.Equal(100, served[1]);
        Assert.Null(served[2]);
        Assert.Null(served[3]);
    }

    [Fact]
    public void ProjectElapsed_StopsAtNowForAnEventThatIsStillRunning()
    {
        // A party scheduled 18:00 to 02:00 read at 19:10: the planned end is in the future, so
        // nothing after 19:10 has traffic to report yet.
        var now = new DateTime(2026, 8, 17, 19, 10, 0, DateTimeKind.Utc);
        var finished = (
            Start: new DateTime(2026, 8, 17, 8, 0, 0, DateTimeKind.Utc),
            End: new DateTime(2026, 8, 17, 16, 0, 0, DateTimeKind.Utc)
        );
        var running = (
            Start: new DateTime(2026, 8, 17, 18, 0, 0, DateTimeKind.Utc),
            End: now
        );
        var present = new[]
        {
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 18, 0, 0, DateTimeKind.Utc), 400, 100),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 18, 30, 0, DateTimeKind.Utc), 300, 100),
            new SparklineBuckets.Bucket(new DateTime(2026, 8, 17, 19, 0, 0, DateTimeKind.Utc), 200, 0)
        };

        var bucketMinutes = SparklineBuckets.ResolveMinutes(8);
        var shared = SparklineBuckets.SharedElapsedMinutes([finished, running], bucketMinutes);
        var filled = SparklineBuckets.Fill(running.Start, running.End, bucketMinutes, present);
        var served = SparklineBuckets.ProjectElapsed(
            filled,
            running.Start,
            bucketMinutes,
            shared,
            b => b.CacheHitBytes + b.CacheMissBytes
        );

        Assert.Equal(30, bucketMinutes);
        Assert.Equal(16, shared.Count);
        Assert.Equal(500, served[0]);
        Assert.Equal(400, served[1]);
        Assert.Equal(14, served.Count(value => value is null));
        Assert.All(served.Skip(2), value => Assert.Null(value));
    }

    [Fact]
    public void BuildSparklineMetric_ReadsTheTrendUpToTheLastBucketWithTraffic()
    {
        // A 24-hour view whose downloads stopped six hours ago: the tail is zero because nothing
        // was served, not because the day fell off.
        var data = Enumerable.Repeat(100.0, 16)
            .Concat(Enumerable.Repeat(300.0, 3))
            .Concat(Enumerable.Repeat(0.0, 6))
            .ToList();

        var metric = DashboardBatchService.BuildSparklineMetric(
            data,
            DashboardBatchService.SparklineTrendScale.Proportional,
            18);

        Assert.Equal("up", metric.Trend);
        Assert.Equal(25, metric.Data.Count);
    }

    [Fact]
    public void BuildSparklineMetric_ReadsAHitRatioThatFallsToZeroInABucketWithTraffic()
    {
        // Four hours whose last hour was served entirely from misses: a measured 0%, so the hour
        // stays in the trend rather than being trimmed as empty.
        var ratios = new List<double> { 50, 50, 50, 0 };
        var traffic = new[] { 100L, 100L, 100L, 400L };

        var metric = DashboardBatchService.BuildSparklineMetric(
            ratios,
            DashboardBatchService.SparklineTrendScale.Points,
            Array.FindLastIndex(traffic, total => total > 0));

        Assert.Equal("down", metric.Trend);
        Assert.Equal(4, metric.Data.Count);
    }

    [Fact]
    public void BuildSparklineMetric_CallsAnEmptyRangeStable()
    {
        var data = Enumerable.Repeat(0.0, 12).ToList();

        var metric = DashboardBatchService.BuildSparklineMetric(
            data,
            DashboardBatchService.SparklineTrendScale.Proportional,
            -1);

        Assert.Equal("stable", metric.Trend);
        Assert.Equal(12, metric.Data.Count);
    }

    /// <summary>
    /// The bucket keys are built in the database while the grid they are looked up in is built in
    /// UTC by <see cref="SparklineBuckets.AlignStart"/>, so every key must name UTC in the SQL.
    /// Unanchored they follow the server's session timezone, and on a zone offset by anything other
    /// than whole hours every lookup in <see cref="SparklineBuckets.Fill"/> misses. [14]
    /// </summary>
    [Theory]
    [InlineData(1440, "date_trunc('day', d.\"StartTimeUtc\", 'UTC')")]
    [InlineData(180, "date_part('hour', d.\"StartTimeUtc\" AT TIME ZONE 'UTC')")]
    [InlineData(15, "date_part('minute', d.\"StartTimeUtc\" AT TIME ZONE 'UTC')")]
    public void PresentBucketsQuery_AnchorsEveryBucketKeyToUtcInSql(int bucketMinutes, string anchoredKey)
    {
        using var context = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql("Host=localhost;Database=sparkline_bucket_translation_smoke_test")
                .Options);

        var sql = DashboardBatchService
            .PresentBucketsQuery(context.Downloads.AsNoTracking(), bucketMinutes)
            .ToQueryString();

        Assert.Contains("date_trunc('day', d.\"StartTimeUtc\", 'UTC')", sql, StringComparison.Ordinal);
        Assert.Contains(anchoredKey, sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY", sql, StringComparison.OrdinalIgnoreCase);
    }
}
