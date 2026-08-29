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
/// Pins the two GameImagesUpdated defects a force refresh has: a pass where every upstream fetch
/// fails must still tell clients it ended, and a pass that stores banners across more than one
/// phase must tell them more than once while it is still running rather than only at the end.
///
/// On the serialized collection for the same reason as GameImageFetchFollowUpPassTests: these
/// tests drive the static execution lock inside GameImageFetchService, which is process-wide.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImageFetchBannerNotificationTests
{
    private static readonly TimeSpan PassTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AllFetchesFailingAsync_StillEmitsGameImagesUpdatedOnceAsync()
    {
        var httpClients = new SingleHandlerHttpClients(new FailingHandler());
        await using var provider = BuildProvider(httpClients);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // A download with no GameAppId and a service the name-keyed phase does not recognize
            // clears the totalDownloads==0 exit without giving the Steam or Epic phases anything to
            // attempt - both return 0 candidates rather than 0 successes, which is what the old
            // emit guard actually read for those two phases. The stale GameImage row below is what
            // gives the pass something to fetch and fail.
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
                ImageData = new byte[6000],
                SourceUrl = "https://example.com/570.jpg",
                FetchedAtUtc = DateTime.UtcNow.AddDays(-10)
            });
            await db.SaveChangesAsync();
        }

        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var service = NewService(provider, tracker, notifications);

        Assert.NotNull(service.StartFetchInBackground(refreshEpicImageUrls: false));
        await WaitForPassesToFinishAsync(tracker);

        var recorder = (RecordingNotifications)(object)notifications;
        var emits = recorder.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);
        Assert.Equal(1, emits);
    }

    [Fact]
    public async Task StoringAcrossMoreThanOnePhaseAsync_EmitsGameImagesUpdatedMoreThanOnceAsync()
    {
        var httpClients = new SingleHandlerHttpClients(new SucceedingHandler());
        await using var provider = BuildProvider(httpClients);
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Downloads.Add(new Download
            {
                Service = "steam",
                ClientIp = "10.0.0.1",
                GameAppId = 570,
                GameName = "Dota 2"
            });
            db.EpicGameMappings.Add(new EpicGameMapping
            {
                AppId = "fortnite",
                Name = "Fortnite",
                ImageUrl = "https://example.com/fortnite.jpg"
            });
            await db.SaveChangesAsync();
        }

        var tracker = NewTracker();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var service = NewService(provider, tracker, notifications);

        Assert.NotNull(service.StartFetchInBackground(refreshEpicImageUrls: false));
        await WaitForPassesToFinishAsync(tracker);

        var recorder = (RecordingNotifications)(object)notifications;
        var emits = recorder.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);
        // Exactly two: the Steam phase's in-pass emit, the Epic phase's held back by the 2000 ms
        // gate this close behind it, and the one every pass ending sends. Three would mean the gate
        // stopped holding anything back; one would mean the in-pass emit stopped happening at all.
        Assert.True(emits == 2, $"expected exactly two GameImagesUpdated, saw {emits}");
    }

    private static ServiceProvider BuildProvider(IHttpClientFactory httpClients)
    {
        // The name is captured once here rather than generated inside the options lambda: that
        // lambda runs again on every DbContext instantiation, so a fresh Guid there would hand
        // every scope its own empty database and the seeded Downloads row would never be seen.
        var databaseName = $"banner-notify-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(httpClients);
        return services.BuildServiceProvider();
    }

    private static UnifiedOperationTracker NewTracker() => new(
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        NullLogger<UnifiedOperationTracker>.Instance);

    private static GameImageFetchService NewService(
        IServiceProvider provider, UnifiedOperationTracker tracker, ISignalRNotificationService notifications)
    {
        return new GameImageFetchService(
            provider,
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            NullProxy<IStateService>(),
            notifications,
            NullProxy<IImageCacheService>(),
            tracker);
    }

    /// <summary>
    /// Returns once every pass this test started has let go of the execution lock. See
    /// GameImageFetchFollowUpPassTests for why the tracker is what is polled rather than the lock.
    /// </summary>
    private static async Task WaitForPassesToFinishAsync(UnifiedOperationTracker tracker)
    {
        var deadline = DateTime.UtcNow + PassTimeout;
        while (tracker.GetActiveOperations(OperationType.GameImageFetch).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "a fetch pass never finished");
            await Task.Delay(10);
        }
    }

    private static T NullProxy<T>() where T : class => DispatchProxy.Create<T, NullReturningProxy>();

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

    /// <summary>Fails every request, so no phase stores anything.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Succeeds on every request with a payload past MinImageBytes, so any phase given a
    /// candidate stores it.</summary>
    private sealed class SucceedingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[6000])
            });
    }
}
