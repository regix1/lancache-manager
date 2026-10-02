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
