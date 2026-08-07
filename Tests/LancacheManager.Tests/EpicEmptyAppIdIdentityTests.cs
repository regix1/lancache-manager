using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// An Epic app id is either absent (null) or a real id. The empty string is neither, and the two
/// halves of the codebase disagree about it: GamesOnDiskCalculator.GetDownloadGameKey buckets an
/// empty id as a named game (see GameMetricsKeyTests.EmptyEpicAppIdIsNotAnEpicIdentity), while the
/// detection and persistence layers test the column against null and would bucket the same row as
/// Epic. These tests pin the guards that keep the empty value from ever entering the system, and
/// the normalization that stops one from being mis-persisted if it already has.
/// </summary>
public class EpicEmptyAppIdIdentityTests
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
            .UseInMemoryDatabase($"epic_empty_appid_{Guid.NewGuid():N}")
            .Options;

    private static AppDbContext NewNpgsqlContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=epic_empty_appid_translation_smoke_test")
            .Options);

    /// <summary>
    /// Only MergeCdnPatternsAsync is exercised here, which touches nothing on the auth/session
    /// dependencies, so those are stand-ins: a NullReturningProxy for interfaces and null for the
    /// concrete EpicAuthStorageService, matching EpicMappingBannerArtTests.
    /// </summary>
    private static EpicMappingService NewMappingService(DbContextOptions<AppDbContext> options)
    {
        var epicApiClient = new EpicApiDirectClient(new HttpClient(), NullLogger<EpicApiDirectClient>.Instance);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var tracker = (IUnifiedOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>();
        var scopeFactory = (IServiceScopeFactory)DispatchProxy.Create<IServiceScopeFactory, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

        return new EpicMappingService(
            NullLogger<EpicMappingService>.Instance,
            epicApiClient,
            null!,
            notifications,
            new InMemoryDbContextFactory(options),
            tracker,
            scopeFactory,
            stateService);
    }

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    private static GameCacheInfo NewEpicGame(string? epicAppId, string gameName)
        => new()
        {
            GameAppId = 0,
            GameName = gameName,
            Service = "epicgames",
            EpicAppId = epicAppId,
            CacheFilesFound = 1,
            TotalSizeBytes = 1024,
            CacheFilePaths = new List<string> { $"/cache/{gameName}" },
            Datasources = new List<string> { "default" }
        };

    // -----------------------------------------------------------------------------------------
    // The write path: the daemon's get-cdn-info reply is not filtered, and CdnInfo.AppId defaults
    // to string.Empty, so an entry with a chunk URL but no app id would otherwise be persisted.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task MergeCdnPatternsAsync_RejectsEntryWithNoAppId()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeCdnPatternsAsync(new List<CdnInfo>
        {
            new() { AppId = string.Empty, Name = "Chunk With No App", ChunkBaseUrl = "/Builds/no-app/" }
        });

        using var db = new AppDbContext(options);
        Assert.Empty(await db.EpicCdnPatterns.ToListAsync());
    }

    [Fact]
    public async Task MergeCdnPatternsAsync_RejectsWhitespaceAppId()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeCdnPatternsAsync(new List<CdnInfo>
        {
            new() { AppId = "   ", Name = "Chunk With Blank App", ChunkBaseUrl = "/Builds/blank-app/" }
        });

        using var db = new AppDbContext(options);
        Assert.Empty(await db.EpicCdnPatterns.ToListAsync());
    }

    [Fact]
    public async Task MergeCdnPatternsAsync_KeepsEntryWithRealAppId()
    {
        var options = NewInMemoryOptions();
        var service = NewMappingService(options);

        await service.MergeCdnPatternsAsync(new List<CdnInfo>
        {
            new() { AppId = "Fortnite", Name = "Fortnite", ChunkBaseUrl = "/Builds/fortnite/" },
            new() { AppId = string.Empty, Name = "Chunk With No App", ChunkBaseUrl = "/Builds/no-app/" }
        });

        using var db = new AppDbContext(options);
        var stored = await db.EpicCdnPatterns.ToListAsync();
        var kept = Assert.Single(stored);
        Assert.Equal("Fortnite", kept.AppId);
    }

    // -----------------------------------------------------------------------------------------
    // The persistence buckets: an empty id must not take the Epic arm, because that arm groups on
    // EpicAppId and collapses every empty-id game into one surviving row.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveGamesAsync_EmptyEpicAppIdIsNotBucketedAsEpic()
    {
        var options = NewInMemoryOptions();
        var service = NewDataService(options);

        await service.SaveGamesAsync(
            new List<GameCacheInfo>
            {
                NewEpicGame(string.Empty, "Fortnite"),
                NewEpicGame(string.Empty, "Rocket League")
            },
            incremental: false);

        using var db = new AppDbContext(options);
        var rows = await db.CachedGameDetections.ToListAsync();

        // Both games survive. Grouping them on an empty EpicAppId would keep only the last.
        Assert.Equal(2, rows.Count);

        // Neither row stores the empty value: (GameAppId 0, EpicAppId "") is a single reusable slot
        // under the unique IX_CachedGameDetection_GameAppId_EpicAppId index, whereas null rows stay
        // distinct under PostgreSQL's NULLS-DISTINCT semantics.
        Assert.All(rows, row => Assert.Null(row.EpicAppId));
        Assert.Equal(
            new[] { "Fortnite", "Rocket League" },
            rows.Select(r => r.GameName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task SaveGamesAsync_RealEpicAppIdStillBucketsAsEpic()
    {
        var options = NewInMemoryOptions();
        var service = NewDataService(options);

        await service.SaveGamesAsync(
            new List<GameCacheInfo> { NewEpicGame("Fortnite", "Fortnite") },
            incremental: false);

        using var db = new AppDbContext(options);
        var row = Assert.Single(await db.CachedGameDetections.ToListAsync());
        Assert.Equal("Fortnite", row.EpicAppId);
    }

    // -----------------------------------------------------------------------------------------
    // SQL translation: both changed predicates run on the server. EF Core throws rather than
    // silently evaluating a Where clause on the client, so compiling the shape is the proof.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void UnresolvedEpicDownloadsQuery_TranslatesEmptyIdTestToSql()
    {
        using var context = NewNpgsqlContext();

        // Mirrors the unresolved-downloads query in EpicMappingService.ResolveDownloadsAsync.
        var sql = context.Downloads
            .Where(d => EF.Functions.Like(d.Service, "%epic%")
                     && string.IsNullOrEmpty(d.EpicAppId)
                     && d.LastUrl != null)
            .Select(d => d.Id)
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);

        // Npgsql expands IsNullOrEmpty into a null test plus an empty-string test, so a row already
        // stamped with an empty id is selected for re-resolution by the database, not in memory.
        Assert.Contains("IS NULL", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("''", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EpicCdnPatternQuery_TranslatesAppIdTestToSql()
    {
        using var context = NewNpgsqlContext();

        // Mirrors the pattern load in EpicMappingService.ResolveDownloadsAsync.
        var sql = context.EpicCdnPatterns
            .Where(p => !string.IsNullOrEmpty(p.AppId))
            .OrderByDescending(p => p.ChunkBaseUrl.Length)
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("''", sql, StringComparison.Ordinal);
    }
}
