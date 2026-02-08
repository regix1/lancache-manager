namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Typed SignalR notification payloads for consistent type safety across the application.
/// Using records for immutability and concise syntax.
/// </summary>
public static class SignalRNotifications
{
    /// <summary>
    /// Base interface for all completion notifications that have success/message pattern.
    /// </summary>
    public interface ICompletionNotification
    {
        bool Success { get; }
        string? Message { get; }
    }

    #region Removal Notifications

    /// <summary>
    /// Notification when game removal starts.
    /// </summary>
    public record GameRemovalStarted(
        string OperationId,
        int GameAppId,
        string GameName,
        string Message,
        DateTime Timestamp
    );

    /// <summary>
    /// Notification for game removal progress updates.
    /// </summary>
    public record GameRemovalProgress(
        string OperationId,
        int GameAppId,
        string GameName,
        string Status,
        string Message,
        int? FilesDeleted = null,
        long? BytesFreed = null
    );

    /// <summary>
    /// Notification when game removal completes (success or failure).
    /// </summary>
    public record GameRemovalComplete(
        bool Success,
        string OperationId,
        int GameAppId,
        string? GameName = null,
        string? Message = null,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        ulong LogEntriesRemoved = 0
    ) : ICompletionNotification;

    /// <summary>
    /// Notification when service removal starts.
    /// </summary>
    public record ServiceRemovalStarted(
        string ServiceName,
        string OperationId,
        string Message,
        DateTime Timestamp
    );

    /// <summary>
    /// Notification for service removal progress updates.
    /// </summary>
    public record ServiceRemovalProgress(
        string ServiceName,
        string OperationId,
        string Status,
        string Message,
        int? FilesDeleted = null,
        long? BytesFreed = null
    );

    /// <summary>
    /// Notification when service removal completes.
    /// </summary>
    public record ServiceRemovalComplete(
        bool Success,
        string ServiceName,
        string OperationId,
        string? Message = null,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        ulong LogEntriesRemoved = 0
    ) : ICompletionNotification;

    /// <summary>
    /// Notification for corruption removal started.
    /// </summary>
    public record CorruptionRemovalStarted(
        string Service,
        string OperationId,
        string Message,
        DateTime Timestamp
    );

    /// <summary>
    /// Notification for corruption removal progress.
    /// </summary>
    /// <summary>
    /// Notification for corruption removal progress.
    /// </summary>
    public record CorruptionRemovalProgress(
        string Service,
        string OperationId,
        string Status,
        string Message,
        DateTime Timestamp,
        int FilesProcessed = 0,
        int TotalFiles = 0,
        double PercentComplete = 0
    );

    /// <summary>
    /// Notification when corruption removal completes.
    /// </summary>
    public record CorruptionRemovalComplete(
        bool Success,
        string Service,
        string? OperationId = null,
        string? Message = null,
        string? Error = null,
        DateTime? Timestamp = null
    ) : ICompletionNotification;

    #endregion

    #region Log Processing Notifications

    /// <summary>
    /// Notification when log removal completes.
    /// </summary>
    public record LogRemovalComplete(
        bool Success,
        string Service,
        string Message,
        int FilesProcessed = 0,
        long LinesProcessed = 0,
        long LinesRemoved = 0,
        int DatabaseRecordsDeleted = 0,
        bool Cancelled = false
    ) : ICompletionNotification;

    #endregion

    #region Detection Notifications

    /// <summary>
    /// Notification for game detection progress.
    /// </summary>
    public record GameDetectionProgress(
        string OperationId,
        string Status,
        string Message,
        int GamesDetected = 0,
        int ServicesDetected = 0,
        double ProgressPercent = 0
    );

    /// <summary>
    /// Notification when game detection completes.
    /// </summary>
    public record GameDetectionComplete(
        bool Success,
        string OperationId,
        string? Message = null,
        int GamesDetected = 0,
        int ServicesDetected = 0
    ) : ICompletionNotification;

    /// <summary>
    /// Notification for corruption detection progress.
    /// </summary>
    public record CorruptionDetectionProgress(
        string OperationId,
        string Status,
        string Message
    );

    /// <summary>
    /// Notification when corruption detection completes.
    /// </summary>
    public record CorruptionDetectionComplete(
        bool Success,
        string? Message = null,
        Dictionary<string, int>? CorruptionCounts = null,
        int TotalServicesWithCorruption = 0,
        int TotalCorruptedChunks = 0
    ) : ICompletionNotification;

    #endregion

    #region Cache Operations

    /// <summary>
    /// Notification for cache clear progress.
    /// </summary>
    public record CacheClearProgress(
        string OperationId,
        string Status,
        string Message,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        double ProgressPercent = 0
    );

    /// <summary>
    /// Notification when cache clear completes.
    /// </summary>
    public record CacheClearComplete(
        bool Success,
        string OperationId,
        string? Message = null,
        int FilesDeleted = 0,
        long BytesFreed = 0,
        string? Error = null
    ) : ICompletionNotification;

    #endregion

    #region Client Groups

    /// <summary>
    /// Notification when a member is added to a client group.
    /// </summary>
    public record ClientGroupMemberAdded(int GroupId, string ClientIp);

    /// <summary>
    /// Notification when a member is removed from a client group.
    /// </summary>
    public record ClientGroupMemberRemoved(int GroupId, string ClientIp);

    #endregion

    #region Events

    /// <summary>
    /// Notification when a download is tagged with an event.
    /// </summary>
    public record DownloadTagged(int EventId, int DownloadId);

    #endregion
}
