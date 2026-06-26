using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for active Epic game removal cleaning up the correct detection row.
/// An Epic row is keyed by EpicAppId (not GameAppId), so the removal must target
/// EpicAppId and must not disturb same-name Steam or named-service rows.
/// </summary>
public class EpicDetectionRemovalTests
{
    private sealed class InMemoryDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<AppDbContext> options)
        {
            _options = options;
        }

        public AppDbContext CreateDbContext() => new AppDbContext(_options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AppDbContext(_options));
    }

    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"epic_removal_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    /// <summary>
    /// An ACTIVE named-game detection row (GameAppId=0, EpicAppId=null, Service+GameName set),
    /// used here as a decoy to confirm that Epic removal leaves named rows intact.
    /// </summary>
    private static CachedGameDetection NamedDetectionRow(string service, string gameName)
        => new CachedGameDetection
        {
            GameAppId = 0,
            EpicAppId = null,
            Service = service,
            GameName = gameName,
            CacheFilesFound = 1,
            TotalSizeBytes = 1,
            IsEvicted = false,
            LastDetectedUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

    /// <summary>
    /// Active Epic removal must delete the Epic detection row (EpicAppId != null, keyed by GameName)
    /// without relying on the Epic mapping loop, and must leave same-name Steam/named rows intact
    /// (identity differs).
    /// </summary>
    [Fact]
    public async Task RemoveEpicGameFromCache_DeletesEpicRow_LeavesOthers()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 0,
                EpicAppId = "epic-fortnite",
                Service = "epicgames",
                GameName = "Fortnite",
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            // Same name on Steam + a named service must survive.
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 730,
                EpicAppId = null,
                Service = "steam",
                GameName = "Fortnite",
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            seed.CachedGameDetections.Add(NamedDetectionRow("xbox", "Fortnite"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.RemoveEpicGameFromCacheAsync("Fortnite");

        await using var assert = new AppDbContext(options);
        // Epic row gone.
        Assert.False(await assert.CachedGameDetections.AnyAsync(g => g.EpicAppId == "epic-fortnite"));
        // Steam + named decoys survive.
        Assert.True(await assert.CachedGameDetections.AnyAsync(g => g.GameAppId == 730 && g.GameName == "Fortnite"));
        Assert.True(await assert.CachedGameDetections.AnyAsync(
            g => g.GameAppId == 0 && g.EpicAppId == null && g.Service == "xbox" && g.GameName == "Fortnite"));
    }
}
