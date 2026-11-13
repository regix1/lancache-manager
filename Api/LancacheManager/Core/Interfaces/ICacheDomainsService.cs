using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Acquires and caches the uklans/cache-domains service/domain list (source configurable via the
/// lancache <c>.env</c> file: CACHE_DOMAINS_REPO / CACHE_DOMAINS_BRANCH / NOFETCH).
/// </summary>
public interface ICacheDomainsService
{
    /// <summary>
    /// Returns the current domain list, fetching from GitHub first when the in-memory cache is
    /// empty, stale (&gt;24h), or <paramref name="forceRefresh"/> is set - unless NOFETCH is enabled,
    /// in which case the disk copy (or an empty list) is served and the network is never touched.
    /// </summary>
    Task<CacheDomainsList> GetDomainsAsync(bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>
    /// Explicit user-triggered refresh. Returns a blocked outcome (never touches the network)
    /// when NOFETCH is enabled; the controller surfaces that as a 409.
    /// </summary>
    Task<CacheDomainsRefreshOutcome> RefreshDomainsAsync(CancellationToken cancellationToken);

    /// <summary>Metadata describing where the currently-cached domain list came from.</summary>
    DomainsSource GetCurrentSource();

    /// <summary>
    /// Dates each of <paramref name="failingEntries"/> by the GitHub commit that added it to the
    /// list, among the commits newer than the newest one that added an entry of
    /// <paramref name="knownEntries"/> (names the DNS server sends to the cache, so that commit is on
    /// its list). An entry with no proven date is left out; NOFETCH, a repository that is not on
    /// GitHub or a failed lookup returns an empty map.
    /// </summary>
    Task<Dictionary<string, DateTime>> GetAddedDatesAsync(
        IReadOnlyCollection<string> failingEntries,
        IReadOnlyCollection<string> knownEntries,
        CancellationToken cancellationToken);
}
