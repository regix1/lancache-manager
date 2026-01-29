namespace LancacheManager.Models;

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

/// <summary>
/// Response for crawl interval update
/// </summary>
public class CrawlIntervalResponse
{
    public string Message { get; set; } = string.Empty;
    public int IntervalHours { get; set; }
}

/// <summary>
/// Response for depot rebuild cancel
/// </summary>
public class DepotRebuildCancelResponse
{
    public string Message { get; set; } = string.Empty;
}
