using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Regression coverage for response-only partial-eviction projections in
/// <see cref="GameCacheDetectionDataService.LoadDetectionAsync"/>. Partials with no persisted
/// detection row must appear in the load payload (Evicted Items) without RecoverEvicted*
/// promoting them into the database.
/// </summary>
public class LoadDetectionPartialEvictionProjectionTests
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
            .UseInMemoryDatabase($"partial_evict_proj_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static Download ByteBackedDownload(
        string? service,
        string? gameName,
        bool isEvicted,
        string clientIp,
        long? gameAppId = null,
        string? epicAppId = null,
        long hitBytes = 1,
        long missBytes = 1)
    {
        var download = new Download
        {
            Service = service ?? string.Empty,
            ClientIp = clientIp,
            StartTimeUtc = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            CacheHitBytes = hitBytes,
            CacheMissBytes = missBytes,
            IsActive = false,
            IsEvicted = isEvicted,
            GameAppId = gameAppId,
            EpicAppId = epicAppId,
            Datasource = "default"
        };
        // Service-residual rows must keep GameName null (not empty string) so they match
        // serviceEvictedMap / RecoverEvictedServicesAsync filters.
        if (gameName != null)
        {
            download.GameName = gameName;
        }

        return download;
    }

    [Fact]
    public async Task LoadDetectionAsync_PartiallyEvictedWsus_NoDetectionRow_SynthesizesService()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("wsus", null, isEvicted: true, clientIp: "10.0.0.1", missBytes: 50_000_000),
                ByteBackedDownload("wsus", null, isEvicted: false, clientIp: "10.0.0.2", missBytes: 1_000_000));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var wsus = Assert.Single(response!.Services!, s =>
            string.Equals(s.ServiceName, "wsus", StringComparison.OrdinalIgnoreCase));
        Assert.False(wsus.IsEvicted);
        Assert.Equal(1, wsus.EvictedDownloadsCount);
        Assert.Equal(0, wsus.CacheFilesFound);
        Assert.Equal(0u, wsus.TotalSizeBytes);
        Assert.Equal(0, response.TotalServicesDetected);

        await using var assert = new AppDbContext(options);
        Assert.False(await assert.CachedServiceDetections.AnyAsync());
    }

    [Fact]
    public async Task LoadDetectionAsync_PartiallyEvictedSteam_NoDetectionRow_SynthesizesGame()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("steam", "Counter-Strike 2", true, "10.0.0.1", gameAppId: 730),
                ByteBackedDownload("steam", "Counter-Strike 2", false, "10.0.0.2", gameAppId: 730));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var game = Assert.Single(response!.Games!, g => g.GameAppId == 730);
        Assert.False(game.IsEvicted);
        Assert.Equal(1, game.EvictedDownloadsCount);
        Assert.Equal(0, game.CacheFilesFound);
        Assert.Equal(0, response.TotalGamesDetected);
    }

    [Fact]
    public async Task LoadDetectionAsync_PartiallyEvictedEpic_NoDetectionRow_SynthesizesGame()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("epicgames", "Fortnite", true, "10.0.0.1", epicAppId: "fortnite"),
                ByteBackedDownload("epicgames", "Fortnite", false, "10.0.0.2", epicAppId: "fortnite"));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var game = Assert.Single(response!.Games!, g => g.EpicAppId == "fortnite");
        Assert.False(game.IsEvicted);
        Assert.Equal(1, game.EvictedDownloadsCount);
        Assert.Equal(0, game.CacheFilesFound);
    }

    [Fact]
    public async Task LoadDetectionAsync_PartiallyEvictedNamedGame_NoDetectionRow_SynthesizesGame()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("xbox", "Halo Infinite", true, "10.0.0.1"),
                ByteBackedDownload("xbox", "Halo Infinite", false, "10.0.0.2"));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        var game = Assert.Single(response!.Games!, g =>
            g.GameAppId == 0 && g.GameName == "Halo Infinite"
            && string.Equals(g.Service, "xbox", StringComparison.OrdinalIgnoreCase));
        Assert.False(game.IsEvicted);
        Assert.Equal(1, game.EvictedDownloadsCount);
        Assert.Equal(0, game.CacheFilesFound);
        Assert.Empty(response.Services!);
    }

    [Fact]
    public async Task LoadDetectionAsync_MixedNamedAndServiceResidual_ProducesDisjointRows()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("blizzard", "Diablo IV", true, "10.0.0.1"),
                ByteBackedDownload("blizzard", "Diablo IV", false, "10.0.0.2"),
                ByteBackedDownload("blizzard", null, true, "10.0.0.3"),
                ByteBackedDownload("blizzard", null, false, "10.0.0.4"));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        Assert.Contains(response!.Games!, g => g.GameName == "Diablo IV" && g.EvictedDownloadsCount == 1);
        Assert.Contains(response.Services!, s =>
            string.Equals(s.ServiceName, "blizzard", StringComparison.OrdinalIgnoreCase)
            && s.EvictedDownloadsCount == 1);
    }

    [Fact]
    public async Task LoadDetectionAsync_CaseMismatchExistingServiceRow_DoesNotDuplicateSynthetic()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("WSUS", null, true, "10.0.0.1"),
                ByteBackedDownload("WSUS", null, false, "10.0.0.2"));
            seed.CachedServiceDetections.Add(new CachedServiceDetection
            {
                ServiceName = "wsus",
                CacheFilesFound = 177,
                TotalSizeBytes = 11_000_000,
                SampleUrlsJson = "[]",
                CacheFilePathsJson = "[]",
                DatasourcesJson = "[\"default\"]",
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        Assert.Single(response!.Services!);
        var wsus = response.Services![0];
        Assert.Equal("wsus", wsus.ServiceName);
        Assert.Equal(177, wsus.CacheFilesFound);
        Assert.Equal(1, wsus.EvictedDownloadsCount);
        Assert.False(wsus.IsEvicted);
    }

    [Fact]
    public async Task LoadDetectionAsync_ExistingRowWithFiles_EnrichmentOnly_NoSyntheticDuplicate()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("steam", "Apex", true, "10.0.0.1", gameAppId: 1172470),
                ByteBackedDownload("steam", "Apex", false, "10.0.0.2", gameAppId: 1172470));
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 1172470,
                GameName = "Apex Legends",
                CacheFilesFound = 100,
                TotalSizeBytes = 1_000_000,
                Service = "steam",
                IsEvicted = false,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        Assert.NotNull(response);
        Assert.Single(response!.Games!, g => g.GameAppId == 1172470);
        var game = response.Games!.Single(g => g.GameAppId == 1172470);
        Assert.Equal(100, game.CacheFilesFound);
        Assert.Equal(1, game.EvictedDownloadsCount);
        Assert.Equal(1, response.TotalGamesDetected);
    }

    [Fact]
    public async Task LoadDetectionAsync_ZeroByteLiveSibling_DoesNotSynthesizePartial()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("wsus", null, true, "10.0.0.1"),
                ByteBackedDownload("wsus", null, false, "10.0.0.2", hitBytes: 0, missBytes: 0));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);

        // Fully evicted from the byte-backed perspective: synthesis stays partial-only.
        // RecoverEvicted* (not LoadDetection) owns inserting the full-eviction row.
        Assert.True(response == null || response.Services!.Count == 0);
        await using var assert = new AppDbContext(options);
        Assert.False(await assert.CachedServiceDetections.AnyAsync());
    }

    [Fact]
    public async Task LoadDetectionAsync_SyntheticsOnly_BuildSucceedsWithZeroAggregate()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("wsus", null, true, "10.0.0.1"),
                ByteBackedDownload("wsus", null, false, "10.0.0.2"));
            await seed.SaveChangesAsync();
        }

        var response = await NewDataService(options).LoadDetectionAsync(includeCacheFilePaths: false);
        Assert.NotNull(response);
        Assert.NotNull(response!.DiskSummary);
        Assert.Equal(0ul, response.DiskSummary.Value.TotalBytes);
        Assert.Equal(0, response.TotalGamesDetected);
        Assert.Equal(0, response.TotalServicesDetected);

        var built = CachedDetectionResponseBuilder.Build(
            response.Games ?? [],
            response.Services,
            response.TotalServicesDetected,
            response.StartTime,
            slimForDashboard: false,
            diskSummary: response.DiskSummary);
        Assert.True(built.HasCachedResults);
        Assert.Equal(0, built.TotalGamesDetected);
        Assert.Equal(0, built.TotalServicesDetected);
        Assert.Equal(0ul, built.IdentifiedCacheBytes);
    }

    [Fact]
    public async Task RecoverEvictedServices_StillDoesNotPersistPartials()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.AddRange(
                ByteBackedDownload("wsus", null, true, "10.0.0.1"),
                ByteBackedDownload("wsus", null, false, "10.0.0.2"));
            await seed.SaveChangesAsync();
        }

        var recovered = await NewDataService(options).RecoverEvictedServicesAsync();
        Assert.Equal(0, recovered);
        await using var assert = new AppDbContext(options);
        Assert.False(await assert.CachedServiceDetections.AnyAsync());
    }
}
