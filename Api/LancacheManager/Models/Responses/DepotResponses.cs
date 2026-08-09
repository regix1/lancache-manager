namespace LancacheManager.Models;

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

    /// <summary>
    /// When the PICS JSON file was last written. Null when <see cref="Exists"/> is false.
    /// </summary>
    public DateTime? LastUpdated { get; set; }
    public int TotalMappings { get; set; }

    /// <summary>
    /// When the next scheduled crawl is due to refresh this file. Null when <see cref="Exists"/>
    /// is false, since there is no prior crawl to schedule the next one from.
    /// </summary>
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

    /// <summary>
    /// How many PICS changes behind the last checked baseline is. Null unless
    /// <see cref="RequiresFullScan"/> is true.
    /// </summary>
    public uint? ChangeGap { get; set; }

    /// <summary>
    /// Rough estimate of how many apps a full scan would need to process. Null unless
    /// <see cref="RequiresFullScan"/> is true.
    /// </summary>
    public int? EstimatedApps { get; set; }

    /// <summary>
    /// Human-readable explanation of why a full scan is required. Null unless
    /// <see cref="RequiresFullScan"/> is true.
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// The underlying viability-check failure, when the check itself could not complete (a Steam
    /// connection or timeout error). Null when the check completed normally.
    /// </summary>
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

/// <summary>
/// Response for crawl interval update
/// </summary>
public class CrawlIntervalResponse
{
    public string Message { get; set; } = string.Empty;
    public int IntervalHours { get; set; }
}

