namespace LancacheManager.Core.Services;

internal sealed class DailyGrowthRow
{
    public DateTime Date { get; init; }
    public long GrowthBytes { get; init; }
}
