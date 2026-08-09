using System.Text.Json.Serialization;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Services;

namespace LancacheManager.Models;

/// <summary>
/// Response for API version endpoint
/// </summary>
public class VersionResponse
{
    public string Version { get; set; } = string.Empty;
}

/// <summary>
/// Response for the liveness and readiness endpoints. SetupRequired is true when the app booted
/// without database credentials and can serve nothing but the setup wizard.
/// </summary>
public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public bool SetupRequired { get; set; }
    public DateTime Timestamp { get; set; }
    public string Service { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
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
    public CacheDeleteMode CacheDeleteMode { get; set; } = CacheDeleteMode.Preserve;
    public SteamAuthMode SteamAuthMode { get; set; } = SteamAuthMode.Anonymous;
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

    /// <summary>
    /// User-defined cache-size limit in bytes, or null when automatic detection is active.
    /// </summary>
    public long? CacheSizeOverrideBytes { get; set; }

    /// <summary>
    /// Effective configured limit in bytes. Zero means no configured limit was found.
    /// </summary>
    public long ResolvedCacheSizeBytes { get; set; }

    /// <summary>
    /// Effective limit source: manual, docker, env, or fullDisk.
    /// </summary>
    public string CacheSizeSource { get; set; } = CacheSizeSourceValues.FullDisk;

    /// <summary>
    /// Configured scheme override: auto, monolithic, or bare_metal.
    /// </summary>
    public string SchemeOverride { get; set; } = "auto";

    /// <summary>
    /// Effective cache-key scheme: monolithic, bare_metal, mixed, or unknown.
    /// </summary>
    public string CacheKeyScheme { get; set; } = "unknown";

    /// <summary>
    /// Reason object-scoped disk features are unavailable, or null when allowed.
    /// </summary>
    public string? CapabilityDenialReason { get; set; }

    /// <summary>
    /// Presentation-only source layout: monolithic | bare_metal | mixed.
    /// </summary>
    public string Layout { get; set; } = "monolithic";

    /// <summary>
    /// Number of logical access-log sources currently on disk.
    /// </summary>
    public int SourceCount { get; set; }

    /// <summary>
    /// Object-scoped disk features (game/service removal, corruption mapping, eviction)
    /// are available when current evidence selects one unambiguous cache-key scheme.
    /// </summary>
    public bool CanMapLogicalObjects { get; set; }

    /// <summary>
    /// Whole-root cache clear needs no key knowledge and stays available everywhere.
    /// </summary>
    public bool CanClearWholeCacheRoot { get; set; }

    /// <summary>
    /// Whether the manager can currently reopen nginx after rewriting this datasource's logs.
    /// </summary>
    public bool NginxReopenAvailable { get; set; }

    /// <summary>
    /// Action needed to make nginx reopen available, or null when it is already available.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public NginxReopenHint? NginxReopenHint { get; set; }
}

/// <summary>
/// Result of setting or clearing one datasource cache-size override.
/// </summary>
public class DatasourceCacheSizeResponse
{
    public string Name { get; set; } = string.Empty;

    /// <summary>The manual override in bytes. Null when the datasource uses automatic size detection instead.</summary>
    public long? CacheSizeOverrideBytes { get; set; }
    public long ResolvedCacheSizeBytes { get; set; }
    public string CacheSizeSource { get; set; } = CacheSizeSourceValues.FullDisk;
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
    public bool Exists { get; set; } = true;
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
    public bool NeedsPostgresCredentials { get; set; }

    /// <summary>
    /// The wizard step in progress. Null once setup has completed, or before the wizard has recorded
    /// a step.
    /// </summary>
    public string? CurrentSetupStep { get; set; }

    /// <summary>
    /// The data source the wizard is set up for. Null once setup has completed, at which point the
    /// wizard state is cleared.
    /// </summary>
    public string? DataSourceChoice { get; set; }

    /// <summary>
    /// Platforms the wizard has finished configuring so far. Null once setup has completed, at which
    /// point the wizard state is cleared.
    /// </summary>
    public string? CompletedPlatforms { get; set; }

    // Postgres deployment mode: "embedded" (default) or "external".
    public string Mode { get; set; } = "embedded";

    /// <summary>
    /// Configured Postgres host. Null while <see cref="NeedsPostgresCredentials"/> is true and
    /// nothing has determined a host yet.
    /// </summary>
    public string? PostgresHost { get; set; }

    /// <summary>
    /// Configured Postgres port. Null in embedded mode (a fixed Unix socket is used instead) and
    /// while <see cref="NeedsPostgresCredentials"/> is true.
    /// </summary>
    public int? PostgresPort { get; set; }

    /// <summary>
    /// Configured Postgres database name. Null while <see cref="NeedsPostgresCredentials"/> is true
    /// and nothing has determined a database yet.
    /// </summary>
    public string? PostgresDatabase { get; set; }

    /// <summary>
    /// Configured Postgres username. Null while <see cref="NeedsPostgresCredentials"/> is true and
    /// nothing has determined a username yet.
    /// </summary>
    public string? PostgresUser { get; set; }
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
/// Response for refresh rate setting
/// </summary>
public class RefreshRateResponse
{
    /// <summary>
    /// Confirmation text for a successful update. Null on a plain read of the current rate; only the
    /// update actions set it.
    /// </summary>
    public string? Message { get; set; }
    public string RefreshRate { get; set; } = string.Empty;
}

/// <summary>
/// Response for GC management status.
/// </summary>
public class GcStatusResponse
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Response for the default guest refresh rate.
/// </summary>
public class DefaultGuestRefreshRateResponse
{
    public string RefreshRate { get; set; } = string.Empty;
    public bool Locked { get; set; }
}

/// <summary>
/// Response for locking or unlocking guest refresh rate selection.
/// </summary>
public class GuestRefreshRateLockResponse
{
    public bool Success { get; set; }
    public bool Locked { get; set; }
}

/// <summary>
/// Response for default guest preferences.
/// </summary>
public class DefaultGuestPreferencesResponse
{
    public bool UseLocalTimezone { get; set; }
    public bool UseUtcTimezone { get; set; }
    public bool Use24HourFormat { get; set; }
    public bool SharpCorners { get; set; }
    public bool DisableTooltips { get; set; }
    public bool ShowDatasourceLabels { get; set; }
    public List<string> AllowedTimeFormats { get; set; } = new();
}

/// <summary>
/// Response for updating the allowed guest time formats.
/// </summary>
public class AllowedTimeFormatsResponse
{
    public string Message { get; set; } = string.Empty;
    public List<string> Formats { get; set; } = new();
}

/// <summary>
/// Response for updating the default guest clock.
/// </summary>
public class DefaultGuestClockResponse
{
    public string Message { get; set; } = string.Empty;
    public UserPreferencesService.ClockPreferences Clock { get; set; } = new();
}

/// <summary>
/// Response for updating a single default guest preference.
/// </summary>
public class DefaultGuestPreferenceResponse
{
    public string Message { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public bool Value { get; set; }
}

/// <summary>
/// Response for default prefill panel settings.
/// </summary>
public class PrefillDefaultsResponse
{
    public List<string> OperatingSystems { get; set; } = new();
    public string MaxConcurrency { get; set; } = string.Empty;
    public int ServerThreadCount { get; set; }

    /// <summary>
    /// The guest thread cap MaxConcurrency was clamped against. Null for an admin session, which has
    /// no thread limit.
    /// </summary>
    public int? MaxThreadLimit { get; set; }
    public string EpicDefaultPrefillMaxConcurrency { get; set; } = string.Empty;
}

/// <summary>
/// Response for GC settings
/// </summary>
public class GcSettingsResponse
{
    public bool Enabled { get; set; }
    public long MemoryThresholdMB { get; set; }

    /// <summary>
    /// Confirmation text for a settings update. Null when read from <c>GET /api/gc/settings</c>,
    /// which only reports the current settings.
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Response for GC trigger operation
/// </summary>
public class GcTriggerResponse
{
    public bool Skipped { get; set; }

    /// <summary>
    /// Why the trigger was skipped. Null when <see cref="Skipped"/> is false.
    /// </summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Seconds left in the cooldown before another trigger will run. Null when
    /// <see cref="Skipped"/> is false.
    /// </summary>
    public double? RemainingSeconds { get; set; }

    /// <summary>
    /// Process working-set size before the collection, in megabytes. Null when
    /// <see cref="Skipped"/> is true (no collection ran).
    /// </summary>
    public double? BeforeMB { get; set; }

    /// <summary>
    /// Process working-set size after the collection, in megabytes. Null when
    /// <see cref="Skipped"/> is true.
    /// </summary>
    public double? AfterMB { get; set; }

    /// <summary>
    /// Megabytes freed by the collection (<see cref="BeforeMB"/> minus <see cref="AfterMB"/>).
    /// Null when <see cref="Skipped"/> is true.
    /// </summary>
    public double? FreedMB { get; set; }
    public string Message { get; set; } = string.Empty;
}

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

/// <summary>
/// Response for metrics security settings
/// </summary>
public class MetricsSecurityResponse
{
    public bool RequiresAuthentication { get; set; }
    public string Source { get; set; } = "config";
    public bool CanToggle { get; set; } = true;
    public bool EnvVarValue { get; set; }
}
