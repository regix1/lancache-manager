using System.Net;
using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
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

    // The in-pass banner signal's gate interval (GameImageFetchService._bannerEmitGate).
    private static readonly TimeSpan BannerGate = TimeSpan.FromMilliseconds(2000);

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

        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
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

        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        await WaitForPassesToFinishAsync(tracker);

        var recorder = (RecordingNotifications)(object)notifications;
        var emits = recorder.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);
        // Exactly two: the Steam phase's in-pass emit, the Epic phase's held back by the 2000 ms
        // gate this close behind it, and the one every pass ending sends. Three would mean the gate
        // stopped holding anything back; one would mean the in-pass emit stopped happening at all.
        Assert.True(emits == 2, $"expected exactly two GameImagesUpdated, saw {emits}");
    }

    [Fact]
    public async Task AnImagePassWhoseEpicLinkRefreshFailedEndsAmberAsync()
    {
        await AssertEpicLinkRefreshFailureEndsAmberAsync(new DroppedConnectionHandler());
    }

    [Fact]
    public async Task AnEpicImageRefreshThatTimesOutEndsAmberAndThePassGoesOnAsync()
    {
        await AssertEpicLinkRefreshFailureEndsAmberAsync(new TimedOutHandler());
    }

    // A batch that stored banners inside the two-second window must still reach the browsers once the window has
    // passed, when the rest of the pass stores nothing for longer.
    [Fact]
    public async Task ABatchStoredInsideTheWindowIsSentBeforeAQuietPassEndsAsync()
    {
        var handler = new QuietStaleRefreshHandler();
        await using var services = BuildProvider(new SingleHandlerHttpClients(handler));
        await SeedQuietStretchAsync(services, withStaleImage: true);
        var tracker = NewTracker();
        var sent = new ScheduledRunReporterTests.CapturingNotificationService();
        var service = NewService(services, tracker, sent);

        // The Steam batch stores a banner and is sent; the Epic batch stores one inside the window and is held;
        // the stale refresh then stores nothing and does not answer for longer than the window.
        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        try
        {
            await handler.QuietStretchEntered.WaitAsync(PassTimeout);
            var deadline = DateTime.UtcNow + BannerGate + TimeSpan.FromSeconds(1);
            while (sent.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated) < 2
                && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }

            var refreshes = sent.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);
            Assert.True(
                refreshes >= 2,
                $"The Epic batch's stored banner never reached the browsers while the pass went quiet for more than "
                + $"{BannerGate.TotalSeconds:0} s: {refreshes} GameImagesUpdated before the pass ended.");
        }
        finally
        {
            handler.Release();
            await WaitForPassesToFinishAsync(tracker);
        }
    }

    // A held banner signal must not go out after the pass has ended and sent its own.
    [Fact]
    public async Task AHeldSignalSendsNothingAfterThePassEndedAsync()
    {
        var handler = new QuietStaleRefreshHandler();
        await using var services = BuildProvider(new SingleHandlerHttpClients(handler));
        await SeedQuietStretchAsync(services, withStaleImage: false);
        var tracker = NewTracker();
        var sent = new ScheduledRunReporterTests.CapturingNotificationService();
        var service = NewService(services, tracker, sent);

        // The Epic batch's signal is held, and the pass ends well inside the window, sending its own ending signal.
        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        await WaitForPassesToFinishAsync(tracker);
        var atEnd = sent.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);
        await Task.Delay(BannerGate + TimeSpan.FromMilliseconds(500));
        var later = sent.Events.Count(e => e.EventName == SignalREvents.GameImagesUpdated);

        Assert.True(
            later == atEnd,
            $"A held banner signal went out after the pass had ended: {later - atEnd} more GameImagesUpdated.");
        Assert.True(
            atEnd == 2,
            $"The pass ended with {atEnd} GameImagesUpdated instead of the Steam batch's one and its ending's one.");
    }

    private static async Task SeedQuietStretchAsync(IServiceProvider services, bool withStaleImage)
    {
        using var scope = services.CreateScope();
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
        if (withStaleImage)
        {
            db.GameImages.Add(new GameImage
            {
                AppId = "440",
                Service = "steam",
                ImageData = new byte[6000],
                SourceUrl = "https://example.com/440.jpg",
                FetchedAtUtc = DateTime.UtcNow.AddDays(-10)
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task AssertEpicLinkRefreshFailureEndsAmberAsync(HttpMessageHandler epicHandler)
    {
        var httpClients = new SingleHandlerHttpClients(new FailingHandler());
        using var epicHttp = new HttpClient(epicHandler);
        using var epicMapping = new EpicMappingService(
            NullLogger<EpicMappingService>.Instance,
            new EpicApiDirectClient(epicHttp, NullLogger<EpicApiDirectClient>.Instance),
            null!,
            NullProxy<ISignalRNotificationService>(),
            null!,
            NullProxy<IUnifiedOperationTracker>(),
            NullProxy<IServiceScopeFactory>(),
            NullProxy<IStateService>());
        // Signed in with a token that has not expired, so RefreshImagesAsync goes on to ask Epic for the catalog.
        typeof(EpicMappingService)
            .GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(epicMapping, true);
        typeof(EpicMappingService)
            .GetField("_currentTokens", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(epicMapping, new EpicOAuthTokens
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                ExpiresAt = DateTime.UtcNow.AddHours(1),
            });
        await using var imageServices = BuildProvider(httpClients, services => services.AddSingleton(epicMapping));
        using (var scope = imageServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
        var service = NewService(imageServices, tracker, notifications);

        var operationId = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: true, RunTrigger.Scheduled);
        Assert.NotNull(operationId);
        await WaitForPassesToFinishAsync(tracker);

        var run = Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == operationId);
        Assert.Equal("completed", run.Status);
        var warning = Assert.Single(run.Warnings);
        Assert.Equal("common.notifications.warnings.epicImageLinksNotRefreshed", warning.StageKey);
    }

    private static ServiceProvider BuildProvider(
        IHttpClientFactory httpClients, Action<IServiceCollection>? configure = null)
    {
        // The name is captured once here rather than generated inside the options lambda: that
        // lambda runs again on every DbContext instantiation, so a fresh Guid there would hand
        // every scope its own empty database and the seeded Downloads row would never be seen.
        var databaseName = $"banner-notify-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddSingleton(httpClients);
        // Every pass backfills Xbox banner URLs; without this service that step throws and leaves
        // an extra warning on the run. No Xbox mappings are seeded, so it never calls out.
        services.AddSingleton(ResolverUnmatchedScanTests.NewXboxService(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options));
        configure?.Invoke(services);
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

    /// <summary>Fails every request, so no phase stores anything.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Fails every request the way a dropped connection does.</summary>
    private sealed class DroppedConnectionHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("connection dropped");
    }

    /// <summary>Fails every request the way HttpClient reports its own timeout: a canceled task whose
    /// inner exception is the timeout.</summary>
    private sealed class TimedOutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
                new TimeoutException());
    }

    /// <summary>
    /// Answers every banner fetch with an image, except the stale refresh of app 440, which waits until the test
    /// lets it go and then fails, so that stretch stores nothing.
    /// </summary>
    private sealed class QuietStaleRefreshHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task QuietStretchEntered => _entered.Task;

        public void Release() => _released.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsoluteUri.Contains("440", StringComparison.Ordinal))
            {
                _entered.TrySetResult();
                await _released.Task.WaitAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[6000]) };
        }
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
