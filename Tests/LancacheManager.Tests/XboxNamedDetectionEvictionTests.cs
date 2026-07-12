using LancacheManager.Core;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for the Xbox = NAMED-style service identity (brief §2) and the §12 Q2 evicted-named
/// recovery fix. Xbox rides the EXISTING Blizzard/Riot named-game machinery with NO new identity
/// bucket: a matched Xbox download is <c>Service='xbox'</c>, <c>GameAppId=NULL</c>,
/// <c>EpicAppId=NULL</c>, <c>GameName=&lt;title&gt;</c> (the detection row carries
/// <c>GameAppId=0</c>, never null). These tests therefore parameterize over xbox/blizzard/riot to
/// prove the code contains NO per-service hardcoding - Xbox is correct exactly because it is
/// indistinguishable from the already-shipped named services.
///
/// Run against the EF Core InMemory provider so the production recovery code paths execute
/// (same harness as <see cref="ServiceEvictionGateTests"/>).
/// </summary>
public class XboxNamedDetectionEvictionTests
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
            .UseInMemoryDatabase($"xbox_named_{Guid.NewGuid():N}")
            .Options;

    private static GameCacheDetectionDataService NewDataService(DbContextOptions<AppDbContext> options)
        => new GameCacheDetectionDataService(
            new InMemoryDbContextFactory(options),
            NullLogger<GameCacheDetectionDataService>.Instance);

    /// <summary>
    /// A named-game Download in the EVICTED state: GameAppId/EpicAppId null, Service set,
    /// GameName populated, IsEvicted=true. This is the row shape a matched Xbox (or Blizzard/Riot)
    /// download takes after its cache is evicted.
    /// </summary>
    private static Download EvictedNamedDownload(string service, string gameName)
        => new Download
        {
            Service = service,
            ClientIp = "10.0.0.1",
            StartTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            EndTimeLocal = DateTime.UtcNow,
            CacheHitBytes = 1,
            CacheMissBytes = 1,
            IsActive = false,
            IsEvicted = true,
            GameAppId = null,
            EpicAppId = null,
            GameName = gameName,
            Datasource = "default"
        };

    /// <summary>
    /// An ACTIVE named-game detection row (GameAppId=0, EpicAppId=null, Service+GameName set),
    /// the shape that shows in the Game Cache Detection grid and that named-game removal must delete.
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

    // -----------------------------------------------------------------------------------------
    // Criterion 2: identity key. Xbox flows through the named arm of GamesOnDiskCalculator.GetGameKey
    // (the public twin of the private GameCacheDetectionService.BuildGameIdentityKey, per its
    // docstring) producing `named:xbox<sep><name>` with NO new `xbox:` arm - byte-identical in shape
    // to Blizzard/Riot. No synthetic appId: a non-zero GameAppId would route to `steam:`.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("xbox", "Halo Infinite")]
    [InlineData("blizzard", "Diablo IV")]
    [InlineData("riot", "VALORANT")]
    public void GetGameKey_NamedGame_ReturnsNamedKey_NoServiceSpecificArm(string service, string gameName)
    {
        var game = new GameCacheInfo
        {
            GameAppId = 0, // named detection sentinel
            Service = service,
            GameName = gameName,
            EpicAppId = null
        };

        var key = GamesOnDiskCalculator.GetGameKey(game);

        // `named:<service><\x01><gameName>` - the \x01 separator cannot appear in a service/game name.
        Assert.Equal($"named:{service.ToLowerInvariant()}\x01{gameName}", key);
        Assert.StartsWith("named:", key);
        Assert.DoesNotContain("xbox:", key);   // no dedicated xbox identity arm
        Assert.DoesNotContain("steam:", key);  // not misrouted to the Steam delete endpoint
    }

    [Fact]
    public void GetGameKey_XboxKeyShape_MatchesBlizzardAndRiot()
    {
        string Key(string svc) => GamesOnDiskCalculator.GetGameKey(new GameCacheInfo
        {
            GameAppId = 0,
            Service = svc,
            GameName = "Same Title"
        });

        // The three named services differ ONLY by the service token - same code path, same structure.
        Assert.Equal(Key("blizzard").Replace("blizzard", "xbox"), Key("xbox"));
        Assert.Equal(Key("riot").Replace("riot", "xbox"), Key("xbox"));
    }

    // -----------------------------------------------------------------------------------------
    // §12 Q2 (the headline deliverable): an evicted NAMED game recovers as a (Service, GameName)
    // CachedGameDetection row, NOT a bare service-level CachedServiceDetection row.
    //   Part B: RecoverEvictedGamesAsync grows a named arm that creates the (0, Service, GameName) row.
    //   Part A: RecoverEvictedServicesAsync gains a `GameName == null` guard so it does NOT also
    //           sweep the same evicted named download into a service row.
    // Parameterized to prove the fix is service-agnostic (fixes Xbox AND latent Blizzard/Riot).
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("xbox", "Halo Infinite")]
    [InlineData("blizzard", "Diablo IV")]
    [InlineData("riot", "League of Legends")]
    public async Task RecoverEvicted_NamedGame_RecoversAsGameRow_NotServiceRow(string service, string gameName)
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            // An already-evicted named download whose detection row is missing (the gap §12 Q2 fixes).
            seed.Downloads.Add(EvictedNamedDownload(service, gameName));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        // Reconciliation order mirrors CacheReconciliationService: games first, then services.
        var gamesRecovered = await dataService.RecoverEvictedGamesAsync();
        var servicesRecovered = await dataService.RecoverEvictedServicesAsync();

        await using var assert = new AppDbContext(options);

        // Part B: the named game recovered as a (GameAppId=0, Service, GameName) detection row.
        var gameRow = await assert.CachedGameDetections.SingleOrDefaultAsync(
            g => g.GameAppId == 0 && g.Service == service && g.GameName == gameName);
        Assert.NotNull(gameRow);
        Assert.True(gameRow!.IsEvicted);
        Assert.Null(gameRow.EpicAppId);
        Assert.Equal(1, gamesRecovered);

        // Part A: NO bare service-level detection row was created for the named service.
        var bareServiceRow = await assert.CachedServiceDetections
            .AnyAsync(s => s.ServiceName == service);
        Assert.False(bareServiceRow);
        Assert.Equal(0, servicesRecovered);
    }

    /// <summary>
    /// §12 Q2 dedup: recovery keys on (GameAppId==0, Service, GameName), NOT the NULLS-DISTINCT
    /// unique index (all named rows share (0, NULL) so the index never dedups them). A second
    /// reconciliation pass must NOT create a duplicate detection row.
    /// </summary>
    [Fact]
    public async Task RecoverEvictedNamedGame_IsIdempotent_NoDuplicateRow()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(EvictedNamedDownload("xbox", "Forza Horizon 5"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        var first = await dataService.RecoverEvictedGamesAsync();
        var second = await dataService.RecoverEvictedGamesAsync();

        Assert.Equal(1, first);
        Assert.Equal(0, second); // already present -> dedup check skips it

        await using var assert = new AppDbContext(options);
        var count = await assert.CachedGameDetections
            .CountAsync(g => g.GameAppId == 0 && g.Service == "xbox" && g.GameName == "Forza Horizon 5");
        Assert.Equal(1, count);
    }

    /// <summary>
    /// §12 Q2 Part A in isolation: when a named game's detection row ALREADY exists,
    /// RecoverEvictedServicesAsync must still not create a service row for it (the GameName==null
    /// guard excludes named downloads entirely from service recovery).
    /// </summary>
    [Fact]
    public async Task RecoverEvictedServices_DoesNotCreateServiceRow_ForNamedDownload()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(EvictedNamedDownload("xbox", "Sea of Thieves"));
            // The named detection row is already present, so games-recovery would be a no-op.
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 0,
                GameName = "Sea of Thieves",
                Service = "xbox",
                EpicAppId = null,
                CacheFilesFound = 0,
                TotalSizeBytes = 0,
                IsEvicted = true,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        var servicesRecovered = await dataService.RecoverEvictedServicesAsync();

        Assert.Equal(0, servicesRecovered);

        await using var assert = new AppDbContext(options);
        Assert.False(await assert.CachedServiceDetections.AnyAsync(s => s.ServiceName == "xbox"));
    }

    /// <summary>
    /// Regression guard: a genuine SERVICE-level evicted download (no GameName) must STILL recover
    /// as a service row - the Part A guard only excludes named (GameName-bearing) rows, it must not
    /// break ordinary service recovery (e.g. a bare wsus/origin service with evicted downloads).
    /// </summary>
    [Fact]
    public async Task RecoverEvictedServices_StillRecoversBareServiceRow()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.Downloads.Add(new Download
            {
                Service = "wsus",
                ClientIp = "10.0.0.1",
                StartTimeUtc = DateTime.UtcNow,
                StartTimeLocal = DateTime.UtcNow,
                EndTimeUtc = DateTime.UtcNow,
                EndTimeLocal = DateTime.UtcNow,
                CacheHitBytes = 1,
                CacheMissBytes = 1,
                IsActive = false,
                IsEvicted = true,
                GameAppId = null,
                EpicAppId = null,
                GameName = null, // bare service download, not a named game
                Datasource = "default"
            });
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        var servicesRecovered = await dataService.RecoverEvictedServicesAsync();

        Assert.Equal(1, servicesRecovered);

        await using var assert = new AppDbContext(options);
        var row = await assert.CachedServiceDetections.SingleAsync(s => s.ServiceName == "wsus");
        Assert.True(row.IsEvicted);
        // And it did NOT leak into the game-detection table.
        Assert.False(await assert.CachedGameDetections.AnyAsync(g => g.Service == "wsus"));
    }

    [Fact]
    public async Task RecoverEvictedGames_DoesNotPromotePartiallyEvictedNamedGame()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            var evicted = EvictedNamedDownload("xbox", "Halo Infinite");
            var stillCached = EvictedNamedDownload("xbox", "Halo Infinite");
            stillCached.ClientIp = "10.0.0.2";
            stillCached.IsEvicted = false;
            seed.Downloads.AddRange(evicted, stillCached);
            await seed.SaveChangesAsync();
        }

        var recovered = await NewDataService(options).RecoverEvictedGamesAsync();

        await using var assert = new AppDbContext(options);
        Assert.Equal(0, recovered);
        Assert.False(await assert.CachedGameDetections.AnyAsync());
    }

    [Fact]
    public async Task RecoverEvictedServices_DoesNotPromotePartiallyEvictedService()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            var evicted = EvictedNamedDownload("wsus", "unused");
            evicted.GameName = null;
            var stillCached = EvictedNamedDownload("wsus", "unused");
            stillCached.ClientIp = "10.0.0.2";
            stillCached.GameName = null;
            stillCached.IsEvicted = false;
            seed.Downloads.AddRange(evicted, stillCached);
            await seed.SaveChangesAsync();
        }

        var recovered = await NewDataService(options).RecoverEvictedServicesAsync();

        await using var assert = new AppDbContext(options);
        Assert.Equal(0, recovered);
        Assert.False(await assert.CachedServiceDetections.AnyAsync());
    }

    // -----------------------------------------------------------------------------------------
    // Active named-game REMOVAL detection-row cleanup. Regression guard for the bug where removing
    // an Xbox named game (e.g. "Minecraft for Windows") succeeded on disk/DB but the detection row
    // survived, so the game kept showing in the Game Cache Detection grid after the frontend refetch.
    // The Steam removal deleted its row by GameAppId; the named removal was missing the equivalent
    // (Service, GameName) cleanup. Parameterized to prove the fix is service-agnostic.
    // -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("xbox", "Minecraft for Windows")]
    [InlineData("blizzard", "Diablo IV")]
    [InlineData("riot", "VALORANT")]
    public async Task RemoveNamedGameFromCache_DeletesOnlyTargetRow(string service, string gameName)
    {
        var options = NewInMemoryOptions();
        var otherNamedService = service == "xbox" ? "blizzard" : "xbox";

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedGameDetections.Add(NamedDetectionRow(service, gameName));                 // target
            seed.CachedGameDetections.Add(NamedDetectionRow(service, "Some Other Game"));         // same service, other game
            seed.CachedGameDetections.Add(NamedDetectionRow(otherNamedService, gameName));        // same name, other service
            // Steam + Epic rows that happen to share the name must survive (identity differs).
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 730,
                EpicAppId = null,
                Service = "steam",
                GameName = gameName,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            seed.CachedGameDetections.Add(new CachedGameDetection
            {
                GameAppId = 0,
                EpicAppId = "epic-123",
                Service = "epicgames",
                GameName = gameName,
                LastDetectedUtc = DateTime.UtcNow,
                CreatedAtUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.RemoveNamedGameFromCacheAsync(service, gameName);

        await using var assert = new AppDbContext(options);

        // Target named row is gone.
        Assert.False(await assert.CachedGameDetections.AnyAsync(
            g => g.GameAppId == 0 && g.EpicAppId == null && g.Service == service && g.GameName == gameName));
        // Same-service, different-game survives.
        Assert.True(await assert.CachedGameDetections.AnyAsync(
            g => g.Service == service && g.GameName == "Some Other Game"));
        // Same-name, different named service survives.
        Assert.True(await assert.CachedGameDetections.AnyAsync(
            g => g.GameAppId == 0 && g.EpicAppId == null && g.Service == otherNamedService && g.GameName == gameName));
        // Steam + Epic decoys survive.
        Assert.True(await assert.CachedGameDetections.AnyAsync(g => g.GameAppId == 730));
        Assert.True(await assert.CachedGameDetections.AnyAsync(g => g.EpicAppId == "epic-123"));
    }

    [Fact]
    public async Task RemoveNamedGameFromCache_MatchesServiceCaseInsensitively()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            // Stored lowercase, as the Xbox cache-split writes it.
            seed.CachedGameDetections.Add(NamedDetectionRow("xbox", "Forza Horizon 5"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        // Caller passes mixed case; the stored row is lowercase.
        await dataService.RemoveNamedGameFromCacheAsync("Xbox", "Forza Horizon 5");

        await using var assert = new AppDbContext(options);
        Assert.Empty(assert.CachedGameDetections);
    }

    [Fact]
    public async Task RemoveNamedGameFromCache_NoMatchingRow_IsNoOp()
    {
        var options = NewInMemoryOptions();

        await using (var seed = new AppDbContext(options))
        {
            seed.CachedGameDetections.Add(NamedDetectionRow("xbox", "Halo Infinite"));
            await seed.SaveChangesAsync();
        }

        var dataService = NewDataService(options);

        await dataService.RemoveNamedGameFromCacheAsync("xbox", "Not Present");

        await using var assert = new AppDbContext(options);
        Assert.Equal(1, await assert.CachedGameDetections.CountAsync());
    }

}
