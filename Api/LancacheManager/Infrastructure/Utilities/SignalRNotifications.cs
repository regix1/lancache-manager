namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Typed SignalR notification payloads for consistent type safety across the application.
/// Using records for immutability and concise syntax.
/// </summary>
public static class SignalRNotifications
{
    /// <summary>
    /// Base interface for all completion notifications that have success/stageKey pattern.
    /// </summary>
    public interface ICompletionNotification
    {
        bool Success { get; }
        string StageKey { get; }
    }

    #region Removal Notifications

    /// <summary>
    /// Notification when game removal starts.
    /// </summary>
    public record GameRemovalStarted(
        Guid OperationId,
        long? GameAppId,
        string? EpicAppId,
        string GameName,
        string StageKey,
        DateTime Timestamp,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification for game removal progress updates.
    /// </summary>
    public record GameRemovalProgress(
        Guid OperationId,
    long? GameAppId,
    string? EpicAppId,
        string GameName,
        string StageKey,
        double PercentComplete = 0,
        int? FilesDeleted = null,
        long? BytesFreed = null,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when game removal completes (success or failure).
    /// </summary>
    public record GameRemovalComplete(
        bool Success,
        Guid OperationId,
    long? GameAppId,
    string? EpicAppId,
        string StageKey,
        string? GameName = null,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        ulong LogEntriesRemoved = 0,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    /// <summary>
    /// Notification when service removal starts.
    /// </summary>
    public record ServiceRemovalStarted(
        string ServiceName,
        Guid OperationId,
        string StageKey,
        DateTime Timestamp,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification for service removal progress updates.
    /// </summary>
    public record ServiceRemovalProgress(
        string ServiceName,
        Guid OperationId,
        string StageKey,
        double PercentComplete = 0,
        int? FilesDeleted = null,
        long? BytesFreed = null,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when service removal completes.
    /// </summary>
    public record ServiceRemovalComplete(
        bool Success,
        string ServiceName,
        Guid OperationId,
        string StageKey,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        ulong LogEntriesRemoved = 0,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    /// <summary>
    /// Notification for corruption removal started.
    /// </summary>
    public record CorruptionRemovalStarted(
        string Service,
        Guid OperationId,
        string StageKey,
        DateTime Timestamp,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification for corruption removal progress.
    /// </summary>
    public record CorruptionRemovalProgress(
        string Service,
        Guid OperationId,
        string Status,
        string StageKey,
        DateTime Timestamp,
        int FilesProcessed = 0,
        int TotalFiles = 0,
        double PercentComplete = 0,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when corruption removal completes.
    /// </summary>
    public record CorruptionRemovalComplete(
        bool Success,
        string Service,
        string StageKey,
        Guid? OperationId = null,
        string? Error = null,
        DateTime? Timestamp = null,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    #endregion

    #region Log Processing Notifications

    /// <summary>
    /// Notification when log processing completes (success, failure, or cancellation).
    /// Mirrors the prior <c>SendOperationCompleteAsync</c> wire shape (OperationId / Success / Status /
    /// Message / Cancelled + EntriesProcessed / LinesProcessed / Elapsed) so the frontend
    /// <c>LogProcessingCompleteEvent</c> contract is unchanged. <c>StageKey</c> is additive/optional.
    /// Emitted from a single place via <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record LogProcessingComplete(
        Guid? OperationId,
        bool Success,
        Models.OperationStatus Status,
        string Message,
        bool Cancelled = false,
        long EntriesProcessed = 0,
        long LinesProcessed = 0,
        double? Elapsed = null,
        string? StageKey = null,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when log removal completes (success, failure, or cancellation).
    /// Mirrors the prior <c>SendOperationCompleteAsync</c> wire shape (OperationId / Success / Status /
    /// Message / Cancelled + Service / FilesProcessed / LinesProcessed / LinesRemoved /
    /// DatabaseRecordsDeleted / Datasource) so the frontend <c>LogRemovalCompleteEvent</c> contract is
    /// unchanged. <c>StageKey</c> is additive/optional. Emitted from a single place via
    /// <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record LogRemovalComplete(
        Guid? OperationId,
        bool Success,
        Models.OperationStatus Status,
        string Message,
        string Service,
        bool Cancelled = false,
        int FilesProcessed = 0,
        long LinesProcessed = 0,
        long LinesRemoved = 0,
        int DatabaseRecordsDeleted = 0,
        string? Datasource = null,
        string? StageKey = null,
        Dictionary<string, object?>? Context = null
    );

    #endregion

    #region Detection Notifications

    /// <summary>
    /// Notification for game detection progress.
    /// </summary>
    public record GameDetectionProgress(
        Guid OperationId,
        string Status,
        string StageKey,
        int GamesDetected = 0,
        int ServicesDetected = 0,
        double ProgressPercent = 0,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when game detection completes (success, failure, or cancellation).
    /// Property names/casing mirror the previous anonymous payload exactly (serialized camelCase via
    /// the global <c>JsonNamingPolicy.CamelCase</c>) so the frontend <c>GameDetectionCompleteEvent</c>
    /// contract (status / cancelled / totalGamesDetected / totalServicesDetected / newGamesCount /
    /// timestamp) is unchanged. Emitted from a single place via <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record GameDetectionComplete(
        bool Success,
        Guid OperationId,
        string StageKey,
        Models.OperationStatus Status = Models.OperationStatus.Completed,
        bool Cancelled = false,
        int? TotalGamesDetected = null,
        int? TotalServicesDetected = null,
        int? NewGamesCount = null,
        DateTime? Timestamp = null,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    /// <summary>
    /// Notification for corruption detection progress.
    /// </summary>
    public record CorruptionDetectionProgress(
        Guid OperationId,
        string Status,
        string StageKey,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when corruption detection completes (success, failure, or cancellation).
    /// One record for ALL terminal paths (replaces the prior anon success object + the separate
    /// force-kill <c>CorruptionDetectionCancelled</c> record). Property names mirror the frontend
    /// <c>CorruptionDetectionCompleteEvent</c> contract (status / cancelled / error /
    /// totalServicesWithCorruption / totalCorruptedChunks). Emitted from a single place via
    /// <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record CorruptionDetectionComplete(
        bool Success,
        Guid OperationId,
        string StageKey,
        Models.OperationStatus Status = Models.OperationStatus.Completed,
        bool Cancelled = false,
        string? Error = null,
        int TotalServicesWithCorruption = 0,
        int TotalCorruptedChunks = 0,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    #endregion

    #region Cache Operations

    /// <summary>
    /// Notification for cache clear progress.
    /// </summary>
    public record CacheClearProgress(
        Guid OperationId,
        string Status,
        string StageKey,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        double ProgressPercent = 0,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when cache clear completes (success, failure, or cancellation).
    /// Mirrors the prior <c>SendOperationCompleteAsync</c> wire shape (OperationId / Success / Status /
    /// Message / Cancelled + FilesDeleted / DirectoriesProcessed / BytesDeleted / DatasourcesCleared /
    /// Duration / Error) so the frontend <c>CacheClearCompleteEvent</c> contract is unchanged.
    /// <c>StageKey</c> is additive/optional. Emitted from a single place via
    /// <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record CacheClearComplete(
        Guid? OperationId,
        bool Success,
        Models.OperationStatus Status,
        string Message,
        bool Cancelled = false,
        int FilesDeleted = 0,
        int DirectoriesProcessed = 0,
        long BytesDeleted = 0,
        int DatasourcesCleared = 0,
        double? Duration = null,
        string? Error = null,
        string? StageKey = null,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>
    /// Notification when a cache size scan completes. Replaces the prior <c>new { success = true }</c>
    /// anonymous payload. <c>OperationId</c> is optional because the scheduled scan is not a tracked
    /// operation. Emitted on the <c>CacheScanComplete</c> SignalR event.
    /// </summary>
    public record CacheScanComplete(
        bool Success,
        Guid? OperationId = null
    );

    /// <summary>
    /// Notification when a database reset completes on the NORMAL path (success or failure) or via
    /// cancellation. Additive terminal event for db reset (the legacy <c>DatabaseResetProgress</c>
    /// status completion is preserved until the frontend handler migrates). Emitted on the
    /// <c>DatabaseResetComplete</c> SignalR event from a single place via
    /// <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record DatabaseResetComplete(
        Guid? OperationId,
        bool Success,
        string StageKey,
        Models.OperationStatus Status = Models.OperationStatus.Completed,
        bool Cancelled = false,
        string? Error = null,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

    #endregion

    #region Client Groups

    /// <summary>
    /// Notification when a member is added to a client group.
    /// </summary>
    public record ClientGroupMemberAdded(long GroupId, string ClientIp);

    /// <summary>
    /// Notification when a member is removed from a client group.
    /// </summary>
    public record ClientGroupMemberRemoved(long GroupId, string ClientIp);

    #endregion

    #region Events

    /// <summary>
    /// Notification when a download is tagged with an event.
    /// </summary>
    public record DownloadTagged(long EventId, long DownloadId);

    #endregion
}
