using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A detection row stands for an unresolved Steam depot when its stored flag says so, not when its
/// name happens to start with the placeholder text.
/// </summary>
public sealed class UnresolvedDepotFlagTests
{
    [Fact]
    public async Task FlaggedRowIsFoundAndResolvedWhateverItsName()
    {
        var options = NewOptions();
        await SeedAsync(options, depotId: 5001, ownerAppId: 5000, new CachedGameDetection
        {
            GameAppId = 5001,
            GameName = "Depot 5001",
            IsUnresolvedDepot = true,
            DepotIdsJson = JsonSerializer.Serialize(new[] { 5001u })
        });

        await using (var context = new AppDbContext(options))
        {
            var unknown = await GameCacheDetectionService.GetUnknownGamesCachedAsync(context);
            Assert.Equal(5001, Assert.Single(unknown).GameAppId);
        }

        var resolvedCount = await NewResolver(options).ResolveUnknownGamesAsync(CancellationToken.None);

        Assert.Equal(1, resolvedCount);
        await using var verification = new AppDbContext(options);
        var game = Assert.Single(await verification.CachedGameDetections.ToListAsync());
        Assert.Equal(5000, game.GameAppId);
        Assert.Equal("Resolved 5000", game.GameName);
        Assert.False(game.IsUnresolvedDepot);
    }

    [Fact]
    public async Task GameWhoseTitleStartsLikeThePlaceholderIsNotUnknown()
    {
        var options = NewOptions();
        await SeedAsync(options, depotId: 5101, ownerAppId: 5100, new CachedGameDetection
        {
            GameAppId = 5101,
            GameName = "Unknown Game (Depot 5101)",
            IsUnresolvedDepot = false,
            DepotIdsJson = JsonSerializer.Serialize(new[] { 5101u })
        });

        await using (var context = new AppDbContext(options))
        {
            Assert.Empty(await GameCacheDetectionService.GetUnknownGamesCachedAsync(context));
        }

        var resolvedCount = await NewResolver(options).ResolveUnknownGamesAsync(CancellationToken.None);

        Assert.Equal(0, resolvedCount);
        await using var verification = new AppDbContext(options);
        var game = Assert.Single(await verification.CachedGameDetections.ToListAsync());
        Assert.Equal(5101, game.GameAppId);
        Assert.Equal("Unknown Game (Depot 5101)", game.GameName);
    }

    private static async Task SeedAsync(
        DbContextOptions<AppDbContext> options,
        long depotId,
        long ownerAppId,
        CachedGameDetection row)
    {
        await using var context = new AppDbContext(options);
        context.SteamDepotMappings.Add(new SteamDepotMapping
        {
            DepotId = depotId,
            AppId = ownerAppId,
            AppName = $"Resolved {ownerAppId}",
            IsOwner = true,
            Source = "SteamKit2-PICS"
        });
        context.CachedGameDetections.Add(row);
        await context.SaveChangesAsync();
    }

    private static UnknownGameResolutionService NewResolver(DbContextOptions<AppDbContext> options) =>
        new(new TestDbContextFactory(options), NullLogger<UnknownGameResolutionService>.Instance);

    private static DbContextOptions<AppDbContext> NewOptions() =>
        new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"unresolved_depot_flag_{Guid.NewGuid():N}")
            .Options;
}
