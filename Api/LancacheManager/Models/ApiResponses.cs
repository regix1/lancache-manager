namespace LancacheManager.Models;

/// <summary>
/// Response for API version endpoint
/// </summary>
public class VersionResponse
{
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Response for async operations that return an operation ID for tracking
/// </summary>
public class OperationResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";

    public static OperationResponse Started(string operationId, string message) => new()
    {
        OperationId = operationId,
        Message = message,
        Status = "running"
    };
}

/// <summary>
/// Response for operation status checks
/// </summary>
public class OperationStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int? PercentComplete { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Response for cache information
/// </summary>
public class CacheInfoResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool Writable { get; set; }
    public long? TotalBytes { get; set; }
}

/// <summary>
/// Response for directory permissions
/// </summary>
public class PermissionsResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Writable { get; set; }
    public bool ReadOnly { get; set; }
}

/// <summary>
/// Response for authentication status
/// </summary>
public class AuthStatusResponse
{
    public bool RequiresAuth { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? AuthenticationType { get; set; }
    public string? DeviceId { get; set; }
    public string? AuthMode { get; set; }
    public int? GuestTimeRemaining { get; set; }
    public bool HasData { get; set; }
    public bool HasEverBeenSetup { get; set; }
    public bool HasBeenInitialized { get; set; }
    public bool HasDataLoaded { get; set; }
    // Prefill permission for guests
    public bool? PrefillEnabled { get; set; }
    public int? PrefillTimeRemaining { get; set; } // minutes
    // Whether the current device is banned from prefill
    public bool IsBanned { get; set; }
}

/// <summary>
/// Response for system configuration
/// </summary>
public class SystemConfigResponse
{
    /// <summary>
    /// Primary cache path (for backward compatibility).
    /// When multiple datasources are configured, this is the first datasource's cache path.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Primary logs path (for backward compatibility).
    /// When multiple datasources are configured, this is the first datasource's logs path.
    /// </summary>
    public string LogsPath { get; set; } = string.Empty;

    public string DataPath { get; set; } = string.Empty;
    public string CacheDeleteMode { get; set; } = string.Empty;
    public string SteamAuthMode { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public bool CacheWritable { get; set; }
    public bool LogsWritable { get; set; }

    /// <summary>
    /// List of all configured datasources.
    /// Empty list indicates single datasource mode (use CachePath/LogsPath).
    /// </summary>
    public List<DatasourceInfoDto> DataSources { get; set; } = new();
}

/// <summary>
/// Datasource information for API responses.
/// </summary>
public class DatasourceInfoDto
{
    /// <summary>
    /// Unique name/identifier for this datasource.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Resolved cache directory path.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Resolved logs directory path.
    /// </summary>
    public string LogsPath { get; set; } = string.Empty;

    /// <summary>
    /// Whether the cache directory is writable.
    /// </summary>
    public bool CacheWritable { get; set; }

    /// <summary>
    /// Whether the logs directory is writable.
    /// </summary>
    public bool LogsWritable { get; set; }

    /// <summary>
    /// Whether this datasource is enabled.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// Response for system state
/// </summary>
public class SystemStateResponse
{
    public bool SetupCompleted { get; set; }
    public bool HasDataLoaded { get; set; }
    public string SteamAuthMode { get; set; } = string.Empty;
    public string CacheDeleteMode { get; set; } = string.Empty;
}

/// <summary>
/// Response for Steam authentication status
/// </summary>
public class SteamAuthStatusResponse
{
    public string Mode { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public string AuthMode { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public bool HasStoredCredentials { get; set; }
}

/// <summary>
/// Response for depot/PICS status
/// </summary>
public class DepotStatusResponse
{
    public bool IsRebuilding { get; set; }
    public string Status { get; set; } = string.Empty;
    public int? Progress { get; set; }
    public string? Message { get; set; }
    public DateTime? LastRebuildTime { get; set; }
    public int TotalDepots { get; set; }
    public double CrawlIntervalHours { get; set; }
    public object? CrawlIncrementalMode { get; set; }
}

/// <summary>
/// Simple message response for operations that just return a message
/// </summary>
public class MessageResponse
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;

    public static MessageResponse Ok(string message) => new() { Success = true, Message = message };
}

/// <summary>
/// Response for API key status
/// </summary>
public class ApiKeyStatusResponse
{
    public bool HasApiKey { get; set; }
    public string KeyType { get; set; } = "none";
    public bool HasPrimaryKey { get; set; }
}

/// <summary>
/// Response for system permissions check
/// </summary>
public class SystemPermissionsResponse
{
    public DirectoryPermission Cache { get; set; } = new();
    public DirectoryPermission Logs { get; set; } = new();
    public DockerSocketPermission DockerSocket { get; set; } = new();
}

/// <summary>
/// Directory permission details
/// </summary>
public class DirectoryPermission
{
    public string Path { get; set; } = string.Empty;
    public bool Writable { get; set; }
    public bool ReadOnly { get; set; }
}

/// <summary>
/// Docker socket availability
/// </summary>
public class DockerSocketPermission
{
    public bool Available { get; set; }
}

/// <summary>
/// Response for setup status
/// </summary>
public class SetupStatusResponse
{
    public bool IsCompleted { get; set; }
    public bool HasProcessedLogs { get; set; }
    public bool SetupCompleted { get; set; } // Legacy field for backward compatibility
}

/// <summary>
/// Response for setup update
/// </summary>
public class SetupUpdateResponse
{
    public string Message { get; set; } = string.Empty;
    public bool SetupCompleted { get; set; }
}

/// <summary>
/// Response for rsync availability check
/// </summary>
public class RsyncAvailableResponse
{
    public bool Available { get; set; }
}

/// <summary>
/// Response for cache delete mode update
/// </summary>
public class CacheDeleteModeResponse
{
    public string Message { get; set; } = string.Empty;
    public string DeleteMode { get; set; } = string.Empty;
}

/// <summary>
/// Response for crawl interval update
/// </summary>
public class CrawlIntervalResponse
{
    public string Message { get; set; } = string.Empty;
    public int IntervalHours { get; set; }
}

/// <summary>
/// Response for scan mode update
/// </summary>
public class ScanModeResponse
{
    public string Message { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

/// <summary>
/// Response for Steam login operations
/// </summary>
public class SteamLoginResponse
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? AuthMode { get; set; }
    public string? Username { get; set; }
    public string? Status { get; set; }
    public bool RequiresTwoFactor { get; set; }
    public bool RequiresEmailCode { get; set; }
    public bool SessionExpired { get; set; }
}

/// <summary>
/// Response for Steam mode update
/// </summary>
public class SteamModeResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

/// <summary>
/// Response for GC settings
/// </summary>
public class GcSettingsResponse
{
    public string Aggressiveness { get; set; } = "disabled";
    public long MemoryThresholdMB { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Response for GC trigger operation
/// </summary>
public class GcTriggerResponse
{
    public bool Skipped { get; set; }
    public string? Reason { get; set; }
    public double? RemainingSeconds { get; set; }
    public double? BeforeMB { get; set; }
    public double? AfterMB { get; set; }
    public double? FreedMB { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for guest session configuration
/// </summary>
public class GuestConfigResponse
{
    public int DurationHours { get; set; }
    public bool IsLocked { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Response for guest duration update
/// </summary>
public class GuestDurationResponse
{
    public bool Success { get; set; }
    public int DurationHours { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for guest mode lock update
/// </summary>
public class GuestLockResponse
{
    public bool Success { get; set; }
    public bool IsLocked { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session clear operation
/// </summary>
public class SessionClearResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session heartbeat (last seen update)
/// </summary>
public class SessionHeartbeatResponse
{
    public bool Success { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Message { get; set; }
}

/// <summary>
/// Response for session creation
/// </summary>
public class SessionCreateResponse
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for session deletion
/// </summary>
public class SessionDeleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Service unavailable response with retry info
/// </summary>
public class ServiceUnavailableResponse
{
    public string Error { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int RetryAfter { get; set; }
}

/// <summary>
/// Response for cache operations (clear, removal)
/// </summary>
public class CacheOperationResponse
{
    public string Message { get; set; } = string.Empty;
    public string? OperationId { get; set; }
    public string? ServiceName { get; set; }
    public string? Service { get; set; }
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for active cache operations
/// </summary>
public class ActiveOperationsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<object>? Operations { get; set; }
}

/// <summary>
/// Response for removal status check
/// </summary>
public class RemovalStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public string? OperationId { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
    public string? Error { get; set; }
    public string? GameName { get; set; }
    public string? ServiceName { get; set; }
    public string? Service { get; set; }
}

// ============================================================
// Log Controller DTOs
// ============================================================

/// <summary>
/// Response for log directory information
/// </summary>
public class LogInfoResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Exists { get; set; }
}

/// <summary>
/// Response for log position reset operation
/// </summary>
public class LogPositionResponse
{
    public string Message { get; set; } = string.Empty;
    public long Position { get; set; }
}

/// <summary>
/// Response for log service removal start
/// </summary>
public class LogRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Response for cancellation operations
/// </summary>
public class CancellationResponse
{
    public string Message { get; set; } = string.Empty;
    public bool Cancelled { get; set; }
}

// ============================================================
// Game Controller DTOs
// ============================================================

/// <summary>
/// Response for game removal operation start
/// </summary>
public class GameRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public int AppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for game detection start
/// </summary>
public class GameDetectionStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for active detection status
/// </summary>
public class ActiveDetectionResponse
{
    public bool IsProcessing { get; set; }
    public object? Operation { get; set; }
}

/// <summary>
/// Response for cached detection results
/// </summary>
public class CachedDetectionResponse
{
    public bool HasCachedResults { get; set; }
    public object? Games { get; set; }
    public object? Services { get; set; }
    public int TotalGamesDetected { get; set; }
    public int TotalServicesDetected { get; set; }
    public string? LastDetectionTime { get; set; }
}

/// <summary>
/// Response for cached corruption detection results
/// </summary>
public class CachedCorruptionResponse
{
    public bool HasCachedResults { get; set; }
    public Dictionary<string, long>? CorruptionCounts { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
    public string? LastDetectionTime { get; set; }
}

// ============================================================
// Depot Controller DTOs
// ============================================================

/// <summary>
/// Response for depot status including JSON file and database info
/// </summary>
public class DepotFullStatusResponse
{
    public DepotJsonFileStatus JsonFile { get; set; } = new();
    public DepotDatabaseStatus Database { get; set; } = new();
    public DepotSteamKit2Status SteamKit2 { get; set; } = new();
}

public class DepotJsonFileStatus
{
    public bool Exists { get; set; }
    public string Path { get; set; } = string.Empty;
    public DateTime? LastUpdated { get; set; }
    public int TotalMappings { get; set; }
    public DateTime? NextUpdateDue { get; set; }
    public bool NeedsUpdate { get; set; }
}

public class DepotDatabaseStatus
{
    public int TotalMappings { get; set; }
}

public class DepotSteamKit2Status
{
    public bool IsReady { get; set; }
    public bool IsRebuildRunning { get; set; }
    public int DepotCount { get; set; }
}

/// <summary>
/// Response for depot rebuild viability pre-flight check
/// </summary>
public class DepotRebuildViabilityResponse
{
    public bool Started { get; set; }
    public bool RequiresFullScan { get; set; }
    public uint? ChangeGap { get; set; }
    public int? EstimatedApps { get; set; }
    public string? Message { get; set; }
    public string? ViabilityError { get; set; }
}

/// <summary>
/// Response for depot rebuild operation start
/// </summary>
public class DepotRebuildStartResponse
{
    public bool Started { get; set; }
    public bool RequiresFullScan { get; set; }
    public bool RebuildInProgress { get; set; }
    public bool Ready { get; set; }
    public int DepotCount { get; set; }
}

/// <summary>
/// Response for depot import operation
/// </summary>
public class DepotImportResponse
{
    public string Message { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Response for depot mapping application
/// </summary>
public class DepotMappingApplyResponse
{
    public string Message { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Response for crawl mode update
/// </summary>
public class CrawlModeResponse
{
    public object? IncrementalMode { get; set; }
    public string Message { get; set; } = string.Empty;
}

// ============================================================
// Cache Controller DTOs
// ============================================================

/// <summary>
/// Response for cache size calculation
/// </summary>
public class CacheSizeResponse
{
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public long TotalDirectories { get; set; }
    public int HexDirectories { get; set; }
    public long ScanDurationMs { get; set; }
    public EstimatedDeletionTimes EstimatedDeletionTimes { get; set; } = new();
    public string FormattedSize { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Estimated deletion times for different methods
/// </summary>
public class EstimatedDeletionTimes
{
    public double PreserveSeconds { get; set; }
    public double FullSeconds { get; set; }
    public double RsyncSeconds { get; set; }
    public string PreserveFormatted { get; set; } = string.Empty;
    public string FullFormatted { get; set; } = string.Empty;
    public string RsyncFormatted { get; set; } = string.Empty;
}

/// <summary>
/// Response for cache clear operation start
/// </summary>
public class CacheClearStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for service removal operation start
/// </summary>
public class ServiceRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for corruption removal operation start
/// </summary>
public class CorruptionRemovalStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string Service { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for active corruption removals
/// </summary>
public class ActiveCorruptionRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<CorruptionRemovalInfo>? Operations { get; set; }
}

public class CorruptionRemovalInfo
{
    public string Service { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for active service removals
/// </summary>
public class ActiveServiceRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<ServiceRemovalInfo>? Operations { get; set; }
}

public class ServiceRemovalInfo
{
    public string ServiceName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for active game removals
/// </summary>
public class ActiveGameRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<GameRemovalInfo>? Operations { get; set; }
}

public class GameRemovalInfo
{
    public int GameAppId { get; set; }
    public string GameName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Message { get; set; }
    public int FilesDeleted { get; set; }
    public long BytesFreed { get; set; }
    public DateTime? StartedAt { get; set; }
}

/// <summary>
/// Response for all active removals (games, services, corruption)
/// </summary>
public class AllActiveRemovalsResponse
{
    public bool IsProcessing { get; set; }
    public IEnumerable<GameRemovalInfo>? GameRemovals { get; set; }
    public IEnumerable<ServiceRemovalInfo>? ServiceRemovals { get; set; }
    public IEnumerable<CorruptionRemovalInfo>? CorruptionRemovals { get; set; }
}

/// <summary>
/// Response for not found errors
/// </summary>
public class NotFoundResponse
{
    public string Error { get; set; } = string.Empty;
    public string? OperationId { get; set; }
}

/// <summary>
/// Response for conflict errors (e.g., operation already running)
/// </summary>
public class ConflictResponse
{
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Generic error response for BadRequest/validation errors
/// </summary>
public class ErrorResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? Message { get; set; }
}

// ============================================================
// Database Controller DTOs
// ============================================================

/// <summary>
/// Response for database reset operation start
/// </summary>
public class DatabaseResetStartResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for selected tables reset operation start
/// </summary>
public class SelectedTablesResetResponse
{
    public string Message { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public List<string> Tables { get; set; } = new();
    public string Status { get; set; } = "running";
}

/// <summary>
/// Response for database reset status
/// </summary>
public class DatabaseResetStatusResponse
{
    public bool IsProcessing { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }
    public int? PercentComplete { get; set; }
}

/// <summary>
/// Response for log entries count
/// </summary>
public class LogEntriesCountResponse
{
    public long Count { get; set; }
}

// ============================================================
// Data Migration Controller DTOs
// ============================================================

/// <summary>
/// Response for data migration import result
/// </summary>
public class MigrationImportResponse
{
    public string Message { get; set; } = string.Empty;
    public ulong TotalRecords { get; set; }
    public ulong Imported { get; set; }
    public ulong Skipped { get; set; }
    public ulong Errors { get; set; }
    public string? BackupPath { get; set; }
}

/// <summary>
/// Response for connection validation
/// </summary>
public class ConnectionValidationResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
    public int? RecordCount { get; set; }
}

// ============================================================
// Devices Controller DTOs
// ============================================================

/// <summary>
/// Response for device list
/// </summary>
public class DeviceListResponse
{
    public List<object> Devices { get; set; } = new();
    public int Count { get; set; }
}

// ============================================================
// File Browser Controller DTOs
// ============================================================

/// <summary>
/// Response for directory listing
/// </summary>
public class DirectoryListResponse
{
    public string CurrentPath { get; set; } = string.Empty;
    public string? ParentPath { get; set; }
    public List<FileSystemItemDto> Items { get; set; } = new();
}

/// <summary>
/// File system item DTO
/// </summary>
public class FileSystemItemDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public DateTime LastModified { get; set; }
    public bool IsAccessible { get; set; } = true;
}

/// <summary>
/// Response for file search results
/// </summary>
public class FileSearchResponse
{
    public string SearchPath { get; set; } = string.Empty;
    public string Pattern { get; set; } = string.Empty;
    public List<FileSystemItemDto> Results { get; set; } = new();
}

// ============================================================
// Game Images Controller DTOs
// ============================================================

/// <summary>
/// Response for game image errors
/// </summary>
public class GameImageErrorResponse
{
    public string Error { get; set; } = string.Empty;
}

// ============================================================
// Operation State Controller DTOs
// ============================================================

/// <summary>
/// Response for save state operation
/// </summary>
public class SaveStateResponse
{
    public bool Success { get; set; }
    public string Key { get; set; } = string.Empty;
}

/// <summary>
/// Response for operation state update
/// </summary>
public class StateUpdateResponse
{
    public bool Success { get; set; }
}

/// <summary>
/// Response for cleanup operation
/// </summary>
public class StateCleanupResponse
{
    public bool Success { get; set; }
    public int ActiveStates { get; set; }
}

// ============================================================
// Stats Controller DTOs
// ============================================================

/// <summary>
/// Response for dashboard stats
/// </summary>
public class DashboardStatsResponse
{
    // All-time metrics
    public long TotalBandwidthSaved { get; set; }
    public long TotalAddedToCache { get; set; }
    public long TotalServed { get; set; }
    public double CacheHitRatio { get; set; }

    // Current status
    public int ActiveDownloads { get; set; }
    public int UniqueClients { get; set; }
    public string TopService { get; set; } = string.Empty;

    // Period-specific metrics
    public DashboardPeriodStats Period { get; set; } = new();

    // Service breakdown
    public List<ServiceBreakdownItem> ServiceBreakdown { get; set; } = new();

    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Period-specific stats for dashboard
/// </summary>
public class DashboardPeriodStats
{
    public string Duration { get; set; } = string.Empty;
    public DateTime? Since { get; set; }
    public long BandwidthSaved { get; set; }
    public long AddedToCache { get; set; }
    public long TotalServed { get; set; }
    public double HitRatio { get; set; }
    public int Downloads { get; set; }
}

/// <summary>
/// Service breakdown item for dashboard
/// </summary>
public class ServiceBreakdownItem
{
    public string Service { get; set; } = string.Empty;
    public long Bytes { get; set; }
    public double Percentage { get; set; }
}

// ============================================================
// Dashboard Analytics DTOs
// ============================================================

/// <summary>
/// Response for hourly activity data (Peak Usage Hours widget)
/// </summary>
public class HourlyActivityResponse
{
    /// <summary>
    /// Activity data for each hour of the day (0-23)
    /// </summary>
    public List<HourlyActivityItem> Hours { get; set; } = new();

    /// <summary>
    /// Hour with the most downloads (0-23)
    /// </summary>
    public int PeakHour { get; set; }

    /// <summary>
    /// Total downloads in the period
    /// </summary>
    public int TotalDownloads { get; set; }

    /// <summary>
    /// Total bytes served in the period
    /// </summary>
    public long TotalBytesServed { get; set; }

    /// <summary>
    /// Number of distinct days in the queried period
    /// </summary>
    public int DaysInPeriod { get; set; } = 1;

    /// <summary>
    /// Start of the data range (Unix timestamp)
    /// </summary>
    public long? PeriodStart { get; set; }

    /// <summary>
    /// End of the data range (Unix timestamp)
    /// </summary>
    public long? PeriodEnd { get; set; }

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;
}

/// <summary>
/// Activity data for a single hour
/// </summary>
public class HourlyActivityItem
{
    /// <summary>
    /// Hour of day (0-23)
    /// </summary>
    public int Hour { get; set; }

    /// <summary>
    /// Number of downloads that started in this hour (total across all days in period)
    /// </summary>
    public int Downloads { get; set; }

    /// <summary>
    /// Average downloads per day for this hour (Downloads / DaysInPeriod)
    /// </summary>
    public double AvgDownloads { get; set; }

    /// <summary>
    /// Total bytes served in this hour (total across all days in period)
    /// </summary>
    public long BytesServed { get; set; }

    /// <summary>
    /// Average bytes served per day for this hour
    /// </summary>
    public long AvgBytesServed { get; set; }

    /// <summary>
    /// Cache hit bytes in this hour
    /// </summary>
    public long CacheHitBytes { get; set; }

    /// <summary>
    /// Cache miss bytes in this hour
    /// </summary>
    public long CacheMissBytes { get; set; }
}

/// <summary>
/// Response for cache growth data over time
/// </summary>
public class CacheGrowthResponse
{
    /// <summary>
    /// Data points showing cache growth over time
    /// </summary>
    public List<CacheGrowthDataPoint> DataPoints { get; set; } = new();

    /// <summary>
    /// Current total cache size (used space)
    /// </summary>
    public long CurrentCacheSize { get; set; }

    /// <summary>
    /// Total cache capacity
    /// </summary>
    public long TotalCapacity { get; set; }

    /// <summary>
    /// Average daily growth in bytes
    /// </summary>
    public long AverageDailyGrowth { get; set; }

    /// <summary>
    /// Trend direction: up, down, or stable
    /// </summary>
    public string Trend { get; set; } = "stable";

    /// <summary>
    /// Percentage change over the period
    /// </summary>
    public double PercentChange { get; set; }

    /// <summary>
    /// Estimated days until cache is full (null if not growing or already full)
    /// </summary>
    public int? EstimatedDaysUntilFull { get; set; }

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;

    /// <summary>
    /// True if the actual cache size is less than cumulative downloads,
    /// indicating data was deleted (cache was cleared/cleaned)
    /// </summary>
    public bool HasDataDeletion { get; set; }

    /// <summary>
    /// Estimated bytes that were deleted from cache
    /// (difference between cumulative downloads and actual cache size)
    /// </summary>
    public long EstimatedBytesDeleted { get; set; }

    /// <summary>
    /// Net average daily growth (accounting for deletions)
    /// Can be negative if cache is shrinking
    /// </summary>
    public long NetAverageDailyGrowth { get; set; }

    /// <summary>
    /// True if the cache was essentially cleared (very small relative to historical downloads).
    /// When true, percentChange is not meaningful and growth rate shows download rate.
    /// </summary>
    public bool CacheWasCleared { get; set; }
}

/// <summary>
/// Single data point for cache growth
/// </summary>
public class CacheGrowthDataPoint
{
    /// <summary>
    /// Timestamp for this data point
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Cumulative cache miss bytes (new data added) up to this point
    /// </summary>
    public long CumulativeCacheMissBytes { get; set; }

    /// <summary>
    /// Growth from previous data point
    /// </summary>
    public long GrowthFromPrevious { get; set; }
}

/// <summary>
/// Response containing sparkline data for dashboard stat cards
/// </summary>
public class SparklineDataResponse
{
    /// <summary>
    /// Sparkline data for bandwidth saved metric
    /// </summary>
    public SparklineMetric BandwidthSaved { get; set; } = new();

    /// <summary>
    /// Sparkline data for cache hit ratio metric
    /// </summary>
    public SparklineMetric CacheHitRatio { get; set; } = new();

    /// <summary>
    /// Sparkline data for total served metric
    /// </summary>
    public SparklineMetric TotalServed { get; set; } = new();

    /// <summary>
    /// Sparkline data for added to cache metric
    /// </summary>
    public SparklineMetric AddedToCache { get; set; } = new();

    /// <summary>
    /// Time period for this data
    /// </summary>
    public string Period { get; set; } = string.Empty;
}

/// <summary>
/// Sparkline data for a single metric
/// </summary>
public class SparklineMetric
{
    /// <summary>
    /// Actual data points for the sparkline (values only, ordered by time).
    /// </summary>
    public List<double> Data { get; set; } = new();

    /// <summary>
    /// Predicted future data points based on linear regression.
    /// These should be displayed in a different color to indicate they are projections.
    /// </summary>
    public List<double> PredictedData { get; set; } = new();

    /// <summary>
    /// Trend direction: up, down, or stable.
    /// Based on linear regression of the data points.
    /// </summary>
    public string Trend { get; set; } = "stable";

    /// <summary>
    /// Change value calculated using linear regression on the data points.
    /// For regular metrics: percentage change from trendline end to predicted end (3 days ahead).
    /// For ratio metrics (IsAbsoluteChange=true): absolute point change over the same forecast window.
    /// </summary>
    public double PercentChange { get; set; }

    /// <summary>
    /// When true, PercentChange represents absolute points (not percentage).
    /// Used for ratio metrics like cache hit ratio where showing "percent of percent" is confusing.
    /// </summary>
    public bool IsAbsoluteChange { get; set; }
}

// ============================================================
// Steam API Keys Controller DTOs
// ============================================================

/// <summary>
/// Response for Steam API status
/// </summary>
public class SteamApiStatusResponse
{
    public string Version { get; set; } = string.Empty;
    public bool IsV2Available { get; set; }
    public bool IsV1Available { get; set; }
    public bool HasApiKey { get; set; }
    public bool IsFullyOperational { get; set; }
    public string? Message { get; set; }
    public DateTime LastChecked { get; set; }
}

/// <summary>
/// Response for API key test
/// </summary>
public class ApiKeyTestResponse
{
    public bool Valid { get; set; }
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for API key save
/// </summary>
public class ApiKeySaveResponse
{
    public string Message { get; set; } = string.Empty;
    public bool Encrypted { get; set; }
}

/// <summary>
/// Response for API key removal
/// </summary>
public class ApiKeyRemoveResponse
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for app list
/// </summary>
public class AppListResponse
{
    public int Total { get; set; }
    public int Returned { get; set; }
    public List<object> Apps { get; set; } = new();
}

// ============================================================
// Theme Controller DTOs
// ============================================================

/// <summary>
/// Response for theme upload
/// </summary>
public class ThemeUploadResponse
{
    public bool Success { get; set; }
    public string ThemeId { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Response for theme deletion
/// </summary>
public class ThemeDeleteResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> FilesDeleted { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Response for theme not found with available themes
/// </summary>
public class ThemeNotFoundResponse
{
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string[] AvailableThemes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for theme cleanup
/// </summary>
public class ThemeCleanupResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public List<string> DeletedThemes { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string[] RemainingThemes { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Response for theme preference get/set
/// </summary>
public class ThemePreferenceResponse
{
    public string ThemeId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Message { get; set; }
}

// ============================================================
// User Preferences Controller DTOs
// ============================================================

/// <summary>
/// Response for preferences update
/// </summary>
public class PreferencesUpdateResponse
{
    public string Message { get; set; } = string.Empty;
}

// ============================================================
// Additional Depot Controller DTOs
// ============================================================

/// <summary>
/// Response for depot rebuild cancel
/// </summary>
public class DepotRebuildCancelResponse
{
    public string Message { get; set; } = string.Empty;
}

// ============================================================
// Memory Controller DTOs
// ============================================================

/// <summary>
/// Response for memory statistics
/// </summary>
public class MemoryStatsResponse
{
    public DateTime Timestamp { get; set; }
    // System Memory
    public double TotalSystemMemoryMB { get; set; }
    public double TotalSystemMemoryGB { get; set; }
    // Process Memory
    public double WorkingSetMB { get; set; }
    public double WorkingSetGB { get; set; }
    public double ManagedMB { get; set; }
    public double ManagedGB { get; set; }
    public double UnmanagedMB { get; set; }
    public double UnmanagedGB { get; set; }
    // Managed Memory Details
    public double TotalAllocatedMB { get; set; }
    public double TotalAllocatedGB { get; set; }
    public double HeapSizeMB { get; set; }
    public double HeapSizeGB { get; set; }
    public double FragmentedMB { get; set; }
    public double FragmentedGB { get; set; }
    // Process Statistics
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
}

// ============================================================
// Metrics Controller DTOs
// ============================================================

/// <summary>
/// Response for metrics endpoint status
/// </summary>
public class MetricsStatusResponse
{
    public bool RequiresAuthentication { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string AuthMethod { get; set; } = string.Empty;
}

// ============================================================
// Common API Response Helpers
// ============================================================

/// <summary>
/// Static factory methods for common API responses.
/// Reduces inline anonymous object creation across controllers.
/// </summary>
public static class ApiResponse
{
    // ==================== Error Responses ====================

    /// <summary>Creates a standard error response object.</summary>
    public static ErrorResponse Error(string error, string? details = null) => new()
    {
        Error = error,
        Details = details
    };

    /// <summary>Creates a not found response for a specific entity type.</summary>
    public static NotFoundResponse NotFound(string entityType) => new()
    {
        Error = $"{entityType} not found"
    };

    /// <summary>Creates a not found response with operation ID.</summary>
    public static NotFoundResponse NotFound(string entityType, string operationId) => new()
    {
        Error = $"{entityType} not found",
        OperationId = operationId
    };

    /// <summary>Creates a conflict response (e.g., operation already running).</summary>
    public static ConflictResponse Conflict(string error) => new()
    {
        Error = error
    };

    // ==================== Success Responses ====================

    /// <summary>Creates a simple success message response.</summary>
    public static MessageResponse Success(string message) => new()
    {
        Success = true,
        Message = message
    };

    /// <summary>Creates a message-only response object.</summary>
    public static object Message(string message) => new { message };

    /// <summary>Creates a success response with custom data.</summary>
    public static object Ok(string message) => new { message };

    // ==================== Validation Responses ====================

    /// <summary>Creates an error response for missing required fields.</summary>
    public static ErrorResponse Required(string fieldName) => new()
    {
        Error = $"{fieldName} is required"
    };

    /// <summary>Creates an error response for invalid values.</summary>
    public static ErrorResponse Invalid(string message) => new()
    {
        Error = message
    };

    /// <summary>Creates an error response for duplicate entries.</summary>
    public static ErrorResponse Duplicate(string entityType, string fieldName) => new()
    {
        Error = $"A {entityType.ToLower()} with this {fieldName.ToLower()} already exists"
    };

    // ==================== Internal Error Responses ====================

    /// <summary>Creates an internal server error response.</summary>
    public static ErrorResponse InternalError(string operation) => new()
    {
        Error = $"An error occurred while {operation}. Check server logs for details."
    };

    /// <summary>Creates an internal server error response with details.</summary>
    public static ErrorResponse InternalError(string operation, string details) => new()
    {
        Error = $"An error occurred while {operation}",
        Details = details
    };
}
