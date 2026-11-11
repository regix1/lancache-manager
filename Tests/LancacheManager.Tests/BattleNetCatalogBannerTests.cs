using System.Text.Json;
using LancacheManager.Core.Services.BattleNet;
using LancacheManager.Infrastructure.Data;

namespace LancacheManager.Tests;

/// <summary>
/// Every Battle.net product the TACT catalog names draws a banner: the catalog name, the curated
/// banner map, the embedded JPEG and the Steam-first map all meet on the same slug.
/// </summary>
public sealed class BattleNetCatalogBannerTests
{
    [Theory]
    [InlineData("aris", "DOOM: The Dark Ages", 3017860L)]
    [InlineData("ark", "The Outer Worlds 2", 1449110L)]
    [InlineData("lbra", "Tony Hawk's Pro Skater 3 + 4", 2545710L)]
    [InlineData("lyra", "The Witcher 3: Wild Hunt — Remastered", 292030L)]
    [InlineData("malb", "Cyberpunk 2077", 1091500L)]
    public void ProductCodeNamesItsGameWithABannerAndSteamArt(string code, string gameName, long steamAppId)
    {
        var products = ReadCatalogSection("products");

        Assert.True(products.TryGetValue(code, out var catalogName), $"catalog has no product '{code}'");
        Assert.Equal(gameName, catalogName);
        AssertEmbeddedBanner(gameName);
        Assert.Equal(steamAppId, NameKeyedSteamAppIds.TryGetSteamAppId("blizzard", gameName));
    }

    [Fact]
    public void WorldOfWarcraftPathNamesWorldOfWarcraftWithItsBanner()
    {
        // wow, wow_classic, wowf and wow_anniversary all publish the tpr/wow CDN path.
        var segment = BattleNetMappingService.ExtractTactSegment("/tpr/wow/data/ab/cd/abcdef");

        Assert.Equal("wow", segment);
        Assert.Equal("World of Warcraft", ReadCatalogSection("products")[segment!]);
        AssertEmbeddedBanner("World of Warcraft");
    }

    [Fact]
    public void EveryCatalogGameHasAnEmbeddedBanner()
    {
        var names = ReadCatalogSection("products").Values
            .Concat(ReadCatalogSection("aliases").Values)
            .Distinct(StringComparer.Ordinal);

        foreach (var name in names)
        {
            AssertEmbeddedBanner(name);
        }
    }

    private static void AssertEmbeddedBanner(string gameName)
    {
        var slug = NameKeyedBannerSource.Slug(gameName);
        Assert.True(
            NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("blizzard", slug, out var bytes, out var contentType),
            $"no embedded banner for '{gameName}' (slug '{slug}')");
        Assert.Equal("image/jpeg", contentType);
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, bytes.Take(2));
    }

    private static Dictionary<string, string> ReadCatalogSection(string section)
    {
        using var stream = typeof(NameKeyedBannerSource).Assembly
            .GetManifestResourceStream("LancacheManager.tact_products.json")!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty(section).EnumerateObject()
            .ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);
    }
}
