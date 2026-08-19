namespace LancacheManager.Models;

/// <summary>
/// Response for the metrics update interval get/set endpoints.
/// </summary>
public class MetricsIntervalResponse
{
    public int Interval { get; set; }
}

/// <summary>
/// Response for the per-game metrics limit get/set endpoints.
/// </summary>
public class MetricsGameLimitResponse
{
    public int GameLimit { get; set; }
}
