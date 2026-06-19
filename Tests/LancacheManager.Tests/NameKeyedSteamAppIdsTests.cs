using LancacheManager.Infrastructure.Data;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the single-source GameName -> Steam appId map (blizzard_steam_appids.json) that makes
/// name-keyed (Blizzard/Riot) games render Steam's header.jpg when they also exist on Steam.
/// Exercises the generic parse (service -> { gameName -> appId }, skipping the "_comment" key),
/// the slug/gameName lookup agreement, service aliasing, and the Riot/unknown null fallbacks - all
/// keyed via the shared NameKeyedBannerSource.Slug / NormalizeService conventions so keys can't drift.
/// </summary>
public class NameKeyedSteamAppIdsTests
{
    [Theory]
    [InlineData("Overwatch", 2357570L)]
    [InlineData("Diablo IV", 2344520L)]
    [InlineData("Call of Duty", 1938090L)]
    [InlineData("Sea of Thieves", 1172620L)]
    public void TryGetSteamAppId_ResolvesKnownGameToVerifiedAppId(string gameName, long expectedAppId)
    {
        Assert.Equal(expectedAppId, NameKeyedSteamAppIds.TryGetSteamAppId("blizzard", gameName));
    }

    [Fact]
    public void TryGetSteamAppIdBySlug_AgreesWithTryGetSteamAppIdForSameGame()
    {
        const string gameName = "Diablo IV";
        var slug = NameKeyedBannerSource.Slug(gameName);

        var byName = NameKeyedSteamAppIds.TryGetSteamAppId("blizzard", gameName);
        var bySlug = NameKeyedSteamAppIds.TryGetSteamAppIdBySlug("blizzard", slug);

        Assert.NotNull(byName);
        Assert.Equal(byName, bySlug);
    }

    [Theory]
    [InlineData("blizzard")]
    [InlineData("Blizzard")]
    [InlineData("battle.net")]
    [InlineData("battlenet")]
    public void TryGetSteamAppId_NormalizesServiceAliases(string service)
    {
        // All Blizzard service aliases must resolve the same mapping (proves NormalizeService reuse).
        Assert.Equal(2344520L, NameKeyedSteamAppIds.TryGetSteamAppId(service, "Diablo IV"));
    }

    [Fact]
    public void TryGetSteamAppIdBySlug_NormalizesServiceAliasesForSlugLookup()
    {
        var slug = NameKeyedBannerSource.Slug("Diablo IV");

        Assert.Equal(2344520L, NameKeyedSteamAppIds.TryGetSteamAppIdBySlug("battle.net", slug));
        Assert.Equal(2344520L, NameKeyedSteamAppIds.TryGetSteamAppIdBySlug("battlenet", slug));
    }

    [Theory]
    [InlineData("World of Warcraft")]
    [InlineData("Hearthstone")]
    [InlineData("Diablo III")]
    [InlineData("Definitely Not A Real Game")]
    public void TryGetSteamAppId_ReturnsNullForGamesNotOnSteam(string gameName)
    {
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppId("blizzard", gameName));
    }

    [Theory]
    [InlineData("League of Legends")]
    [InlineData("Valorant")]
    [InlineData("Legends of Runeterra")]
    public void TryGetSteamAppId_ReturnsNullForRiotGames(string gameName)
    {
        // The riot section is intentionally empty - none of these are on Steam.
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppId("riot", gameName));
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("epicgames")]
    [InlineData("")]
    [InlineData(null)]
    public void TryGetSteamAppId_ReturnsNullForNonNameKeyedService(string? service)
    {
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppId(service, "Diablo IV"));
    }

    [Fact]
    public void TryGetSteamAppIdBySlug_ReturnsNullForUnknownSlug()
    {
        Assert.Null(NameKeyedSteamAppIds.TryGetSteamAppIdBySlug("blizzard", "definitely-not-a-real-game"));
    }
}
