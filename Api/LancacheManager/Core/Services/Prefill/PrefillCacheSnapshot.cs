using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

public sealed class PrefillCacheSnapshot
{
    public required IReadOnlyList<CachedDepotInput> Depots { get; init; }
    public required IReadOnlyList<CacheAppScope> Scope { get; init; }
}
