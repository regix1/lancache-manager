using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Coverage for restoring Epic banner art on prefill-discovered games without a user login:
/// the pre-merge landscape pick, the merge guard that protects existing art, and the Downloads
/// propagation it depends on.
/// </summary>
public class EpicMappingBannerArtTests
{
    private static DbContextOptions<AppDbContext> NewInMemoryOptions()
        => new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"epic_banner_{Guid.NewGuid():N}")
            .Options;

    private static EpicGameMapping NewMapping(string appId, string name, string? imageUrl)
        => new()
        {
            AppId = appId,
            Name = name,
            ImageUrl = imageUrl,
            DiscoveredAtUtc = DateTime.UtcNow,
            LastSeenAtUtc = DateTime.UtcNow,
            DiscoveredByHash = "hash",
            Source = "prefill-login"
        };

    private static Download NewDownload(string epicAppId, string? gameImageUrl)
        => new()
        {
            Service = "epicgames",
            ClientIp = "127.0.0.1",
            EpicAppId = epicAppId,
            GameImageUrl = gameImageUrl,
            StartTimeUtc = DateTime.UtcNow,
            StartTimeLocal = DateTime.UtcNow,
            EndTimeUtc = DateTime.UtcNow,
            EndTimeLocal = DateTime.UtcNow
        };

    /// <summary>
    /// Builds a real EpicMappingService against an in-memory database. Only MergeOwnedGamesAsync
    /// and PropagateGameImagesAsync are exercised by these tests - neither touches the
    /// auth/session dependencies, so those are stand-ins: a NullReturningProxy for interfaces
    /// (established pattern - see NullReturningProxy.cs, used the same way in
    /// AccountDisplayNameCaptureTests.cs) and null for the concrete EpicAuthStorageService (the
    /// login-only path, matching the null! convention already used for unexercised dependencies in
    /// CacheSizeConfigurationTests.cs).
    /// </summary>
    private static EpicMappingService NewService(DbContextOptions<AppDbContext> options)
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
            new TestDbContextFactory(options),
            tracker,
            scopeFactory,
            stateService);
    }

    // --- GetBestImageUrl: the landscape picker that must not regress to portrait art ---

    [Fact]
    public void GetBestImageUrl_PicksLandscapeOverPortrait()
    {
        var images = new List<EpicKeyImage>
        {
            new() { Type = "Thumbnail", Url = "https://cdn.epicgames.com/portrait.jpg", Width = 600, Height = 900 },
            new() { Type = "Other", Url = "https://cdn.epicgames.com/wide.jpg", Width = 1920, Height = 1080 }
        };

        var picked = EpicApiDirectClient.GetBestImageUrl(images, "Test Game");

        Assert.NotNull(picked);
        Assert.Contains("wide.jpg", picked);
    }

    [Fact]
    public void GetBestImageUrl_ReturnsNull_WhenNothingIsLandscape()
    {
        var images = new List<EpicKeyImage>
        {
            new() { Type = "Thumbnail", Url = "https://cdn.epicgames.com/portrait.jpg", Width = 600, Height = 900 }
        };

        Assert.Null(EpicApiDirectClient.GetBestImageUrl(images, "Test Game"));
    }

    // --- OwnedGame.KeyImages: additive field, other daemons must be unaffected ---

    [Fact]
    public void OwnedGame_DeserializesWithoutKeyImages_LeavesKeyImagesNull()
    {
        // Simulates the steam/xbox/battlenet/riot daemons, none of which send this field.
        const string json = """{"appId":"730","name":"Test Game","imageUrl":null}""";

        var game = JsonSerializer.Deserialize<OwnedGame>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(game);
        Assert.Null(game!.KeyImages);
    }

    [Fact]
    public void OwnedGame_DeserializesKeyImages_WhenPresent()
    {
        const string json = """
            {"appId":"epic-app","name":"Test Game","keyImages":[{"type":"DieselStoreFrontWide","url":"https://cdn.epicgames.com/wide.jpg","width":2560,"height":1440}]}
            """;

        var game = JsonSerializer.Deserialize<OwnedGame>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(game);
        var keyImages = Assert.Single(game!.KeyImages!);
        Assert.Equal("https://cdn.epicgames.com/wide.jpg", keyImages.Url);
    }

    // --- The HTTP path must not ship the raw catalog artwork to the browser ---

    /// <summary>
    /// Both game-picker endpoints return whatever this method hands back, and nothing in the browser
    /// reads keyImages - a few hundred titles' worth is hundreds of KB the page parses and discards.
    /// The picked ImageUrl has to survive; only the raw entries are dropped.
    /// </summary>
    [Fact]
    public async Task GetOwnedGamesAsync_DropsRawArtwork_BeforeItLeavesTheService()
    {
        var client = new FakeReconnectDaemonClient
        {
            OwnedGamesHandler = _ => Task.FromResult(new List<OwnedGame>
            {
                new()
                {
                    AppId = "epic-app",
                    Name = "Test Game",
                    ImageUrl = "https://cdn.epicgames.com/picked.jpg",
                    KeyImages = new List<EpicKeyImage>
                    {
                        new() { Type = "DieselStoreFrontWide", Url = "https://cdn.epicgames.com/wide.jpg", Width = 2560, Height = 1440 }
                    }
                }
            })
        };

        var (daemon, sessionId) = NewDaemonWithSession(client);

        var games = await daemon.GetOwnedGamesAsync(sessionId);

        var game = Assert.Single(games);
        Assert.Null(game.KeyImages);
        Assert.Equal("https://cdn.epicgames.com/picked.jpg", game.ImageUrl);
    }

    private static (EpicPrefillDaemonService Daemon, string SessionId) NewDaemonWithSession(FakeReconnectDaemonClient client)
    {
        var options = NewInMemoryOptions();
        var dbFactory = new TestDbContextFactory(options);
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var pathResolver = (IPathResolver)DispatchProxy.Create<IPathResolver, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

        var daemon = new TestableEpicDaemonService(
            notifications,
            new ConfigurationBuilder().Build(),
            pathResolver,
            stateService,
            new PrefillSessionService(dbFactory, NullLogger<PrefillSessionService>.Instance),
            new PrefillCacheService(dbFactory, NullLogger<PrefillCacheService>.Instance),
            NewService(options),
            new StaticOptionsMonitor<PrefillNetworkOptions>(new PrefillNetworkOptions()));

        var session = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            Platform = "Epic",
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = client
        };
        daemon.InjectSession(session);

        return (daemon, session.Id);
    }

    // _sessions is protected on PrefillDaemonServiceBase, so the test reaches it through a subclass
    // rather than starting a real container.
    private sealed class TestableEpicDaemonService : EpicPrefillDaemonService
    {
        public TestableEpicDaemonService(
            ISignalRNotificationService notifications,
            IConfiguration configuration,
            IPathResolver pathResolver,
            IStateService stateService,
            PrefillSessionService sessionService,
            PrefillCacheService cacheService,
            EpicMappingService mappingService,
            IOptionsMonitor<PrefillNetworkOptions> networkOptions)
            : base(NullLogger<EpicPrefillDaemonService>.Instance, notifications, configuration, pathResolver,
                stateService, sessionService, cacheService, mappingService, networkOptions,
                new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        {
        }

        public void InjectSession(DaemonSession session) => _sessions[session.Id] = session;
    }

    // --- Prefill merge path ---

    [Fact]
    public async Task MergeOwnedGamesAsync_PersistsPickedArt_ForArtlessPrefillGame()
    {
        var options = NewInMemoryOptions();
        var service = NewService(options);

        var game = new OwnedGame
        {
            AppId = "test-app",
            Name = "Test Game",
            ImageUrl = null,
            KeyImages = new List<EpicKeyImage>
            {
                new() { Type = "DieselStoreFrontWide", Url = "https://cdn.epicgames.com/wide.jpg", Width = 2560, Height = 1440 }
            }
        };

        // Mirrors the pre-merge pick in EpicPrefillDaemonService.CollectSessionGameMappingsAsync.
        game.ImageUrl = EpicApiDirectClient.GetBestImageUrl(game.KeyImages, game.Name);

        await service.MergeOwnedGamesAsync(new List<OwnedGame> { game }, "hash", "prefill-login");

        await using var assertDb = new AppDbContext(options);
        var mapping = await assertDb.EpicGameMappings.SingleAsync(m => m.AppId == "test-app");
        Assert.False(string.IsNullOrEmpty(mapping.ImageUrl));
        Assert.Contains("wide.jpg", mapping.ImageUrl);
    }

    [Fact]
    public async Task MergeOwnedGamesAsync_ArtlessUpdate_DoesNotWipeExistingArt()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicGameMappings.Add(NewMapping("test-app", "Test Game", "https://cdn.epicgames.com/existing.jpg"));
            await seed.SaveChangesAsync();
        }

        var service = NewService(options);
        var artlessUpdate = new OwnedGame { AppId = "test-app", Name = "Test Game", ImageUrl = null };

        await service.MergeOwnedGamesAsync(new List<OwnedGame> { artlessUpdate }, "hash2", "prefill-login");

        await using var assertDb = new AppDbContext(options);
        var mapping = await assertDb.EpicGameMappings.SingleAsync(m => m.AppId == "test-app");
        Assert.Equal("https://cdn.epicgames.com/existing.jpg", mapping.ImageUrl);
    }

    // --- Downloads propagation ---
    // No self-healing sweep exists: EpicGameMapping stores no artwork entries, so a sweep with
    // no Steam fallback would have had no art source at all and done nothing. Art-less rows heal
    // through the existing prefill merge instead - the daemon now returns artwork on every
    // owned-games fetch, so MergeOwnedGamesAsync_PersistsPickedArt_ForArtlessPrefillGame above
    // already covers that path. These two tests remain as regression pins on the existing
    // PropagateGameImagesAsync.

    [Fact]
    public async Task PropagateGameImagesAsync_UpdatesMatchingDownloadImageUrl()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicGameMappings.Add(NewMapping("test-app", "Test Game", "https://cdn.epicgames.com/wide.jpg"));
            seed.Downloads.Add(NewDownload("test-app", null));
            await seed.SaveChangesAsync();
        }

        var service = NewService(options);
        var updated = await service.PropagateGameImagesAsync(string.Empty);

        Assert.Equal(1, updated);

        await using var assertDb = new AppDbContext(options);
        var download = await assertDb.Downloads.SingleAsync(d => d.EpicAppId == "test-app");
        Assert.Equal("https://cdn.epicgames.com/wide.jpg", download.GameImageUrl);
    }

    /// <summary>
    /// Pins the exact regression the plan called out: PropagateGameImagesAsync must skip art-less
    /// mappings rather than assigning their null ImageUrl onto Downloads that already have art.
    /// </summary>
    [Fact]
    public async Task PropagateGameImagesAsync_SkipsArtlessMappings_DoesNotEraseExistingDownloadArt()
    {
        var options = NewInMemoryOptions();
        await using (var seed = new AppDbContext(options))
        {
            seed.EpicGameMappings.Add(NewMapping("test-app", "Test Game", null));
            seed.Downloads.Add(NewDownload("test-app", "https://cdn.epicgames.com/existing.jpg"));
            await seed.SaveChangesAsync();
        }

        var service = NewService(options);
        var updated = await service.PropagateGameImagesAsync(string.Empty);

        Assert.Equal(0, updated);

        await using var assertDb = new AppDbContext(options);
        var download = await assertDb.Downloads.SingleAsync(d => d.EpicAppId == "test-app");
        Assert.Equal("https://cdn.epicgames.com/existing.jpg", download.GameImageUrl);
    }
}
