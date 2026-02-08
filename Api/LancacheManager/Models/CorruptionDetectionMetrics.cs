namespace LancacheManager.Models;

/// <summary>
/// Metadata for corruption detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with corruption-detection-specific metrics.
/// </summary>
public class CorruptionDetectionMetrics
{
    /// <summary>
    /// Corruption counts keyed by category or URL.
    /// </summary>
    public Dictionary<string, long>? CorruptionCounts { get; set; }

    /// <summary>
    /// Timestamp of the last completed corruption detection.
    /// </summary>
    public DateTime? LastDetectionTime { get; set; }
}
