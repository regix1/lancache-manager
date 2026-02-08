namespace LancacheManager.Models;

/// <summary>
/// Metadata for game detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with game-detection-specific metrics.
/// </summary>
public class GameDetectionMetrics
{
    /// <summary>
    /// Type of scan performed - "full" or "incremental".
    /// </summary>
    public string ScanType { get; set; } = "incremental";

    /// <summary>
    /// List of detected game caches.
    /// </summary>
    public List<GameCacheInfo> Games { get; set; } = new();

    /// <summary>
    /// List of detected service caches.
    /// </summary>
    public List<ServiceCacheInfo> Services { get; set; } = new();

    /// <summary>
    /// Total number of games detected during the scan.
    /// </summary>
    public int TotalGamesDetected { get; set; }

    /// <summary>
    /// Total number of services detected during the scan.
    /// </summary>
    public int TotalServicesDetected { get; set; }

    /// <summary>
    /// Error message if the detection operation failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// When this detection was started (or when cached results were last detected).
    /// Used by GetCachedDetectionAsync to carry the last detection timestamp.
    /// </summary>
    public DateTime StartTime { get; set; }
}
