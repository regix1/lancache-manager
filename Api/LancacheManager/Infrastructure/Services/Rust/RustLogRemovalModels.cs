using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

internal readonly record struct LogRemovalCompletionMetrics(
    Guid OperationId,
    string Service,
    string SuccessMessage,
    string FailureMessage,
    string CancelMessage,
    int FilesProcessed = 0,
    long LinesProcessed = 0,
    long LinesRemoved = 0,
    int DatabaseRecordsDeleted = 0,
    string? Datasource = null,
    string? StageKey = null);

internal sealed record LogRemovalCurrentProgress(
    OperationProgressSnapshot Snapshot,
    int FilesProcessed,
    long LinesProcessed,
    long LinesRemoved,
    string? Datasource);

internal sealed class DatabaseCleanupResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int TotalDeleted { get; set; }
    public int LogEntriesDeleted { get; set; }
    public int DownloadsDeleted { get; set; }
}
