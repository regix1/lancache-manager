using System.Net;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the two halves of refreshing banners in place rather than deleting them first.
///
/// Clearing the cache used to empty the GameImages table, so /available reported only what had been
/// re-stored so far and every banner past the first batch dropped to a placeholder for the length of
/// the pass. Rows are now backdated into the stale-refresh phase instead, which overwrites each one
/// where it stands, so no banner leaves the screen. The second half is what keeps that affordable:
/// re-fetching art that came back byte-identical must not move the version the banner URL carries,
/// or every client would re-download every banner on every pass.
///
/// On the serialized collection for the same reason as the other image tests: they drive the static
/// execution lock inside GameImageFetchService, which is process-wide.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImageRefreshInPlaceTests
{
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StaleCutoff = TimeSpan.FromDays(7);

    // ImageCacheService.ClearCacheAsync marks every row for re-fetch with a single ExecuteUpdate
    // statement, and that half has NO test here. The InMemory provider rejects ExecuteUpdate and
    // ExecuteDelete outright, and the only provider in reach that runs them, SQLite, pulls
    // SQLitePCLRaw.lib.e_sqlite3, which carries a known high-severity advisory that this solution's
    // NuGet audit turns into a build error. Loading the rows instead of a set-based statement would
    // pull every banner's bytes into memory on a real install, which is not a trade worth making for
    // testability. The tests below cover the half that is reachable: what the stale phase does with
    // the rows once they are marked.

    [Fact]
    public async Task ArtThatCameBackUnchangedDoesNotMoveItsVersionOrAnnounceAsync()
    {
        // Seeded bytes and served bytes are both 6000 zeros, so the refresh stores art identical to
        // what was already there.
        var (emits, image) = await RunStalePassAsync(seededImage: new byte[6000], servedImage: new byte[6000]);

        // The banner URL is versioned by UpdatedAtUtc ?? FetchedAtUtc. It must stay back where it was,
        // or every client re-downloads a banner whose bytes are identical.
        Assert.NotNull(image.UpdatedAtUtc);
        Assert.True(image.UpdatedAtUtc < DateTime.UtcNow - StaleCutoff,
            $"the version moved to {image.UpdatedAtUtc:o} for art that did not change");

        // FetchedAtUtc still has to move, or this row is re-fetched on every pass forever.
        Assert.True(image.FetchedAtUtc > DateTime.UtcNow - StaleCutoff,
            "the staleness clock did not move, so this row would be re-fetched every pass");

        // Only the one ungated emit that every pass ends with. No mid-pass announcement, because
        // there is nothing new to put on screen.
        Assert.Equal(1, emits);
    }

    [Fact]
    public async Task ArtThatActuallyChangedMovesItsVersionAndAnnouncesAsync()
    {
        var seeded = Enumerable.Repeat((byte)1, 6000).ToArray();
        var (emits, image) = await RunStalePassAsync(seededImage: seeded, servedImage: new byte[6000]);

        Assert.NotNull(image.UpdatedAtUtc);
        Assert.Equal(new byte[6000], image.ImageData);

        // The mid-pass announcement plus the terminal one: this is what puts new art on screen while
        // the pass is still running instead of only at the end.
        Assert.Equal(2, emits);
    }

    private static async Task<(int Emits, GameImage Image)> RunStalePassAsync(
        byte[] seededImage,
        byte[] servedImage)
    {
        var databaseName = $"image-refresh-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton<IHttpClientFactory>(new SingleHandlerHttpClients(new FixedBytesHandler(servedImage)));
        await using var provider = services.BuildServiceProvider();

        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Clears the totalDownloads==0 exit without handing the Steam, Epic or name-keyed phases
            // a candidate, so the stale phase is the only one that does any work.
            db.Downloads.Add(new Download
            {
                Service = "origin",
                ClientIp = "10.0.0.1",
                GameName = "Some Origin Game"
            });
            db.GameImages.Add(new GameImage
            {
                AppId = "570",
                Service = "steam",
                ImageData = seededImage,
                SourceUrl = "https://example.com/570.jpg",
                FetchedAtUtc = DateTime.UtcNow.AddDays(-10)
            });
            await db.SaveChangesAsync();
        }

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();

        var service = new GameImageFetchService(
            provider,
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            DispatchProxy.Create<IStateService, NullReturningProxy>(),
            notifications,
            DispatchProxy.Create<IImageCacheService, NullReturningProxy>(),
            tracker);

        Assert.NotNull(service.StartFetchInBackground(refreshEpicImageUrls: false));

        var deadline = DateTime.UtcNow + PassTimeout;
        while (tracker.GetActiveOperations(OperationType.GameImageFetch).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "a fetch pass never finished");
            await Task.Delay(10);
        }

        var recorder = (RecordingNotifications)(object)notifications;
        var emits = recorder.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);

        using var readScope = provider.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var image = await readDb.GameImages.AsNoTracking().SingleAsync();

        return (emits, image);
    }

    /// <summary>Hands out one HttpClient per requested name, all backed by the same handler.</summary>
    private sealed class SingleHandlerHttpClients : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public SingleHandlerHttpClients(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>Answers every request with the same payload, so a refresh always succeeds.</summary>
    private sealed class FixedBytesHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public FixedBytesHandler(byte[] payload)
        {
            _payload = payload;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            });
    }
}
