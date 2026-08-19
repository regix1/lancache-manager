using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the Epic Steam-first image fallback: an Epic game whose name has a curated Steam appId
/// (steam_fallback_appids.json "epic" section) renders Steam's header.jpg instead of Epic's own art.
/// GameImageFetchService.FetchEpicImageAsync does real HTTP and DB work and is private, so these
/// tests exercise GetEpicImageUrl directly - the one place that decides Steam-first vs Epic's own
/// art, and the only Epic caller of NameKeyedSteamAppIds.TryGetSteamAppIdForSection. The GameImage
/// row FetchEpicImageAsync stores is unconditionally keyed by (AppId = mapping.AppId,
/// Service = "epicgames") regardless of which branch produced the url - that key never changes,
/// only the fetched bytes' source does.
/// </summary>
public class GameImageFetchServiceEpicImageUrlTests
{
    [Fact]
    public void GetEpicImageUrl_PrefersSteamHeaderOverEpicArt_WhenSteamAppIdResolved()
    {
        const long overwatchSteamAppId = 2357570;

        var url = GameImageFetchService.GetEpicImageUrl(overwatchSteamAppId, "https://cdn.epicgames.com/help/some-epic-art.jpg?w=200");

        Assert.Equal(GameImageFetchService.GetSteamHeaderImageUrl(overwatchSteamAppId), url);
        Assert.DoesNotContain("epicgames.com", url);
    }

    [Fact]
    public void GetEpicImageUrl_FallsBackToEpicArt_WhenNoSteamAppIdResolved()
    {
        const string epicImageUrl = "https://cdn.epicgames.com/help/some-epic-art.jpg";

        var url = GameImageFetchService.GetEpicImageUrl(steamAppId: null, epicImageUrl);

        Assert.Equal(EpicApiDirectClient.EnsureResizeParams(epicImageUrl), url);
    }

    [Fact]
    public void TryGetSteamAppIdForSection_ReachesARawSectionKeyThatNormalizeServiceRejects()
    {
        // NormalizeService has no alias for "epic" (Epic is appId-keyed, not name-keyed), so
        // TryGetSteamAppId - which goes through NormalizeService - can never resolve it.
        Assert.Null(NameKeyedBannerSource.NormalizeService("epic"));
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppId("epic", "Overwatch"));

        // TryGetSteamAppIdForSection reads the raw JSON section directly, bypassing that gate. The
        // shipped "epic" section is empty (candidate appIds are verified separately before being
        // added), so today this still returns null - an empty-section miss, not the NormalizeService
        // gate reasserting itself.
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppIdForSection("epic", "Overwatch"));

        // The same lookup resolves a real entry once the section has one. "blizzard" is both a real,
        // non-empty section AND (unlike "epic") one NormalizeService already accepts, so agreement
        // here proves TryGetSteamAppIdForSection reads directly off the section key rather than
        // depending on NormalizeService for the actual value.
        Assert.Equal(
            NameKeyedSteamAppIds.TryGetSteamAppId("blizzard", "Overwatch"),
            NameKeyedSteamAppIds.TryGetSteamAppIdForSection("blizzard", "Overwatch"));
    }

    [Fact]
    public void TryGetSteamAppIdForSection_IsCaseInsensitiveOnTheSectionKey()
    {
        Assert.Equal(2357570L, NameKeyedSteamAppIds.TryGetSteamAppIdForSection("Blizzard", "Overwatch"));
        Assert.Equal(2357570L, NameKeyedSteamAppIds.TryGetSteamAppIdForSection("BLIZZARD", "Overwatch"));
    }

    [Fact]
    public void TryGetSteamAppIdForSection_ReturnsNullForUnknownSectionOrGameName()
    {
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppIdForSection("not-a-real-service", "Overwatch"));
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppIdForSection("blizzard", "Definitely Not A Real Game"));
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppIdForSection("blizzard", ""));
    }
}
