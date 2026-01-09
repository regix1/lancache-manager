namespace LancacheManager.Models;

public enum GcAggressiveness
{
    Disabled,
    OnPageLoad,
    Every60Minutes,
    Every60Seconds,
    Every30Seconds,
    Every10Seconds,
    Every5Seconds,
    Every1Second
}

public class GcSettings
{
    public GcAggressiveness Aggressiveness { get; set; } = GcAggressiveness.Disabled;
    public long MemoryThresholdMB { get; set; } = 4096; // 4GB default
}
