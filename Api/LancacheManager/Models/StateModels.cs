using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Application state model (with decrypted sensitive fields in memory)
/// </summary>
public class AppState
{
    public LogProcessingState LogProcessing { get; set; } = new();
    public DepotProcessingState DepotProcessing { get; set; } = new();
    // LEGACY: CacheClearOperations moved to separate file (data/operations/cache_operations.json)
    [JsonIgnore]
    public List<CacheClearOperation> CacheClearOperations { get; set; } = new();
    // LEGACY: OperationStates moved to separate file (data/operations/operation_history.json)
    [JsonIgnore]
    public List<OperationState> OperationStates { get; set; } = new();
    public bool SetupCompleted { get; set; } = false;
    public DateTime? LastPicsCrawl { get; set; }
    public double CrawlIntervalHours { get; set; } = 1.0; // Default to 1 hour
    public object CrawlIncrementalMode { get; set; } = true; // Default to incremental scans (true/false/"github")
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public bool HasDataLoaded { get; set; } = false;
    public bool HasProcessedLogs { get; set; } = false; // Track if logs have been processed at least once
    public int GuestSessionDurationHours { get; set; } = 6; // Default to 6 hours
    public bool GuestModeLocked { get; set; } = false; // When true, guest mode login is disabled
    public string? SelectedTheme { get; set; } = "dark-default"; // Default theme for authenticated users
    public string? DefaultGuestTheme { get; set; } = "dark-default"; // Default theme for guest users
    public string RefreshRate { get; set; } = "STANDARD"; // Default to 10 seconds (LIVE, ULTRA, REALTIME, STANDARD, RELAXED, SLOW)
    public string DefaultGuestRefreshRate { get; set; } = "STANDARD"; // Default refresh rate for guest users
    public bool GuestRefreshRateLocked { get; set; } = true; // When true, guests cannot change their refresh rate

    // Default guest preferences (applied to new guest sessions)
    public bool DefaultGuestUseLocalTimezone { get; set; } = false;
    public bool DefaultGuestUse24HourFormat { get; set; } = true;
    public bool DefaultGuestSharpCorners { get; set; } = false;
    public bool DefaultGuestDisableTooltips { get; set; } = false;
    public bool DefaultGuestShowDatasourceLabels { get; set; } = true;
    public bool DefaultGuestShowYearInDates { get; set; } = false;

    // Allowed time formats for guests (e.g., ["server-24h", "server-12h", "local-24h", "local-12h"])
    // If empty or null, all formats are allowed
    public List<string> AllowedTimeFormats { get; set; } = new() { "server-24h", "server-12h", "local-24h", "local-12h" };

    // Guest prefill permissions - controls access to the Prefill tab for guests
    public bool GuestPrefillEnabledByDefault { get; set; } = false; // Whether new guests get prefill access by default
    public int GuestPrefillDurationHours { get; set; } = 2; // Default duration for prefill access (1 or 2 hours)

    // Prefill panel default settings
    public List<string> DefaultPrefillOperatingSystems { get; set; } = new() { "windows", "linux", "macos" };
    public string DefaultPrefillMaxConcurrency { get; set; } = "default";

    // Max thread count limit for guest users (null = no limit)
    public int? DefaultGuestMaxThreadCount { get; set; } = null;

    // Epic prefill settings
    public bool EpicGuestPrefillEnabledByDefault { get; set; } = false;
    public int EpicGuestPrefillDurationHours { get; set; } = 2;
    public int? EpicDefaultGuestMaxThreadCount { get; set; } = null;
    public string EpicDefaultPrefillMaxConcurrency { get; set; } = "default";

    // PICS viability check caching (prevents repeated Steam API calls)
    public bool RequiresFullScan { get; set; } = false; // True if Steam requires full scan due to large change gap
    public DateTime? LastViabilityCheck { get; set; } // When we last checked with Steam
    public uint LastViabilityCheckChangeNumber { get; set; } = 0; // Change number at time of last check
    public uint ViabilityChangeGap { get; set; } = 0; // Change gap at time of last check

    // Metrics authentication toggle (null = use env var default, true/false = UI override)
    public bool? RequireAuthForMetrics { get; set; } = null;

    // Client IPs to exclude from stats calculations
    public List<string> ExcludedClientIps { get; set; } = new();

    // Client IP exclusion rules (mode controls stats-only vs hide)
    public List<ClientExclusionRule> ExcludedClientRules { get; set; } = new();

    // LEGACY: SteamAuth has been migrated to separate file (data/security/steam_auth/credentials.json)
    // This property is kept temporarily for backward compatibility during migration
    public SteamAuthState? SteamAuth { get; set; }
}

/// <summary>
/// Steam authentication state (legacy - kept for backward compatibility during migration)
/// </summary>
public class SteamAuthState
{
    public string Mode { get; set; } = "anonymous"; // "anonymous" or "authenticated"
    public string? Username { get; set; }
    public string? RefreshToken { get; set; } // Decrypted in memory, encrypted in storage
    // NOTE: GuardData removed - modern Steam auth uses refresh tokens only
    public DateTime? LastAuthenticated { get; set; }
}

/// <summary>
/// Log processing state for tracking log file positions
/// </summary>
public class LogProcessingState
{
    /// <summary>
    /// Legacy single position (for backward compatibility with single datasource).
    /// Use DatasourcePositions for multi-datasource support.
    /// </summary>
    public long Position { get; set; } = 0;

    /// <summary>
    /// Per-datasource log positions. Key is datasource name, value is line number.
    /// </summary>
    public Dictionary<string, long> DatasourcePositions { get; set; } = new();

    /// <summary>
    /// Per-datasource total line counts. Key is datasource name, value is total lines.
    /// Populated by Rust processor to avoid C# recounting.
    /// </summary>
    public Dictionary<string, long> DatasourceTotalLines { get; set; } = new();

    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Depot processing state for tracking Steam depot crawling progress
/// </summary>
public class DepotProcessingState
{
    public bool IsActive { get; set; } = false;
    public string Status { get; set; } = "Idle";
    public int TotalApps { get; set; } = 0;
    public int ProcessedApps { get; set; } = 0;
    public int TotalBatches { get; set; } = 0;
    public int ProcessedBatches { get; set; } = 0;
    public double ProgressPercent { get; set; } = 0;
    public int DepotMappingsFound { get; set; } = 0;
    public DateTime? StartTime { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public uint LastChangeNumber { get; set; } = 0;
    public List<uint> RemainingApps { get; set; } = new();
}

/// <summary>
/// Cache clear operation tracking
/// </summary>
public class CacheClearOperation
{
    public string Id { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Progress { get; set; } = 0;
    public DateTime StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// General operation state tracking
/// </summary>
public class OperationState
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public object? Data { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
