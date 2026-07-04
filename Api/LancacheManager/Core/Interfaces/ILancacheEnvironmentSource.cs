namespace LancacheManager.Core.Interfaces;

/// <summary>Which tier ultimately supplied an environment value.</summary>
public enum EnvValueSource
{
    DockerInspect,
    EnvFile
}

/// <summary>Result of a tiered environment-value lookup. <see cref="Value"/> is <c>null</c> when
/// neither the Docker-inspect nor the <c>.env</c>-file tier yielded the key - the caller applies
/// its own hardcoded default (Tier 3) and reports that itself.</summary>
public sealed class EnvValueResult
{
    public string? Value { get; init; }
    public EnvValueSource Source { get; init; }
}

/// <summary>
/// Composes the codebase's established two-tier environment-value pattern (the same shape
/// <c>CacheManagementService</c> already uses for cache size: Docker inspect first, <c>.env</c>
/// file second) for the Status Check feature's variables (<c>LANCACHE_IP</c>, <c>DISABLE_&lt;SERVICE&gt;</c>,
/// <c>CACHE_DOMAINS_REPO</c>/<c>BRANCH</c>, <c>NOFETCH</c>).
/// Tier 1: inspects the running lancache-dns container's <c>Config.Env</c> (ground truth - compose
/// substitution already resolved), falling through per-variable to the cache/monolithic container.
/// Tier 2: <see cref="ILancacheEnvFileReader"/>. Tier 3 (hardcoded defaults) is the caller's concern.
/// </summary>
public interface ILancacheEnvironmentSource
{
    Task<EnvValueResult> GetValueAsync(string key, CancellationToken cancellationToken);
}
