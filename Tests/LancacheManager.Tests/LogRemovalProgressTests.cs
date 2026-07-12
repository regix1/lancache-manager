using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

public class LogRemovalProgressTests
{
    [Theory]
    [InlineData(0, 2, 0, 0)]
    [InlineData(0, 2, 100, 47.5)]
    [InlineData(1, 2, 0, 47.5)]
    [InlineData(1, 2, 100, 95)]
    public void MultiDatasourcePercentUsesMonotonicBands(
        int index,
        int count,
        double inner,
        double expected)
    {
        Assert.Equal(expected, RustLogRemovalService.ScaleIntoBand(inner, index, count, 95), 6);
    }

    [Fact]
    public void CountersAreCumulativeAcrossDatasources()
    {
        var cumulative = RustLogRemovalService.AddCumulativeCounters(
            completedFiles: 10,
            completedLines: 1_000,
            completedRemoved: 250,
            currentFiles: 3,
            currentLines: 400,
            currentRemoved: 100);

        Assert.Equal(13, cumulative.Files);
        Assert.Equal(1_400, cumulative.Lines);
        Assert.Equal(350, cumulative.Removed);
    }
}
