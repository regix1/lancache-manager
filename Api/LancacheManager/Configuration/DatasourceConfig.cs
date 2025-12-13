namespace LancacheManager.Configuration;

/// <summary>
/// Represents a single datasource configuration for a LANCache instance.
/// </summary>
public class DatasourceConfig
{
    /// <summary>
    /// Unique name/identifier for this datasource.
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// Path to the cache directory for this datasource.
    /// </summary>
    public string CachePath { get; set; } = "/cache";

    /// <summary>
    /// Path to the logs directory for this datasource.
    /// </summary>
    public string LogPath { get; set; } = "/logs";

    /// <summary>
    /// Whether this datasource is enabled for processing.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Root configuration for LANCache settings, supporting multiple datasources.
/// </summary>
public class LanCacheSettings
{
    /// <summary>
    /// List of configured datasources. If empty, falls back to legacy CachePath/LogPath.
    /// </summary>
    public List<DatasourceConfig> DataSources { get; set; } = new();

    /// <summary>
    /// Legacy single cache path (for backward compatibility).
    /// </summary>
    public string? CachePath { get; set; }

    /// <summary>
    /// Legacy single log path (for backward compatibility).
    /// </summary>
    public string? LogPath { get; set; }

    /// <summary>
    /// Whether to start processing from end of log file (skip existing entries).
    /// </summary>
    public bool StartFromEndOfLog { get; set; } = false;
}
