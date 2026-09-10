using LancacheManager.Configuration;

namespace LancacheManager.Core.Services;

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
