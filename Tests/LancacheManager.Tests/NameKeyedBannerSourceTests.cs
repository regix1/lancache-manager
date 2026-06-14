using LancacheManager.Infrastructure.Data;

namespace LancacheManager.Tests;

/// <summary>
/// Guards the data-driven name-keyed banner source: the generic game_banners.json parse
/// (service -> { gameName -> url }, skipping the "_comment" doc key), the service alias/normalization,
/// and the on-demand embedded serve (curated banners are served from the assembly with no stored
/// GameImage row, so they render the instant a curated game card appears).
/// </summary>
public class NameKeyedBannerSourceTests
{
    [Theory]
    [InlineData("blizzard", "blizzard")]
    [InlineData("Blizzard", "blizzard")]
    [InlineData("battle.net", "blizzard")]
    [InlineData("battlenet", "blizzard")]
    [InlineData("riot", "riot")]
    [InlineData("riotgames", "riot")]
    public void NormalizeService_MapsKnownServicesAndAliases(string input, string expected)
    {
        Assert.Equal(expected, NameKeyedBannerSource.NormalizeService(input));
    }

    [Theory]
    [InlineData("steam")]
    [InlineData("epicgames")]
    [InlineData("wsus")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeService_ReturnsNullForNonNameKeyedServices(string? input)
    {
        Assert.Null(NameKeyedBannerSource.NormalizeService(input));
    }

    [Fact]
    public void EmbeddedBannerSlugs_ParsesCuratedJsonAndFindsPresentResources()
    {
        // Exercises the generic parse end-to-end: a malformed/empty parse or a missing JPEG resource
        // would yield an empty set (or throw), so a non-empty result proves the data-driven load works
        // AND the embedded banner assets are wired into the assembly.
        var slugs = NameKeyedBannerSource.EmbeddedBannerSlugs();
        Assert.NotEmpty(slugs);
    }

    [Fact]
    public void TryGetEmbeddedBytesForSlug_ServesACuratedBannerWithoutAStoredRow()
    {
        var slug = NameKeyedBannerSource.EmbeddedBannerSlugs().First();

        // Slugs are service-agnostic; the curated entry lives under exactly one name-keyed service.
        var ok = NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("blizzard", slug, out var bytes, out var contentType)
              || NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("riot", slug, out bytes, out contentType);

        Assert.True(ok);
        Assert.NotEmpty(bytes);
        Assert.Equal("image/jpeg", contentType);
    }

    [Fact]
    public void TryGetEmbeddedBytesForSlug_ReturnsFalseForUnknownSlugOrService()
    {
        Assert.False(NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("blizzard", "definitely-not-a-real-game", out _, out _));
        Assert.False(NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("steam", NameKeyedBannerSource.EmbeddedBannerSlugs().First(), out _, out _));
    }
}
