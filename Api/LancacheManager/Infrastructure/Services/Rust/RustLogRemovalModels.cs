using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

// StageKey is the key a successful ending names itself by: the child's last stage, or
// signalr.logRemoval.complete when the child wrote none.
internal readonly record struct LogRemovalCompletionMetrics(
    Guid OperationId,
    string Service,
    string StageKey,
    int FilesProcessed = 0,
    long LinesProcessed = 0,
    long LinesRemoved = 0,
    int DatabaseRecordsDeleted = 0,
    string? Datasource = null);

internal sealed record LogRemovalCurrentProgress(
    OperationProgressSnapshot Snapshot,
    int FilesProcessed,
    long LinesProcessed,
    long LinesRemoved,
    string? Datasource);
