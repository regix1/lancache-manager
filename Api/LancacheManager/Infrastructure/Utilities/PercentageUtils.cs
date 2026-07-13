namespace LancacheManager.Infrastructure.Utilities;

internal static class PercentageUtils
{
    /// <summary>
    /// Computes recent-vs-older percent change with the dashboard's established zero-baseline
    /// semantics and safety bound. Callers deliberately choose their own rounding and trend shape.
    /// </summary>
    internal static double CalculateBoundedChange(double olderAverage, double recentAverage)
    {
        double percentChange;
        if (olderAverage == 0 && recentAverage == 0)
        {
            percentChange = 0;
        }
        else if (olderAverage == 0)
        {
            percentChange = recentAverage > 0 ? 100 : 0;
        }
        else
        {
            percentChange = ((recentAverage - olderAverage) / olderAverage) * 100;
        }

        return Math.Max(-999, Math.Min(999, percentChange));
    }
}
