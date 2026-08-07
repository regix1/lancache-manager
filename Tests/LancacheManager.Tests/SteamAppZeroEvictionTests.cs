using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A <see cref="Download"/> carrying <c>GameAppId = 0</c> has no Steam identity: 0 is the
/// sentinel a <see cref="CachedGameDetection"/> row uses for a named (Blizzard/Riot/Xbox) game,
/// and the identity key built in GamesOnDiskCalculator treats null and 0 alike. The Steam arms of
/// the eviction code used to test only <c>GameAppId != null</c>, so App 0 rows were pooled under
/// key 0 and that pooled figure was read back as one entity's own evicted total.
///
/// Two consequences are covered here: the detection load must not build a game out of the App 0
/// pool, and the detection eviction pass must not carry 0 into the id list whose update matches
/// every named row.
/// </summary>
public class SteamAppZeroEvictionTests
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
            .UseInMemoryDatabase($"steam_app_zero_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static Download SteamDownload(
        long? gameAppId,
        string? gameName,
        bool isEvicted,
        string clientIp,
        string service = "steam",
        long hitBytes = 1,
        long missBytes = 1)
        => new Download
        {
            Service = service,
            ClientIp = clientIp,
            StartTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            EndTimeLocal = DateTime.UtcNow,
            CacheHitBytes = hitBytes,
            CacheMissBytes = missBytes,
            IsActive = false,
            IsEvicted = isEvicted,
            GameAppId = gameAppId,
            GameName = gameName,
            Datasource = "default"
        };

    private static CachedGameDetection DetectionRow(
        long gameAppId,
        string? service,
        string gameName,
        bool isEvicted = false)
        => new CachedGameDetection
        {
            GameAppId = gameAppId,
            Service = service,
            GameName = gameName,
            CacheFilesFound = 0,
            TotalSizeBytes = 0,
            IsEvicted = isEvicted,
            LastDetectedUtc = DateTime.UtcNow,
            CreatedAtUtc = DateTime.UtcNow
        };

    // ---------------------------------------------------------------------------------------------
    // LoadDetectionAsync: the App 0 pool must not become a game
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LoadDetectionAsync_App0Downloads_DoNotSynthesizeGame()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            // App 0 rows with the partial shape the synthesis path looks for: an evicted
            // byte-backed row plus a live byte-backed sibling.
            seed.Downloads.AddRange(
                SteamDownload(0, "Some App 0 Traffic", true, "10.0.0.1", missBytes: 40_000_000),
                SteamDownload(0, "Other App 0 Traffic", false, "10.0.0.2", missBytes: 5_000_000),
                // A real Steam partial, so the response is populated and the assertion below is
                // about App 0 specifically rather than about an empty payload.
                SteamDownload(730, "Counter-Strike 2", true, "10.0.0.3"),
                SteamDownload(730, "Counter-Strike 2", false, "10.0.0.4"));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        Assert.Single(response!.Games!, g => g.GameAppId == 730);
        Assert.DoesNotContain(response.Games!, g => g.GameAppId == 0);
    }

    [Fact]
    public async Task LoadDetectionAsync_App0Downloads_DoNotFillDetectionRowEvictedTotal()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            // A row with no service takes the Steam branch of the evicted-total assignment, so
            // before the fix it read the App 0 pool as its own total.
            seed.CachedGameDetections.Add(DetectionRow(0, service: null, gameName: "Legacy Row"));
            seed.Downloads.AddRange(
                SteamDownload(0, "Some App 0 Traffic", true, "10.0.0.1", missBytes: 40_000_000),
                SteamDownload(0, "Other App 0 Traffic", true, "10.0.0.2", missBytes: 60_000_000));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var row = Assert.Single(response!.Games!, g => g.GameAppId == 0);
        Assert.Equal(0, row.EvictedDownloadsCount);
        Assert.Equal(0ul, row.EvictedBytes);
        Assert.Empty(row.EvictedSampleUrls);
    }

    [Fact]
    public async Task GetGamesToUnevict_App0Download_IsNotRecacheEvidence()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            // An evicted named row puts the sentinel 0 into the evicted-id list the Steam arm reads.
            seed.CachedGameDetections.Add(
                DetectionRow(0, "blizzard", "Overwatch", isEvicted: true));
            seed.Downloads.Add(
                SteamDownload(0, "Some App 0 Traffic", false, "10.0.0.1", missBytes: 5_000_000));
            await seed.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var targets = await NewDataService(options)
            .GetGamesToUnevictAsync(context, CancellationToken.None);

        Assert.DoesNotContain(0L, targets.SteamGameAppIds);
    }

    // ---------------------------------------------------------------------------------------------
    // EvictCachedGameDetectionsAsync: runs against Sqlite because the InMemory provider does not
    // support the ExecuteUpdate the eviction write uses.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task EvictCachedGameDetections_App0DownloadsAllEvicted_LeavesNamedRowsAlone()
    {
        using var connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var seed = new AppDbContext(options))
        {
            await seed.Database.EnsureCreatedAsync();

            seed.CachedGameDetections.AddRange(
                // Named row: identity is (Service, GameName); its GameAppId is the sentinel 0.
                DetectionRow(0, "blizzard", "Overwatch"),
                // A serviceless zero row is what puts 0 into the Steam id list in the first place.
                DetectionRow(0, service: null, gameName: "Legacy Row"),
                // Positive control: a real Steam game whose Downloads are all evicted must still
                // be flagged, so the App 0 exclusion cannot be hiding a broken eviction pass.
                DetectionRow(730, "steam", "Counter-Strike 2"));

            seed.Downloads.AddRange(
                SteamDownload(0, "Some App 0 Traffic", true, "10.0.0.1", missBytes: 40_000_000),
                SteamDownload(0, "Other App 0 Traffic", true, "10.0.0.2", missBytes: 60_000_000),
                SteamDownload(730, "Counter-Strike 2", true, "10.0.0.3"),
                SteamDownload(null, "Overwatch", false, "10.0.0.4", service: "blizzard"));
            await seed.SaveChangesAsync();
        }

        int evicted;
        await using (var run = new AppDbContext(options))
        {
            evicted = await CacheReconciliationService.EvictCachedGameDetectionsAsync(
                run, NullLogger.Instance, CancellationToken.None);
        }

        Assert.Equal(1, evicted);

        await using var assert = new AppDbContext(options);
        var named = await assert.CachedGameDetections.SingleAsync(g => g.GameName == "Overwatch");
        Assert.False(named.IsEvicted);

        var legacy = await assert.CachedGameDetections.SingleAsync(g => g.GameName == "Legacy Row");
        Assert.False(legacy.IsEvicted);

        var steam = await assert.CachedGameDetections.SingleAsync(g => g.GameAppId == 730);
        Assert.True(steam.IsEvicted);
    }
}
