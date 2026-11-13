using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

/// <summary>
/// A banner the repo ships in Infrastructure/Data/banners is the art chosen for that game. It has
/// to reach the Games list even when the game already has another image stored: Steam's header
/// for a slug that also exists on Steam, or a copy of an older shipped file saved before the new
/// one was deployed. Its version has to move off the stored image's too, or a page that cached the
/// old URL keeps the old bytes.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImagesShippedBannerTests
{
    [Theory]
    // The Outer Worlds 2 also maps to Steam app 1449110, whose header was stored first.
    [InlineData("the-outer-worlds-2", "1449110", "steam",
        "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1449110/header.jpg")]
    // Warcraft II keeps the row the July banner was copied into before the new file shipped.
    [InlineData("warcraft-ii-battle-net-edition", "warcraft-ii-battle-net-edition", "blizzard",
        "embedded://warcraft-ii-battle-net-edition")]
    public async Task AShippedBannerIsServedOverAnotherStoredImageForItsGameAsync(
        string slug, string storedAppId, string storedService, string storedSourceUrl)
    {
        Assert.True(
            NameKeyedBannerSource.TryGetEmbeddedBytesForSlug("blizzard", slug, out var shipped, out _),
            $"the repo ships no banner for blizzard '{slug}'");

        var storedAtUtc = new DateTime(2026, 7, 20, 14, 0, 0, DateTimeKind.Utc);
        var storedBytes = Enumerable.Repeat((byte)0x5A, 6000).ToArray();
        var storedVersion = new DateTimeOffset(storedAtUtc).ToUnixTimeSeconds();

        await using var context = EmbeddedBannerVersionTests.CreateContext();
        context.GameImages.Add(new GameImage
        {
            AppId = storedAppId,
            Service = storedService,
            ImageData = storedBytes,
            ContentType = "image/jpeg",
            SourceUrl = storedSourceUrl,
            FetchedAtUtc = storedAtUtc
        });
        await context.SaveChangesAsync();

        // The image cache answers from the same table the controller lists from.
        var controller = EmbeddedBannerVersionTests.CreateController(context);

        var listed = Assert.IsType<AvailableGameImagesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAvailableImageIdsAsync(CancellationToken.None)).Result).Value);
        var advertised = Assert.Contains(slug, (IDictionary<string, long>)listed.Images);

        var served = Assert.IsType<FileContentResult>(
            await controller.GetNameKeyedHeaderImageAsync("blizzard", slug, advertised, CancellationToken.None));
        Assert.True(
            served.FileContents.SequenceEqual(shipped),
            $"'{slug}' served the stored {storedService} image ({served.FileContents.Length} bytes) instead of the shipped banner ({shipped.Length} bytes)");
        Assert.True(
            advertised != storedVersion,
            $"'{slug}' is still advertised at the stored {storedService} image's version {storedVersion}, so a page that cached that URL keeps the old image");
    }
}
