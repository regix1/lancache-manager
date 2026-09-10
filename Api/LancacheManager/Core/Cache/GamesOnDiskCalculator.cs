using LancacheManager.Models;

namespace LancacheManager.Core;

/// <summary>
/// Identity keys for cache-on-disk aggregates. Per-row byte totals are measured once by the
/// detection scan and persisted in <see cref="CachedDetectionSummary"/> for fast dashboard reads.
/// </summary>
public static class GamesOnDiskCalculator
{
    /// <summary>
    /// Stable per-game attribution key. Mirrors <c>GameCacheDetectionService.BuildGameIdentityKey</c>
    /// and the persistence buckets in <c>GameCacheDetectionDataService.SaveGamesAsync</c> exactly.
    /// Named (Blizzard/Riot) games ALL have <see cref="GameCacheInfo.GameAppId"/> == 0, so keying them
    /// on <c>steam:0</c> collapsed every named game into a single bucket — each one overwrote the same
    /// entry and they all read back the last-written game's size. Named games must therefore key on
    /// <c>(Service, GameName)</c> using the Rust composite-key separator (<c>\x01</c>, which cannot
    /// appear in a service or game name) so each resolves its OWN on-disk size.
    /// Forwards to <see cref="GetDownloadGameKey"/>, which applies these same three branches to the
    /// <see cref="Download"/> column shape. Two hand-maintained copies of one rule cannot be kept
    /// identical by inspection, and they must return byte-identical keys for the same game or the
    /// per-game download figures stop joining against the on-disk figures.
    /// </summary>
    public static string GetGameKey(GameCacheInfo game) =>
        GetDownloadGameKey(game.GameAppId, game.EpicAppId, game.Service, game.GameName);

    /// <summary>
    /// Stable per-game attribution key, taken column by column so it serves both a
    /// <see cref="Download"/> row and the <see cref="GameCacheInfo"/> shape behind
    /// <see cref="GetGameKey"/>. A named game carries <c>GameAppId</c> 0 in
    /// <see cref="GameCacheInfo"/> and NULL in a <see cref="Download"/> row
    /// (<see cref="Download.GameAppId"/> is <c>long?</c>), so the named branch accepts 0 as well as
    /// NULL. The empty-name test is <c>!= ""</c> rather than <c>IsNullOrEmpty</c>, so a row with no
    /// recorded name still keys on its service instead of dropping into the Steam bucket.
    /// </summary>
    public static string GetDownloadGameKey(long? gameAppId, string? epicAppId, string? service, string? gameName)
    {
        if (!string.IsNullOrEmpty(epicAppId))
        {
            return $"epic:{epicAppId}";
        }

        if ((gameAppId is null || gameAppId == 0) && service != null && gameName != "")
        {
            return $"named:{service.ToLowerInvariant()}\x01{gameName}";
        }

        return $"steam:{gameAppId ?? 0}";
    }

    public static string GetServiceKey(ServiceCacheInfo service) =>
        service.ServiceName.ToLowerInvariant();
}
