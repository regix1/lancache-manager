namespace LancacheManager.Models;

/// <summary>
/// Metadata for corruption detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with corruption-detection-specific metrics.
/// </summary>
public class CorruptionDetectionMetrics
{
    public Guid? ScanId { get; set; }

    public int Threshold { get; set; }

    public int LookbackDays { get; set; }

    public CorruptionDetectionMethod DetectionMethod { get; set; } = CorruptionDetectionMethod.RepeatedMiss;

    /// <summary>
    /// Corruption counts keyed by category or URL.
    /// </summary>
    public Dictionary<string, long>? CorruptionCounts { get; set; }

    public Dictionary<string, long>? DetectionCounts { get; set; }

    public CorruptionScanCoverage? Coverage { get; set; }

    /// <summary>
    /// Timestamp of the last completed corruption detection.
    /// </summary>
    public DateTime? LastDetectionTime { get; set; }

}
