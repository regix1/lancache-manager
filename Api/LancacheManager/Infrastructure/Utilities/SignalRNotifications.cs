using LancacheManager.Models;

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
        Dictionary<string, object?>? Context = null,
        // Additive terminal fields (appended so positional callers are unaffected) — guarantee the
        // shared IOperationComplete contract on the failure/cancel paths.
        string? Error = null,
        bool Cancelled = false
    ) : ICompletionNotification, IOperationComplete
    {
        Guid? IOperationComplete.OperationId => OperationId;
        OperationStatus IOperationComplete.Status =>
            Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
    }

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
        Dictionary<string, object?>? Context = null,
        // Additive terminal fields (appended so positional callers are unaffected) — guarantee the
        // shared IOperationComplete contract on the failure/cancel paths.
        string? Error = null,
        bool Cancelled = false
    ) : ICompletionNotification, IOperationComplete
    {
        Guid? IOperationComplete.OperationId => OperationId;
        OperationStatus IOperationComplete.Status =>
            Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
    }

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
    ) : ICompletionNotification, IOperationComplete
    {
        OperationStatus IOperationComplete.Status => Success ? OperationStatus.Completed : OperationStatus.Failed;
        bool IOperationComplete.Cancelled => false;
    }

    /// <summary>Notification when an exact historical-evidence purge starts.</summary>
    public record HistoricalEvidencePurgeStarted(
        Guid OperationId,
        string Scope,
        int CandidateCount,
        string StageKey,
        DateTime Timestamp,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>Progress for an exact historical-evidence purge. Cache-file counters are absent by design.</summary>
    public record HistoricalEvidencePurgeProgress(
        Guid OperationId,
        string Scope,
        string Status,
        string StageKey,
        double PercentComplete = 0,
        long LogLinesRemoved = 0,
        long LogEntriesRemoved = 0,
        long DownloadsDeleted = 0,
        Dictionary<string, object?>? Context = null
    );

    /// <summary>Single terminal payload for successful, failed, or cancelled evidence purges.</summary>
    public record HistoricalEvidencePurgeComplete(
        Guid? OperationId,
        bool Success,
        Models.OperationStatus Status,
        string Scope,
        string StageKey,
        bool Cancelled = false,
        string? Error = null,
        int CandidateCount = 0,
        long LogLinesRemoved = 0,
        long LogEntriesRemoved = 0,
        long DownloadsDeleted = 0,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification, IOperationComplete;

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
    ) : IOperationComplete
    {
        // No dedicated Error field: on failure the human-readable reason rides in Message.
        string? IOperationComplete.Error => Success ? null : Message;
    }

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
    ) : IOperationComplete
    {
        // No dedicated Error field: on failure the human-readable reason rides in Message.
        string? IOperationComplete.Error => Success ? null : Message;
    }

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
        Dictionary<string, object?>? Context = null,
        // Additive terminal field (appended so positional callers are unaffected) — guarantees the
        // shared IOperationComplete contract on the failure path.
        string? Error = null
    ) : ICompletionNotification, IOperationComplete
    {
        Guid? IOperationComplete.OperationId => OperationId;
    }

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
    /// Progress for a single-service "view corrupted chunk details" fetch. Deliberately a
    /// distinct event from <see cref="CorruptionDetectionProgress"/> - the details fetch is a
    /// read-only per-row lookup, not the bulk scan, and must not feed the global
    /// 'corruption_detection' notification card (that would make the Scan button and notification
    /// bar think a full scan is running just because a service's details are expanded).
    /// </summary>
    public record CorruptionDetailsProgress(
        Guid OperationId,
        string Service,
        double PercentComplete,
        int FilesProcessed,
        int TotalFiles
    );

    /// <summary>
    /// Notification when corruption detection completes (success, failure, or cancellation).
    /// One record for ALL terminal paths (replaces the prior anon success object + the separate
    /// force-kill <c>CorruptionDetectionCancelled</c> record). Property names mirror the frontend
    /// <c>CorruptionDetectionCompleteEvent</c> contract (status / cancelled / error /
    /// totalServicesWithCorruption / totalCorruptedChunks) and add the v2 removable/review-only
    /// maps and totals. Emitted from a single place via <c>OperationInfo.OnTerminalEmit</c>.
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
        Dictionary<string, long>? RemovableServiceCounts = null,
        Dictionary<string, long>? ReviewOnlyServiceCounts = null,
        long RemovableTotal = 0,
        long ReviewOnlyTotal = 0,
        Dictionary<string, object?>? Context = null
    ) : ICompletionNotification, IOperationComplete
    {
        Guid? IOperationComplete.OperationId => OperationId;
    }

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
    ) : IOperationComplete;

    /// <summary>
    /// Notification when a cache size scan completes. Replaces the prior <c>new { success = true }</c>
    /// anonymous payload. <c>OperationId</c> is optional because the scheduled scan is not a tracked
    /// operation. Emitted on the <c>CacheScanComplete</c> SignalR event.
    /// </summary>
    public record CacheScanComplete(
        bool Success,
        Guid? OperationId = null,
        // Additive terminal fields (appended so positional callers are unaffected) — guarantee the
        // shared IOperationComplete contract on the failure path.
        string? Error = null,
        bool Cancelled = false
    ) : IOperationComplete
    {
        OperationStatus IOperationComplete.Status =>
            Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
    }

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
    ) : ICompletionNotification, IOperationComplete;

    #endregion

    #region Mapping / Import Notifications

    /// <summary>
    /// Notification when the Steam depot mapping scan completes (success, failure, or cancellation).
    /// Property names/casing mirror the previous anonymous payloads emitted on the
    /// <c>DepotMappingComplete</c> SignalR event EXACTLY (serialized camelCase via the global
    /// <c>JsonNamingPolicy.CamelCase</c>): success carried
    /// <c>success / message / totalMappings / downloadsUpdated / scanMode / isLoggedOn / timestamp</c>;
    /// cancel carried <c>operationId / success / cancelled / message / isLoggedOn / timestamp</c>;
    /// error carried <c>success / message / error / isLoggedOn / timestamp</c>. <c>OperationId</c> is
    /// additive on the success/error paths (the prior anon omitted it there) and optional everywhere.
    /// Emitted from a single place via <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record DepotMappingComplete(
        Guid? OperationId,
        bool Success,
        string Message,
        bool Cancelled = false,
        int? TotalMappings = null,
        int? DownloadsUpdated = null,
        Models.DepotScanMode? ScanMode = null,
        bool IsLoggedOn = false,
        string? Error = null,
        DateTime? Timestamp = null
    ) : IOperationComplete
    {
        OperationStatus IOperationComplete.Status =>
            Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
    }

    /// <summary>
    /// Notification when the Epic catalog mapping / auth-login terminal state is reached (success,
    /// failure, or cancellation). Emitted on the <c>EpicMappingProgress</c> SignalR event (there is no
    /// dedicated <c>EpicMappingComplete</c> event const — the terminal rides the progress event, exactly
    /// as the prior anonymous payloads did). Property names/casing mirror those terminal anons EXACTLY
    /// (<c>operationId / status / percentComplete / gamesDiscovered / stageKey / cancelled / context</c>);
    /// <c>status</c> is the <c>OperationStatus</c> enum (Completed / Failed) as before. <c>Success</c> is
    /// carried additively (the prior anon conveyed success via <c>status</c> only). Emitted from a single
    /// place via <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record EpicMappingComplete(
        Guid? OperationId,
        bool Success,
        Models.OperationStatus Status,
        string StageKey,
        double PercentComplete = 100.0,
        int GamesDiscovered = 0,
        bool Cancelled = false,
        string? Error = null,
        Dictionary<string, object?>? Context = null,
        string? Message = null
    ) : IOperationComplete;

    /// <summary>
    /// Notification when a data import completes (success, failure, or cancellation). Property
    /// names/casing mirror the previous anonymous payloads emitted on the <c>DataImportComplete</c>
    /// SignalR event EXACTLY (<c>operationId / success / message</c> on every path; the rich success
    /// paths additionally carried <c>recordsImported / recordsSkipped / recordsErrors / totalRecords</c>).
    /// Emitted from a single place via <c>OperationInfo.OnTerminalEmit</c>.
    /// </summary>
    public record DataImportComplete(
        Guid OperationId,
        bool Success,
        string Message,
        bool Cancelled = false,
        ulong? RecordsImported = null,
        ulong? RecordsSkipped = null,
        ulong? RecordsErrors = null,
        ulong? TotalRecords = null
    ) : IOperationComplete
    {
        Guid? IOperationComplete.OperationId => OperationId;
        OperationStatus IOperationComplete.Status =>
            Cancelled ? OperationStatus.Cancelled : Success ? OperationStatus.Completed : OperationStatus.Failed;
        // No dedicated Error field: on failure the human-readable reason rides in Message.
        string? IOperationComplete.Error => Success ? null : Message;
    }

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
