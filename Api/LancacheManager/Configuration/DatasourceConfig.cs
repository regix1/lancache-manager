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

/// <summary>
/// Parses and formats datasource scheme values used by configuration and API responses.
/// </summary>
public static class DatasourceSchemeOverrideValues
{
    public const string Auto = "auto";
    public const string Monolithic = "monolithic";
    public const string BareMetal = "bare_metal";

    public static DatasourceSchemeOverride Parse(string? value, string? parameterName = null)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or Auto => DatasourceSchemeOverride.Auto,
            Monolithic => DatasourceSchemeOverride.Monolithic,
            BareMetal => DatasourceSchemeOverride.BareMetal,
            _ => throw new ArgumentException(
                $"Datasource scheme override must be one of: {Auto}, {Monolithic}, {BareMetal}.",
                parameterName)
        };
    }

    public static string ToWireValue(this DatasourceSchemeOverride value) => value switch
    {
        DatasourceSchemeOverride.Auto => Auto,
        DatasourceSchemeOverride.Monolithic => Monolithic,
        DatasourceSchemeOverride.BareMetal => BareMetal,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown datasource scheme override")
    };
}

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

    /// <summary>
    /// Optional cache-key scheme override: auto, monolithic, or bare_metal.
    /// </summary>
    public string SchemeOverride { get; set; } = DatasourceSchemeOverrideValues.Auto;
}
