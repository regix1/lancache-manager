namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Generic lancache <c>.env</c> file discovery + key/value reader, extracted from
/// <c>CacheManagementService.ReadCacheSizeFromEnvFile</c> so the Status Check feature (and anything
/// else that needs a value out of the same <c>.env</c>, e.g. CACHE_DOMAINS_REPO/BRANCH/NOFETCH)
/// doesn't duplicate the discovery chain.
/// </summary>
public interface ILancacheEnvFileReader
{
    /// <summary>
    /// The absolute path of the <c>.env</c> file currently in use, or <c>null</c> if none could be
    /// found. Re-evaluated lazily and cached (invalidated when the file's last-write time changes).
    /// </summary>
    string? ResolvedPath { get; }

    /// <summary>
    /// Looks up a <c>KEY=value</c> entry from the resolved <c>.env</c> file. Case-insensitive key
    /// match; surrounding whitespace and a single pair of wrapping <c>"</c>/<c>'</c> quotes are
    /// trimmed from the value. Returns <c>null</c> when no <c>.env</c> file is found or the key is
    /// absent - never throws.
    /// </summary>
    string? TryGetValue(string key);
}
