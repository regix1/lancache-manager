using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
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
/// A fetch pass reads the list of games it will fetch art for when it starts, so rows written after
/// that are invisible to it. Depot mapping writes exactly such rows and then asks for a pass, which
/// is refused while one is running. What happens to that refused request is what these tests pin.
///
/// A null from StartFetchInBackgroundAsync is documented as "the lock was held and a follow-up is armed",
/// and callers drop their own fallbacks on the strength of that, so it is pinned for both flavors of
/// caller rather than only for the one that found the bug.
///
/// On the serialized collection because these tests drive the static execution lock inside
/// GameImageFetchService, which is process-wide: two classes taking it at once would refuse each
/// other's first start and fail whichever ran second.
/// </summary>
[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImageFetchFollowUpPassTests
{
    /// <summary>How long a pass that is supposed to run is given to reach its fetch.</summary>
    private static readonly TimeSpan Arrival = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long the test watches for a pass that must not happen. Each unwanted pass costs one scope
    /// and one count query, so a chain announces itself in well under this.
    /// </summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartRefusedWhileAPassRuns_FetchesAgainWhenThatPassEndsAsync(bool refusedAsksForEpicUrls)
    {
        var httpClients = new PausingHttpClientFactory();
        await using var provider = BuildProvider(httpClients);
        var tracker = NewTracker();
        var service = NewService(provider, tracker);

        var running = await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        Assert.NotNull(running);
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the first pass never reached its fetch");

        // False is depot mapping, which has just written GameAppId onto downloads the running pass
        // already read. True is the clear-the-cache path, which has just deleted every stored image.
        // Both are refused the execution lock, and neither may lose its request.
        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: refusedAsksForEpicUrls, RunTrigger.Scheduled));

        httpClients.Release();

        Assert.True(
            await httpClients.WaitForCallAsync(Arrival),
            "the refused request was dropped: no pass ran once the running one finished");

        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// The record is cleared when a pass takes the execution lock, before that pass reads its work
    /// list, and the empty-downloads exit at the top of the fetch is the one place a pass can leave
    /// without having read it. Leaving the record set there instead reads as the safer choice and is
    /// not: the pass that the exit would arm reads the same empty table and arms another, so the
    /// chain never ends. This is what stops that being introduced.
    /// </summary>
    [Fact]
    public async Task AFollowUpThatFindsNoDownloadsDoesNotChainMorePassesAsync()
    {
        var httpClients = new PausingHttpClientFactory();
        await using var provider = BuildProvider(httpClients);
        var tracker = NewTracker();
        var service = NewService(provider, tracker);

        Assert.NotNull(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the first pass never reached its fetch");
        Assert.Null(await service.StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));

        httpClients.Release();
        Assert.True(await httpClients.WaitForCallAsync(Arrival), "the follow-up pass never ran");

        // The Downloads table this provider builds is empty, so the follow-up returns at the top of
        // the fetch without reading a work list. Nothing may follow it. Read the count after the
        // window a chain would announce itself in, rather than asking whether one more pass arrived:
        // the number separates a single stray pass from a chain, and a boolean cannot.
        await Task.Delay(Quiet);
        var passes = httpClients.Calls;
        Assert.True(
            passes == 2,
            $"expected the running pass and its follow-up and nothing else, saw {passes}; more than "
                + "two means a pass that found nothing to do armed another one, which repeats");

        await WaitForPassesToFinishAsync(tracker);
    }

    /// <summary>
    /// Why the follow-up is armed inside the service rather than parked on the operation queue: a
    /// second Game Image Fetch is answered as one the running pass already covers, and the start
    /// delegate is never invoked. That answer is right for work whose scope is fixed when it is
    /// asked for, and wrong for a pass that read its work list before the rows existed.
    /// </summary>
    [Fact]
    public async Task ASecondImageFetchIsAnsweredAsAlreadyRunningRatherThanParkedAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            NullNotifications(),
            NullLogger<OperationQueueService>.Instance);

        tracker.RegisterOperation(
            OperationType.GameImageFetch, "Game Image Fetch", new CancellationTokenSource());

        var starts = 0;
        var answer = await queue.EnqueueAsync(
            OperationType.GameImageFetch,
            ConflictScope.Bulk(),
            "Game Image Fetch",
            () =>
            {
                Interlocked.Increment(ref starts);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None);

        Assert.False(answer.Queued);
        Assert.True(answer.AlreadyRunning);
        Assert.Equal(0, Volatile.Read(ref starts));
    }

    private static ServiceProvider BuildProvider(IHttpClientFactory httpClients)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(
            options => options.UseInMemoryDatabase($"game-image-fetch-{Guid.NewGuid():N}"));
        services.AddSingleton(httpClients);
        return services.BuildServiceProvider();
    }

    private static UnifiedOperationTracker NewTracker() => new(
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        NullLogger<UnifiedOperationTracker>.Instance);

    private static GameImageFetchService NewService(
        IServiceProvider provider, UnifiedOperationTracker tracker)
    {
        return new GameImageFetchService(
            provider,
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            NullProxy<IStateService>(),
            NullNotifications(),
            NullProxy<IImageCacheService>(),
            tracker);
    }

    /// <summary>
    /// Returns once every pass this test started has let go of the execution lock.
    /// </summary>
    /// <remarks>
    /// The lock is static and shared by every test in this class, so a pass still unwinding when a
    /// test returns is a pass the next test gets refused by, which reads as a failure of whichever
    /// test happened to run second. Waiting on the tracker rather than on the lock itself is what
    /// makes this honest: a pass releases the lock and only then completes its tracked operation
    /// (`GameImageFetchService.cs:185-186`), so an empty active list means the lock is already free.
    /// Reaching into the semaphore instead would take a permit the finishing pass is about to give
    /// back.
    /// </remarks>
    private static async Task WaitForPassesToFinishAsync(UnifiedOperationTracker tracker)
    {
        var deadline = DateTime.UtcNow + Arrival;
        while (tracker.GetActiveOperations(OperationType.GameImageFetch).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "a fetch pass never finished");
            await Task.Delay(10);
        }
    }

    private static ISignalRNotificationService NullNotifications() => NullProxy<ISignalRNotificationService>();

    private static T NullProxy<T>() where T : class => DispatchProxy.Create<T, NullReturningProxy>();

    /// <summary>
    /// Stands in for the HTTP client factory the fetch asks for before it reads anything, so a pass
    /// can be held inside the execution lock for as long as the test needs and each entry counted.
    /// </summary>
    private sealed class PausingHttpClientFactory : IHttpClientFactory
    {
        private readonly SemaphoreSlim _entered = new(0);
        private readonly ManualResetEventSlim _release = new(false);
        private int _calls;

        /// <summary>
        /// Passes that have reached their fetch. A count rather than a flag because the number is
        /// the diagnosis: three says one stray pass ran, thirty says a chain is running away.
        /// </summary>
        public int Calls => Volatile.Read(ref _calls);

        public HttpClient CreateClient(string name)
        {
            Interlocked.Increment(ref _calls);
            _entered.Release();
            _release.Wait(TimeSpan.FromSeconds(30));
            return new HttpClient();
        }

        public Task<bool> WaitForCallAsync(TimeSpan timeout) => _entered.WaitAsync(timeout);

        public void Release() => _release.Set();
    }
}
