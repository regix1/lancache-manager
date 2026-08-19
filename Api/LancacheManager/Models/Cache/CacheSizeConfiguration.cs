namespace LancacheManager.Models;

/// <summary>
/// Origin of an effective datasource cache-size limit.
/// </summary>
public enum CacheSizeSource
{
    Manual,
    Docker,
    Env,
    FullDisk
}

/// <summary>
/// Stable wire values for cache-size origins.
/// </summary>
public static class CacheSizeSourceValues
{
    public const string Manual = "manual";
    public const string Docker = "docker";
    public const string Env = "env";
    public const string FullDisk = "fullDisk";

    public static string ToWireValue(this CacheSizeSource source) => source switch
    {
        CacheSizeSource.Manual => Manual,
        CacheSizeSource.Docker => Docker,
        CacheSizeSource.Env => Env,
        CacheSizeSource.FullDisk => FullDisk,
        _ => throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown cache-size source")
    };
}

/// <summary>
/// Effective cache-size limit for one datasource. A resolved size of zero with a fullDisk source
/// means no configured limit is known and disk-capacity fallback remains active.
/// </summary>
public sealed record DatasourceCacheSizeResolution(
    string DatasourceName,
    long? OverrideBytes,
    long ResolvedBytes,
    CacheSizeSource Source);
