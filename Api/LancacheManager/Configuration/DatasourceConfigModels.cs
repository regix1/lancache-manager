namespace LancacheManager.Configuration;

/// <summary>
/// Optional cache-key scheme selection for a datasource. Auto preserves log-topology
/// inference; explicit values are used when a non-stock log layout cannot be inferred.
/// </summary>
public enum DatasourceSchemeOverride
{
    Auto,
    Monolithic,
    BareMetal
}
