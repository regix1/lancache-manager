using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The version a shipped banner is advertised at, and the Steam-first rule that still sits in
/// front of shipped banners for the games the Steam map lists.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class EmbeddedBannerVersionTests
{
    private const string ShippedOnlySlug = "warcraft-ii-battle-net-edition";

    [Fact]
    public async Task AShippedBannerKeepsItsVersionWhenAnImagePassEndsAsync()
    {
        await using var context = CreateContext();
        var controller = CreateController(context);

        var before = await AdvertisedAsync(controller, ShippedOnlySlug);

        // A pass end moves the generation to the current second; wait for it to really move, so
        // the assert below cannot pass merely because both reads fell in the same second.
        var generation = GameImagesController.CacheGeneration;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (GameImagesController.CacheGeneration == generation && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            GameImagesController.IncrementCacheGeneration();
        }
        Assert.NotEqual(generation, GameImagesController.CacheGeneration);

        var after = await AdvertisedAsync(controller, ShippedOnlySlug);

        Assert.True(before < 0, $"the shipped banner reported {before}, which a stored row could also report");
        Assert.Equal(before, after);

        Assert.IsType<FileContentResult>(
            await controller.GetNameKeyedHeaderImageAsync("blizzard", ShippedOnlySlug, after, CancellationToken.None));
        Assert.Equal("public, max-age=31536000, immutable", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task ASteamFirstGameWithAShippedBannerStillServesSteamsHeaderAsync()
    {
        // Overwatch ships a banner and is listed in the Steam map, so Steam's header wins once stored,
        // and a stored copy of its shipped banner is not what the version follows.
        const string slug = "overwatch";
        const string steamAppId = "2357570";
        var steamStoredAtUtc = new DateTime(2026, 9, 2, 8, 0, 0, DateTimeKind.Utc);
        var steamBytes = Enumerable.Repeat((byte)0x33, 6000).ToArray();

        await using var context = CreateContext();
        context.GameImages.AddRange(
            new GameImage
            {
                AppId = steamAppId,
                Service = "steam",
                ImageData = steamBytes,
                ContentType = "image/jpeg",
                FetchedAtUtc = steamStoredAtUtc
            },
            new GameImage
            {
                AppId = slug,
                Service = "blizzard",
                ImageData = Enumerable.Repeat((byte)0x44, 6000).ToArray(),
                ContentType = "image/jpeg",
                SourceUrl = "embedded://overwatch",
                FetchedAtUtc = new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc)
            });
        await context.SaveChangesAsync();
        var controller = CreateController(context);

        var advertised = await AdvertisedAsync(controller, slug);
        Assert.Equal(new DateTimeOffset(steamStoredAtUtc).ToUnixTimeSeconds(), advertised);

        var served = Assert.IsType<FileContentResult>(
            await controller.GetNameKeyedHeaderImageAsync("blizzard", slug, advertised, CancellationToken.None));
        Assert.Equal(steamBytes, served.FileContents);
        Assert.Equal("public, max-age=31536000, immutable", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public void HasEmbeddedBanner_AgreesWithTheBytesTheRouteWouldServe()
    {
        foreach (var slug in NameKeyedBannerSource.EmbeddedBannerSlugs())
        {
            foreach (var service in new[] { "blizzard", "battle.net", "riot", "xbox", "steam" })
            {
                Assert.Equal(
                    NameKeyedBannerSource.TryGetEmbeddedBytesForSlug(service, slug, out _, out _),
                    NameKeyedBannerSource.HasEmbeddedBanner(service, slug));
            }
        }

        Assert.False(NameKeyedBannerSource.HasEmbeddedBanner("blizzard", "definitely-not-a-real-game"));
        Assert.False(NameKeyedBannerSource.HasEmbeddedBanner("blizzard", ""));
    }

    /// <summary>An empty in-memory database; also used by GameImagesShippedBannerTests.</summary>
    internal static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"embedded-banner-version-{Guid.NewGuid():N}")
            .Options);

    private static async Task<long> AdvertisedAsync(GameImagesController controller, string slug)
    {
        var listed = Assert.IsType<AvailableGameImagesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAvailableImageIdsAsync(CancellationToken.None)).Result).Value);
        return Assert.Contains(slug, (IDictionary<string, long>)listed.Images);
    }

    /// <summary>The image controller over <paramref name="context"/>'s GameImages rows; also used by
    /// GameImagesShippedBannerTests.</summary>
    internal static GameImagesController CreateController(AppDbContext context)
    {
        var images = DispatchProxy.Create<IImageCacheService, RowImages>();
        ((RowImages)(object)images).Context = context;

        return new GameImagesController(
            NullLogger<GameImagesController>.Instance,
            images,
            new GameImageFetchService(
                DispatchProxy.Create<IServiceProvider, NullReturningProxy>(),
                NullLogger<GameImageFetchService>.Instance,
                new ConfigurationBuilder().Build(),
                DispatchProxy.Create<IStateService, NullReturningProxy>(),
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                images,
                DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>()),
            context,
            DispatchProxy.Create<IOperationWaitlist, NullReturningProxy>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    /// <summary>Answers GetImageAsync from the test's GameImages rows; the controller calls nothing else on it.</summary>
    private class RowImages : DispatchProxy
    {
        public AppDbContext Context { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IImageCacheService.GetImageAsync), targetMethod?.Name);
            var appId = Assert.IsType<string>(args![0]);
            var service = Assert.IsType<string>(args[1]);
            var row = Context.GameImages.AsNoTracking()
                .FirstOrDefault(image => image.AppId == appId && image.Service == service);
            return Task.FromResult<(byte[] imageBytes, string contentType, DateTime storedAtUtc)?>(
                row == null ? null : (row.ImageData, row.ContentType, row.UpdatedAtUtc ?? row.FetchedAtUtc));
        }
    }
}
