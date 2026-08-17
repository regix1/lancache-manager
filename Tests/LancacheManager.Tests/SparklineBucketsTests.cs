using LancacheManager.Infrastructure.Utilities;

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
            new DateTime(2026, 8, 16, 20, 0, 0, DateTimeKind.Utc),
            180,
            present
        );

        Assert.Equal(3, filled.Count);
        Assert.Equal(0, filled[0].CacheHitBytes);
        Assert.Equal(100, filled[1].CacheHitBytes);
        Assert.Equal(0, filled[2].CacheHitBytes);
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

        Assert.Equal(new[] { 0, 180, 360, 540, 720 }, elapsed);
    }

    [Fact]
    public void ProjectElapsed_StopsAfterTheShorterEvent()
    {
        var start = new DateTime(2026, 8, 16, 12, 0, 0, DateTimeKind.Utc);
        var filled = new[]
        {
            new SparklineBuckets.Bucket(start, 50, 10),
            new SparklineBuckets.Bucket(start.AddHours(3), 80, 20)
        };
        var shared = new[] { 0, 180, 360, 540 };

        var served = SparklineBuckets.ProjectElapsed(
            filled,
            start,
            180,
            shared,
            b => b.CacheHitBytes + b.CacheMissBytes
        );

        Assert.Equal(60, served[0]);
        Assert.Equal(100, served[1]);
        Assert.Null(served[2]);
        Assert.Null(served[3]);
    }
}
