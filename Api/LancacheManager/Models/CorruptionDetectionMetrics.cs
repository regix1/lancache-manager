namespace LancacheManager.Models;

/// <summary>
/// Metadata for corruption detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with corruption-detection-specific metrics.
/// </summary>
public class CorruptionDetectionMetrics
{
    public Guid? ScanId { get; set; }

    public CorruptionDetectionMode DetectionMode { get; set; } = CorruptionDetectionMode.Unknown;

    public int Threshold { get; set; }

    public int LookbackDays { get; set; }

    /// <summary>
    /// Corruption counts keyed by category or URL.
    /// </summary>
    public Dictionary<string, long>? CorruptionCounts { get; set; }

    public Dictionary<string, long>? RemovableServiceCounts { get; set; }

    public Dictionary<string, long>? ReviewOnlyServiceCounts { get; set; }

    /// <summary>
    /// Timestamp of the last completed corruption detection.
    /// </summary>
    public DateTime? LastDetectionTime { get; set; }

    /// <summary>
    /// When set, this operation is loading per-service corruption details (not a full summary scan).
    /// </summary>
    public string? ServiceName { get; set; }
}
