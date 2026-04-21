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
    /// Notification when log removal completes.
    /// </summary>
    public record LogRemovalComplete(
        bool Success,
        string Service,
        string StageKey,
        int FilesProcessed = 0,
        long LinesProcessed = 0,
        long LinesRemoved = 0,
        int DatabaseRecordsDeleted = 0,
        bool Cancelled = false,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification;

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
    /// Notification when game detection completes.
    /// </summary>
    public record GameDetectionComplete(
        bool Success,
        Guid OperationId,
        string StageKey,
        int GamesDetected = 0,
        int ServicesDetected = 0,
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
    /// Notification when corruption detection completes.
    /// </summary>
    public record CorruptionDetectionComplete(
        bool Success,
        string StageKey,
        Dictionary<string, int>? CorruptionCounts = null,
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
    /// Notification when cache clear completes.
    /// </summary>
    public record CacheClearComplete(
        bool Success,
        Guid OperationId,
        string StageKey,
        int FilesDeleted = 0,
        long BytesFreed = 0,
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
