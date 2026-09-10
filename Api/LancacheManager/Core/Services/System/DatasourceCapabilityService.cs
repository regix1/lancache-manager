using LancacheManager.Configuration;

namespace LancacheManager.Core.Services;

/// <summary>
/// Computes per-datasource capabilities from disk evidence and enforces them at the
/// mutating chokepoints. Destructive operations must revalidate HERE immediately before
/// mutation (evidence can change between queueing and execution), and multi-datasource
/// operations fail closed when ANY targeted datasource lacks the capability.
/// </summary>
public class DatasourceCapabilityService
{
    private readonly DatasourceService _datasourceService;

    public DatasourceCapabilityService(DatasourceService datasourceService)
    {
        _datasourceService = datasourceService;
    }

    /// <summary>
    /// Capabilities for one datasource, computed from the sources on disk right now.
    /// </summary>
    public DatasourceCapabilities GetCapabilities(ResolvedDatasource datasource)
    {
        datasource.RefreshLogSources();
        var stems = datasource.LogSourceStems;
        var hasMonolithic = stems.Contains(LogSourceLayout.MonolithicStem);
        var hasPerService = stems.Any(LogSourceLayout.IsPerServiceStem);

        // Explicit configuration is authoritative for the key recipe. Auto retains the existing
        // evidence inference and its mixed/unknown default-deny behavior.
        CacheKeyScheme scheme;
        if (datasource.SchemeOverride == DatasourceSchemeOverride.Monolithic)
        {
            scheme = CacheKeyScheme.SupportedMonolithic;
        }
        else if (datasource.SchemeOverride == DatasourceSchemeOverride.BareMetal)
        {
            scheme = CacheKeyScheme.ObservedBareMetal;
        }
        else if (hasMonolithic && hasPerService)
        {
            scheme = CacheKeyScheme.Mixed;
        }
        else if (hasMonolithic)
        {
            scheme = CacheKeyScheme.SupportedMonolithic;
        }
        else if (hasPerService)
        {
            scheme = CacheKeyScheme.ObservedBareMetal;
        }
        else
        {
            scheme = CacheKeyScheme.Unknown;
        }

        var canUseObjectScopedDiskFeatures = scheme is
            CacheKeyScheme.SupportedMonolithic or CacheKeyScheme.ObservedBareMetal;

        return new DatasourceCapabilities
        {
            DatasourceName = datasource.Name,
            CacheKeyScheme = scheme,
            SchemeOverride = datasource.SchemeOverride,
            DenialReason = canUseObjectScopedDiskFeatures ? null : DenialMessage(datasource.Name),
            CanIngest = stems.Count > 0,
            CanRewriteAllActiveLogs = scheme == CacheKeyScheme.SupportedMonolithic,
            CanInspectCacheStructure = canUseObjectScopedDiskFeatures,
            CanClearWholeCacheRoot = true,
            CanMapLogicalObjects = canUseObjectScopedDiskFeatures,
            CanSignalLogReopen = !hasPerService,
            // The speed tracker discovers and tails BOTH layouts (the monolithic cachelog and
            // the per-service bare-metal http-detailed files), so any datasource with a single
            // trustworthy layout can be tracked. Unknown/Mixed evidence has no reliable service
            // attribution and stays off.
            CanTrackLiveSpeed = scheme is
                CacheKeyScheme.SupportedMonolithic or CacheKeyScheme.ObservedBareMetal
        };
    }

    /// <summary>
    /// Native key-scheme value selected from the datasource's current log evidence.
    /// Ambiguous or absent evidence is refused rather than silently selecting a recipe.
    /// </summary>
    public string GetKeySchemeWireValue(ResolvedDatasource datasource)
    {
        var capabilities = GetCapabilities(datasource);
        if (!capabilities.CanMapLogicalObjects)
        {
            throw new InvalidOperationException(capabilities.DenialReason);
        }

        return GetSchemeWireValue(capabilities);
    }

    /// <summary>
    /// Effective scheme exposed by the capability API, including denied inferred states.
    /// </summary>
    public static string GetSchemeWireValue(DatasourceCapabilities capabilities)
    {
        return capabilities.CacheKeyScheme switch
        {
            CacheKeyScheme.SupportedMonolithic => LogSourceLayout.LayoutMonolithic,
            CacheKeyScheme.ObservedBareMetal => LogSourceLayout.LayoutBareMetal,
            CacheKeyScheme.Mixed => LogSourceLayout.LayoutMixed,
            CacheKeyScheme.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities.CacheKeyScheme,
                "Unknown cache-key scheme")
        };
    }

    /// <summary>Capabilities for a datasource by name, or null when it does not exist.</summary>
    public DatasourceCapabilities? GetCapabilities(string datasourceName)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        return datasource == null ? null : GetCapabilities(datasource);
    }

    /// <summary>Capabilities for every enabled datasource.</summary>
    public List<DatasourceCapabilities> GetAllCapabilities()
    {
        return _datasourceService.GetDatasources()
            .Where(ds => ds.Enabled)
            .Select(GetCapabilities)
            .ToList();
    }

    /// <summary>
    /// Guard for object-scoped mutations that span every datasource. Returns null when
    /// permitted; otherwise a denial naming the first datasource that fails.
    /// </summary>
    public string? CheckAllCanMapLogicalObjects()
    {
        var all = GetAllCapabilities();
        if (all.Count == 0)
        {
            return "No datasources configured";
        }
        var blocked = all.FirstOrDefault(c => !c.CanMapLogicalObjects);
        return blocked?.DenialReason;
    }

    private static string DenialMessage(string datasourceName) =>
        $"Datasource '{datasourceName}' has mixed or unknown cache-key evidence, so this feature " +
        "cannot safely select a key scheme. Disk-level game and service operations are not " +
        "available; clearing the whole cache remains available.";
}
