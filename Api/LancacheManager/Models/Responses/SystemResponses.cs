namespace LancacheManager.Models;

/// <summary>
/// Response for API version endpoint
/// </summary>
public class VersionResponse
{
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
/// Response for directory permissions
/// </summary>
public class PermissionsResponse
{
    public string Path { get; set; } = string.Empty;
    public bool Writable { get; set; }
    public bool ReadOnly { get; set; }
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
/// Response for scan mode update
/// </summary>
public class ScanModeResponse
{
    public string Message { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
}

/// <summary>
/// Response for refresh rate setting
/// </summary>
public class RefreshRateResponse
{
    public string? Message { get; set; }
    public string RefreshRate { get; set; } = string.Empty;
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
/// Response for metrics endpoint status
/// </summary>
public class MetricsStatusResponse
{
    public bool RequiresAuthentication { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string AuthMethod { get; set; } = string.Empty;
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
