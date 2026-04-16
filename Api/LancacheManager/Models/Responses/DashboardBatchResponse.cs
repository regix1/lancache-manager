namespace LancacheManager.Models.Responses;

/// <summary>
/// Response shape for the dashboard batch endpoint.
/// Each field is nullable to support partial failure (null = sub-query failed).
/// </summary>
public class DashboardBatchResponse
{
    public CacheInfo? Cache { get; set; }
    public object? Clients { get; set; }
    public object? Services { get; set; }
    public object? Dashboard { get; set; }
    public object? Downloads { get; set; }
    public object? Detection { get; set; }
    public object? Sparklines { get; set; }
    public object? HourlyActivity { get; set; }
    public object? CacheSnapshot { get; set; }
    public object? CacheGrowth { get; set; }
}
