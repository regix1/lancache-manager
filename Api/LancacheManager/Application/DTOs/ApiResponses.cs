namespace LancacheManager.Application.DTOs;

/// <summary>
/// Standard API response wrapper for consistent response format across all endpoints
/// </summary>
/// <typeparam name="T">The type of data being returned</typeparam>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }

    public static ApiResponse<T> Ok(T data, string? message = null) => new()
    {
        Success = true,
        Data = data,
        Message = message
    };

    public static ApiResponse<T> Fail(string error) => new()
    {
        Success = false,
        Error = error
    };
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
}

/// <summary>
/// Response for session information
/// </summary>
public class SessionResponse
{
    public string Id { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }
    public string? LocalIp { get; set; }
    public string? Hostname { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Browser { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsExpired { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }
    public string Type { get; set; } = string.Empty; // "authenticated" or "guest"
}

/// <summary>
/// Response for paginated session list
/// </summary>
public class SessionListResponse
{
    public List<SessionResponse> Sessions { get; set; } = new();
    public PaginationInfo Pagination { get; set; } = new();
    public int Count { get; set; }
    public int AuthenticatedCount { get; set; }
    public int GuestCount { get; set; }
}

/// <summary>
/// Pagination information
/// </summary>
public class PaginationInfo
{
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalCount { get; set; }
    public int TotalPages { get; set; }
    public bool HasNextPage { get; set; }
    public bool HasPreviousPage { get; set; }
}

/// <summary>
/// Response for system configuration
/// </summary>
public class SystemConfigResponse
{
    public string CachePath { get; set; } = string.Empty;
    public string LogsPath { get; set; } = string.Empty;
    public string DataPath { get; set; } = string.Empty;
    public string CacheDeleteMode { get; set; } = string.Empty;
    public string SteamAuthMode { get; set; } = string.Empty;
    public string TimeZone { get; set; } = "UTC";
    public bool CacheWritable { get; set; }
    public bool LogsWritable { get; set; }
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
/// Response for game detection results
/// </summary>
public class GameDetectionResponse
{
    public bool HasCachedResults { get; set; }
    public List<object>? Games { get; set; }
    public List<object>? Services { get; set; }
    public int TotalGamesDetected { get; set; }
    public int TotalServicesDetected { get; set; }
    public string? LastDetectionTime { get; set; }
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
/// Response for game list endpoint
/// </summary>
public class GameListResponse
{
    public string Message { get; set; } = string.Empty;
    public object? CachedResults { get; set; }
}

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
/// Response for resolving unknown games
/// </summary>
public class ResolveUnknownGamesResponse
{
    public bool Success { get; set; }
    public int ResolvedCount { get; set; }
    public string Message { get; set; } = string.Empty;
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

/// <summary>
/// Response for depot crawl interval update (supports fractional hours)
/// </summary>
public class DepotCrawlIntervalResponse
{
    public double IntervalHours { get; set; }
    public string Message { get; set; } = string.Empty;
}
