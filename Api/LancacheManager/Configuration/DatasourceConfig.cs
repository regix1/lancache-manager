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
