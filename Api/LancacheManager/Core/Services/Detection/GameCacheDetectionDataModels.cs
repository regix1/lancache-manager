namespace LancacheManager.Core.Services;

internal readonly record struct CachedGameUnevictTargets(
    IReadOnlyList<long> SteamGameAppIds,
    IReadOnlyList<string> EpicAppIds,
    IReadOnlyList<NamedGameKey> NamedGameKeys);

/// <summary>
/// Identity of a named (Blizzard/Riot) game in the detection/eviction layer:
/// (lowercased Service, GameName). Used where neither GameAppId nor EpicAppId exists.
/// </summary>
internal readonly record struct NamedGameKey(string Service, string GameName);
