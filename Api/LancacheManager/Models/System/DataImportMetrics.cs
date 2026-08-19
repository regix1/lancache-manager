namespace LancacheManager.Models;

/// <summary>Operation-owned terminal metrics and current structured data-import progress.</summary>
public sealed class DataImportMetrics
{
    private OperationProgressSnapshot? _currentProgress;
    private long _progressRevision;

    public string? Message { get; set; }
    public ulong? RecordsImported { get; set; }
    public ulong? RecordsSkipped { get; set; }
    public ulong? RecordsErrors { get; set; }
    public ulong? TotalRecords { get; set; }

    public OperationProgressSnapshot? CurrentProgress => Volatile.Read(ref _currentProgress);

    public void PublishProgress(OperationProgressSnapshot snapshot) =>
        Volatile.Write(ref _currentProgress, snapshot);

    public OperationProgressSnapshot CaptureProgress(
        string stageKey,
        double percentComplete,
        IReadOnlyDictionary<string, object?>? context)
    {
        var snapshot = OperationProgressSnapshot.Create(
            stageKey,
            percentComplete,
            context,
            Interlocked.Increment(ref _progressRevision));
        PublishProgress(snapshot);
        return snapshot;
    }

    public void ClearProgress() => Volatile.Write(ref _currentProgress, null);
}
