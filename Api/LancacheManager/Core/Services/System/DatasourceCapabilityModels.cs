using LancacheManager.Configuration;

namespace LancacheManager.Core.Services;

/// <summary>
/// What the manager knows about how a datasource's cache keys are formed. This is an
/// evidence record, not a boolean: monolithic-only and per-service-only evidence select
/// their respective native key schemes, while mixed or absent evidence stays denied.
/// </summary>
public enum CacheKeyScheme
{
    /// <summary>No evidence either way — default-deny for every key-dependent feature.</summary>
    Unknown,
    /// <summary>Monolithic lancache: keys follow the service+uri[+range] recipe the manager computes.</summary>
    SupportedMonolithic,
    /// <summary>Evidence of more than one scheme in the same cache root.</summary>
    Mixed,
    /// <summary>Per-service bare-metal keys, selected from per-service-only log evidence.</summary>
    ObservedBareMetal
}

/// <summary>
/// Per-datasource capabilities. Auto mode is computed from current on-disk evidence;
/// an explicit datasource override selects the cache-key scheme when inference is unavailable.
/// </summary>
public class DatasourceCapabilities
{
    public string DatasourceName { get; init; } = string.Empty;
    public CacheKeyScheme CacheKeyScheme { get; init; }
    public DatasourceSchemeOverride SchemeOverride { get; init; }
    public string? DenialReason { get; init; }
    /// <summary>Log sources exist and can be ingested.</summary>
    public bool CanIngest { get; init; }
    /// <summary>Line-level rewriting of every active log (per-game purge) is meaningful.</summary>
    public bool CanRewriteAllActiveLogs { get; init; }
    /// <summary>Structural cache inspection (corruption scanning) understands this cache's files.</summary>
    public bool CanInspectCacheStructure { get; init; }
    /// <summary>
    /// Whole-root cache clear: validates two-hex directories only and needs no key
    /// knowledge, so it stays available for every scheme.
    /// </summary>
    public bool CanClearWholeCacheRoot { get; init; }
    /// <summary>
    /// Object-scoped mapping between logical games/services and physical cache files:
    /// detection, removal, eviction reconciliation, corruption mapping. Requires an
    /// unambiguous monolithic or bare-metal key scheme.
    /// </summary>
    public bool CanMapLogicalObjects { get; init; }
    /// <summary>The container-exec nginx log-reopen signal applies to this datasource.</summary>
    public bool CanSignalLogReopen { get; init; }
    /// <summary>The live speed tracker's cachelog tail understands this datasource's logs.</summary>
    public bool CanTrackLiveSpeed { get; init; }
}
