using LancacheManager.Configuration;

namespace LancacheManager.Core.Services;

internal enum DatasourceOrigin
{
    Legacy,
    Explicit,
    Discovery
}

/// <summary>
/// Datasource information for API responses.
/// </summary>
public class DatasourceInfo
{
    public string Name { get; set; } = string.Empty;
    public string CachePath { get; set; } = string.Empty;
    public string LogsPath { get; set; } = string.Empty;
    public bool CacheWritable { get; set; }
    public bool LogsWritable { get; set; }
    public bool Enabled { get; set; }
    public string SchemeOverride { get; set; } = DatasourceSchemeOverrideValues.Auto;
    /// <summary>Presentation-only source layout: monolithic | bare_metal | mixed.</summary>
    public string Layout { get; set; } = LogSourceLayout.LayoutMonolithic;
    /// <summary>Number of logical access-log sources currently on disk.</summary>
    public int SourceCount { get; set; }
}

/// <summary>
/// A candidate datasource pair discovered during bounded auto-discovery traversal, before
/// deduplication. Carries the depth and relative path used to resolve duplicate-name priority.
/// </summary>
internal sealed record DiscoveryCandidate(string Name, string CachePath, string LogPath, int Depth, string RelativePath);

/// <summary>
/// A datasource with resolved absolute paths.
/// </summary>
public class ResolvedDatasource
{
    /// <summary>
    /// Unique name/identifier for this datasource.
    /// </summary>
    public string Name { get; set; } = "default";

    /// <summary>
    /// Resolved absolute path to the cache directory.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Resolved absolute path to the logs directory.
    /// </summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>
    /// Full path to the access.log file.
    /// </summary>
    public string LogFilePath { get; set; } = string.Empty;

    /// <summary>
    /// Whether this datasource is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    internal DatasourceOrigin Origin { get; set; }

    /// <summary>
    /// Configured cache-key scheme selection. Auto keeps the inferred log topology.
    /// </summary>
    public DatasourceSchemeOverride SchemeOverride { get; set; } = DatasourceSchemeOverride.Auto;

    /// <summary>
    /// Whether the cache directory is writable.
    /// </summary>
    public bool CacheWritable { get; set; }

    /// <summary>
    /// Whether the logs directory is writable.
    /// </summary>
    public bool LogsWritable { get; set; }

    /// <summary>
    /// Presentation-only source layout: monolithic | bare_metal | mixed. Derived from the
    /// stems on disk at the last refresh; never drives capability decisions by itself.
    /// </summary>
    public string Layout { get; set; } = LogSourceLayout.LayoutMonolithic;

    /// <summary>
    /// Logical source stems present at the last refresh (access.log, steam-access.log, ...).
    /// </summary>
    public IReadOnlyList<string> LogSourceStems { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Current (non-rotated) file paths for every source stem at the last refresh.
    /// LogFilePath above stays the legacy access.log path.
    /// </summary>
    public IReadOnlyList<string> LogFilePaths { get; set; } = Array.Empty<string>();

    private readonly object _refreshLock = new();

    /// <summary>
    /// The log directory exactly as configured, before any bare-metal http/ descent.
    /// </summary>
    public string ConfiguredLogPath { get; set; } = string.Empty;

    /// <summary>
    /// Re-enumerates the source stems on disk from the configured root.
    /// </summary>
    public void RefreshLogSources()
    {
        lock (_refreshLock)
        {
            var root = string.IsNullOrEmpty(ConfiguredLogPath) ? LogPath : ConfiguredLogPath;
            var resolvedDir = LogSourceLayout.ResolveAccessLogDirectory(root);
            if (!string.Equals(resolvedDir, LogPath, StringComparison.Ordinal))
            {
                LogPath = resolvedDir;
                LogFilePath = Path.Combine(resolvedDir, "access.log");
            }

            var stems = LogSourceLayout.EnumerateStems(LogPath)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();
            LogSourceStems = stems;
            Layout = LogSourceLayout.DeriveLayout(stems);
            LogFilePaths = stems
                .Select(stem => Path.Combine(LogPath, stem))
                .Where(File.Exists)
                .ToList();
        }
    }
}
