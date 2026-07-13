using LancacheManager.Infrastructure.Utilities;

namespace LancacheManager.Tests;

public class PercentageUtilsTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 10, 100)]
    [InlineData(0, -10, 0)]
    [InlineData(100, 125, 25)]
    [InlineData(100, 50, -50)]
    [InlineData(1, 100, 999)]
    [InlineData(1, -100, -999)]
    public void CalculateBoundedChange_PreservesEstablishedTrendSemantics(
        double olderAverage,
        double recentAverage,
        double expected)
    {
        Assert.Equal(expected, PercentageUtils.CalculateBoundedChange(olderAverage, recentAverage));
    }
}
