namespace LancacheManager.Models;

/// <summary>
/// Metadata for corruption detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with corruption-detection-specific metrics.
/// </summary>
public class CorruptionDetectionMetrics
{
    private OperationProgressSnapshot? _currentProgress;
    private long _progressRevision;

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

    public OperationProgressSnapshot? CurrentProgress => Volatile.Read(ref _currentProgress);

    public OperationProgressSnapshot CaptureProgress(
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?>? context,
        IReadOnlyDictionary<string, object?>? authoritativeContext = null)
    {
        var snapshot = OperationProgressSnapshot.Create(
            stageKey,
            percentComplete,
            context,
            Interlocked.Increment(ref _progressRevision),
            authoritativeContext);
        Volatile.Write(ref _currentProgress, snapshot);
        return snapshot;
    }

    public void ClearProgress() => Volatile.Write(ref _currentProgress, null);

}
