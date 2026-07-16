using LancacheManager.Core.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Game detection scans each datasource with its own Rust process, and every process restarts its
/// progress count at 0. The scan-phase percent must therefore fold each datasource into an equal
/// slice of the shared 1..30 band so the emitted percent never drops when the next datasource
/// begins. These tests pin that global-denominator mapping and the running-max backstop.
/// </summary>
public class GameDetectionProgressMonotonicTests
{
    [Fact]
    public void MultipleDatasources_EmittedScanPercentIsMonotonicAndEndsAtBandTop()
    {
        const int datasourceCount = 2;
        var rustTicksPerDatasource = new[] { 0.0, 10, 25, 50, 75, 90, 100 };

        double runningMax = 0;
        var emitted = new List<double>();
        for (var index = 0; index < datasourceCount; index++)
        {
            foreach (var rust in rustTicksPerDatasource)
            {
                var mapped = GameCacheDetectionService.MapDatasourceScanPercent(rust, index, datasourceCount);
                // Running-max clamp mirrors the backstop the service applies at the emission point.
                runningMax = Math.Max(runningMax, mapped);
                emitted.Add(runningMax);
            }
        }

        for (var i = 1; i < emitted.Count; i++)
        {
            Assert.True(
                emitted[i] >= emitted[i - 1],
                $"emitted scan percent regressed at index {i}: {emitted[i - 1]} -> {emitted[i]}");
        }

        Assert.True(emitted[0] >= 1.0);
        Assert.Equal(30.0, emitted[^1], precision: 6);
    }

    [Fact]
    public void SecondDatasourceStart_DoesNotRegressBelowFirstDatasourceEnd()
    {
        const int datasourceCount = 2;

        var firstDatasourceEnd = GameCacheDetectionService.MapDatasourceScanPercent(100, 0, datasourceCount);
        var secondDatasourceStart = GameCacheDetectionService.MapDatasourceScanPercent(0, 1, datasourceCount);

        Assert.True(
            secondDatasourceStart >= firstDatasourceEnd,
            $"second datasource start {secondDatasourceStart} fell below first datasource end {firstDatasourceEnd}");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(5.0)]
    [InlineData(37.5)]
    [InlineData(80.0)]
    [InlineData(100.0)]
    public void SingleDatasource_MatchesOriginalOneToThirtyMapping(double rustPercent)
    {
        var expected = 1 + (rustPercent * 29.0 / 100.0);
        var mapped = GameCacheDetectionService.MapDatasourceScanPercent(rustPercent, 0, 1);

        Assert.Equal(expected, mapped, precision: 9);
    }
}
