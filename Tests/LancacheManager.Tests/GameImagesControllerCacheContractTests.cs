using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the image endpoints' HTTP contract: clearing the cache hands the re-fetch to the background
/// and answers immediately, and a served banner stays revalidatable so an unchanged one costs a 304.
/// </summary>
/// <remarks>
/// Several of these hold GameImageFetchService's execution lock, which is static, so they cannot run
/// beside another class that also takes it. This collection keeps them off the parallel schedule.
/// </remarks>
[CollectionDefinition(nameof(GameImageExecutionLockCollection), DisableParallelization = true)]
public sealed class GameImageExecutionLockCollection
{
}

[Collection(nameof(GameImageExecutionLockCollection))]
public sealed class GameImagesControllerCacheContractTests
{
    [Fact]
    public async Task ClearImageCache_Accepts202WhileTheFetchPassIsStillRunningAsync()
    {
        using var passStarted = new ManualResetEventSlim(false);
        using var releasePass = new ManualResetEventSlim(false);

        // A real tracker: the pass now completes through a ScheduledRunReporter, whose completion
        // waits on the tracker invoking the terminal-emit callback. A proxy that swallows
        // CompleteOperation never invokes it and deadlocks the pass while it holds the static
        // execution lock.
        var tracker = NewTracker();

        var queue = new RecordingOperationQueue(new QueuedOperationResponse
        {
            OperationId = Guid.NewGuid(),
            Queued = true,
            Status = "waiting"
        });

        var fetchServices = new BlockingServiceProvider(passStarted, releasePass);
        var controller = CreateController(
            fetchServices,
            tracker,
            conflict: null,
            queue);

        var result = await controller.ClearImageCacheAsync(CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<ImageCacheClearResponse>(accepted.Value);

        // The pass is under way, and the response arrived without waiting for it to end.
        Assert.True(passStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.NotEmpty(tracker.GetActiveOperations(OperationType.GameImageFetch));

        // Refreshing Epic's image URLs calls out to Epic's catalog, so it must belong to the pass
        // and not to the request. The pass is still parked on resolving the Epic service, which the
        // response above already beat, so the request cannot have waited on that network call.
        Assert.Equal(typeof(EpicMappingService), fetchServices.FirstResolved);

        // The reported generation is the one the browser must use right now, not one captured
        // before a pass that bumps it again.
        Assert.Equal(GameImagesController.CacheGeneration, body.CacheGeneration);

        releasePass.Set();
        await WaitForPassesToFinishAsync(tracker);
    }

    [Fact]
    public async Task ClearImageCache_WhenConflict_EnqueuesGameImageFetchAsync()
    {
        using var passStarted = new ManualResetEventSlim(false);
        using var releasePass = new ManualResetEventSlim(true);

        var queuedId = Guid.NewGuid();
        var queue = new RecordingOperationQueue(new QueuedOperationResponse
        {
            OperationId = queuedId,
            Queued = true,
            Status = "waiting"
        });

        var conflict = new OperationConflictResponse
        {
            StageKey = "errors.conflict.globalOperationActive",
            Error = "blocked"
        };

        var controller = CreateController(
            new BlockingServiceProvider(passStarted, releasePass),
            CreateDefaultProxy<IUnifiedOperationTracker>(),
            conflict,
            queue);

        var result = await controller.ClearImageCacheAsync(CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result);
        var body = Assert.IsType<QueuedOperationResponse>(accepted.Value);
        Assert.Equal(queuedId, body.OperationId);

        Assert.Equal(OperationType.GameImageFetch, queue.Type);
        Assert.Equal(ConflictScope.Bulk(), queue.Scope);
        Assert.Equal("Game Image Fetch", queue.DisplayName);

        // Nothing may start while the conflict stands - the queue owns the start path from here.
        Assert.False(passStarted.IsSet);
    }

    /// <summary>
    /// Refresh banners deletes every stored row before it starts the re-fetch. If a fetch pass is
    /// already running, the queue cannot carry that re-fetch: it reads a second request under the
    /// same display name as an idempotent accept and answers "already running" without parking a
    /// waiter, and the running pass read its work list before the delete. Sending it there leaves
    /// every banner gone until the next scheduled tick, up to half an hour later.
    /// </summary>
    /// <param name="activeOperationType">
    /// The conflict checker's answer. It reports a duplicate once the running pass has registered its
    /// tracker operation, and nothing at all in the window before that, so both reach this path.
    /// </param>
    [Theory]
    [InlineData(null)]
    [InlineData("GameImageFetch")]
    public async Task ClearImageCache_WhileAFetchPassRuns_ArmsTheRefetchRatherThanQueueingItAsync(
        string? activeOperationType)
    {
        var passes = new CountingServiceProvider();
        var tracker = NewTracker();
        var images = CreateDefaultProxy<IImageCacheService>();

        // A pass is already under way and holds the execution lock.
        Assert.NotNull(await CreateFetchService(passes, tracker, images)
            .StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(passes.WaitForFirstPass(), "the first pass never started");

        var conflict = activeOperationType == null
            ? null
            : new OperationConflictResponse
            {
                StageKey = "errors.conflict.duplicate",
                Error = "A GameImageFetch operation for the same target is already in progress.",
                ActiveOperationId = Guid.NewGuid(),
                ActiveOperationType = activeOperationType
            };

        var queue = new RecordingOperationQueue(new QueuedOperationResponse());
        var controller = CreateController(passes, tracker, conflict, queue, images);

        var accepted = Assert.IsType<AcceptedResult>(
            await controller.ClearImageCacheAsync(CancellationToken.None));
        Assert.IsType<ImageCacheClearResponse>(accepted.Value);

        // Not parked. The queue is exactly what would swallow it.
        Assert.Null(queue.Type);

        // The running pass finishes and starts the follow-up, which reads the table this request
        // emptied and refills it.
        passes.ReleaseFirstPass();
        Assert.True(passes.WaitForSecondPass(), "the cleared cache was never re-fetched");
    }

    /// <summary>
    /// Someone presses Refresh banners and closes the tab, so the request is aborted. Clearing the
    /// rows takes no cancellation token, so it finishes; the conflict check is what runs next and it
    /// throws on an aborted request while walking the active operations
    /// (OperationConflictChecker.cs:49). Without cover, every banner is deleted with nothing started
    /// and nothing armed.
    /// </summary>
    [Fact]
    public async Task ClearImageCache_WhenTheCallerHasGoneAway_StillArmsTheRefetchAsync()
    {
        var passes = new CountingServiceProvider();
        var tracker = NewTracker();
        var images = CreateDefaultProxy<IImageCacheService>();

        // A pass is already running, so the follow-up is the only thing that can refill the table.
        // It is also what makes the throw reachable: the checker only tests the token once it has an
        // active operation to walk.
        Assert.NotNull(await CreateFetchService(passes, tracker, images)
            .StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled));
        Assert.True(passes.WaitForFirstPass(), "the first pass never started");

        var controller = CreateController(
            passes, tracker, conflict: null, new RecordingOperationQueue(new QueuedOperationResponse()), images);

        using var callerGone = new CancellationTokenSource();
        await callerGone.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.ClearImageCacheAsync(callerGone.Token));

        passes.ReleaseFirstPass();
        Assert.True(passes.WaitForSecondPass(), "the deleted rows were left with nothing to refill them");
    }

    [Fact]
    public async Task AvailableImages_ReportsTheSteamVersionForASteamFirstSlug_SoItsUrlIsSafeToPinAsync()
    {
        // Sea of Thieves is one of the 38 Xbox titles steam_fallback_appids.json maps to a Steam
        // appId, so the name-keyed route serves Steam's header for it once one has been fetched.
        const string slug = "sea-of-thieves";
        const string steamAppId = "1172620";
        var steamStoredAtUtc = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);
        var slugStoredAtUtc = new DateTime(2026, 8, 1, 9, 30, 0, DateTimeKind.Utc);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"game-images-steam-first-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.GameImages.AddRange(
            new GameImage
            {
                AppId = steamAppId,
                Service = "steam",
                ImageData = ArtBytes,
                ContentType = "image/jpeg",
                FetchedAtUtc = steamStoredAtUtc
            },
            new GameImage
            {
                AppId = slug,
                Service = "xbox",
                ImageData = [9, 9, 9],
                ContentType = "image/jpeg",
                FetchedAtUtc = slugStoredAtUtc
            });
        await context.SaveChangesAsync();

        var imageCacheService = CreateProxy<IImageCacheService>((method, args) =>
            method.Name == nameof(IImageCacheService.GetImageAsync)
                && (string)args![0]! == steamAppId
                && (string)args[1]! == "steam"
                    ? Task.FromResult<(byte[] imageBytes, string contentType, DateTime storedAtUtc)?>(
                        (ArtBytes, "image/jpeg", steamStoredAtUtc))
                    : DefaultReturn(method.ReturnType));

        var controller = CreateController(
            new BlockingServiceProvider(new ManualResetEventSlim(false), new ManualResetEventSlim(true)),
            CreateDefaultProxy<IUnifiedOperationTracker>(),
            conflict: null,
            new RecordingOperationQueue(new QueuedOperationResponse()),
            imageCacheService,
            context);

        var listed = Assert.IsType<AvailableGameImagesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAvailableImageIdsAsync(CancellationToken.None)).Result).Value);

        // The version advertised for the slug has to be the one the bytes behind it were stored at.
        // Advertising the Xbox row's own timestamp instead puts a number in the URL that the served
        // Steam bytes never had, and the two landing in the same second would then be read as "this
        // URL is current" and cached for a year holding art that was never behind it.
        var advertised = Assert.Contains(slug, (IDictionary<string, long>)listed.Images);
        Assert.Equal(new DateTimeOffset(steamStoredAtUtc).ToUnixTimeSeconds(), advertised);
        Assert.NotEqual(new DateTimeOffset(slugStoredAtUtc).ToUnixTimeSeconds(), advertised);

        // And because it is that row's own version, the URL the frontend builds from it is the one
        // the browser is allowed to keep.
        Assert.IsType<FileContentResult>(
            await controller.GetNameKeyedHeaderImageAsync("xbox", slug, advertised, CancellationToken.None));
        Assert.Equal(
            "public, max-age=31536000, immutable",
            controller.Response.Headers["Cache-Control"].ToString());
    }

    /// <summary>
    /// A curated banner with no row is versioned by the cache generation, and a row's version is the
    /// second its bytes were stored. Both were unix seconds, so they could land on the same number,
    /// and then a slug that gains a row in the second the generation moved keeps the URL a browser
    /// was already told to hold for a year. The sign is what keeps the two apart, so the sign is what
    /// this asserts: the rowless slug's version is negative and no row's version can be.
    /// </summary>
    [Fact]
    public async Task AvailableImages_VersionsARowlessCuratedSlugOutsideTheRangeAnyRowCanOccupyAsync()
    {
        var slug = NameKeyedBannerSource.EmbeddedBannerSlugs().First();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"game-images-embedded-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);

        // A row stored in the very second the generation was last moved, which is the collision this
        // is about. The embedded re-seed writes one with no network call, so it is not a rare timing.
        context.GameImages.Add(new GameImage
        {
            AppId = "730",
            Service = "steam",
            ImageData = ArtBytes,
            ContentType = "image/jpeg",
            FetchedAtUtc = DateTimeOffset.FromUnixTimeSeconds(GameImagesController.CacheGeneration).UtcDateTime
        });
        await context.SaveChangesAsync();

        var controller = CreateController(
            new BlockingServiceProvider(new ManualResetEventSlim(false), new ManualResetEventSlim(true)),
            CreateDefaultProxy<IUnifiedOperationTracker>(),
            conflict: null,
            new RecordingOperationQueue(new QueuedOperationResponse()),
            CreateDefaultProxy<IImageCacheService>(),
            context);

        var listed = Assert.IsType<AvailableGameImagesResponse>(
            Assert.IsType<OkObjectResult>(
                (await controller.GetAvailableImageIdsAsync(CancellationToken.None)).Result).Value);

        var embedded = Assert.Contains(slug, (IDictionary<string, long>)listed.Images);
        var row = Assert.Contains("730", (IDictionary<string, long>)listed.Images);

        Assert.True(embedded < 0, $"the rowless curated slug reported {embedded}, which a row could also report");
        Assert.True(row > 0, $"a stored row reported {row}, which is inside the range reserved for rowless banners");
        Assert.NotEqual(row, embedded);

        // The header route has to agree, or the version it hands out is not the one it checks.
        Assert.IsType<FileContentResult>(
            await controller.GetNameKeyedHeaderImageAsync("blizzard", slug, embedded, CancellationToken.None));
        Assert.Equal(
            "public, max-age=31536000, immutable",
            controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public async Task GetHeaderImage_WhenTheUrlNamesTheStoredVersion_CachesForAYearAndAnswers304Async()
    {
        var controller = CreateHeaderImageController(ArtBytes, StoredAtUtc);

        Assert.IsType<FileContentResult>(
            await controller.GetHeaderImageAsync(570, StoredVersion, CancellationToken.None));

        // A URL naming the version these bytes were stored at can never go out of date, because new
        // artwork is stored at a new second and so lives at a different URL. So the browser can keep
        // it and stop asking: a page full of banners costs no round trips at all on a second load.
        var etag = controller.Response.Headers["ETag"].ToString();
        Assert.Equal("public, max-age=31536000, immutable", controller.Response.Headers["Cache-Control"].ToString());
        Assert.False(string.IsNullOrEmpty(etag));

        controller.Request.Headers["If-None-Match"] = etag;
        var revalidated = Assert.IsType<StatusCodeResult>(
            await controller.GetHeaderImageAsync(570, StoredVersion, CancellationToken.None));
        Assert.Equal(StatusCodes.Status304NotModified, revalidated.StatusCode);
    }

    [Fact]
    public async Task GetHeaderImage_WhenTheUrlNamesAnOlderVersion_ServesCurrentArtWithoutPinningItAsync()
    {
        var controller = CreateHeaderImageController(ArtBytes, StoredAtUtc);

        // A tab left open across an artwork change still holds the old URL. It gets the current art,
        // so the banner keeps showing rather than turning into an empty slot.
        var served = Assert.IsType<FileContentResult>(
            await controller.GetHeaderImageAsync(570, StoredVersion - 3600, CancellationToken.None));
        Assert.Equal(ArtBytes, served.FileContents);

        // What it must not do is let that response be kept. Nothing will ever request this URL again
        // once the tab reloads, so caching it for a year pins artwork that has already been replaced.
        var etag = controller.Response.Headers["ETag"].ToString();
        Assert.Equal("no-cache", controller.Response.Headers["Cache-Control"].ToString());
        Assert.False(string.IsNullOrEmpty(etag));

        // The ETag still turns the revalidation that costs into a bodyless 304.
        controller.Request.Headers["If-None-Match"] = etag;
        var revalidated = Assert.IsType<StatusCodeResult>(
            await controller.GetHeaderImageAsync(570, StoredVersion - 3600, CancellationToken.None));
        Assert.Equal(StatusCodes.Status304NotModified, revalidated.StatusCode);
    }

    [Fact]
    public async Task GetHeaderImage_WithoutAVersionSegment_StillServesArtAsync()
    {
        var controller = CreateHeaderImageController(ArtBytes, StoredAtUtc);

        // The versionless route is anonymous and documented for callers outside this repo, so it
        // keeps working. It cannot claim to be current, so it is not kept either.
        var served = Assert.IsType<FileContentResult>(
            await controller.GetHeaderImageAsync(570, version: null, CancellationToken.None));
        Assert.Equal(ArtBytes, served.FileContents);
        Assert.Equal("no-cache", controller.Response.Headers["Cache-Control"].ToString());
    }

    [Fact]
    public void BannerRoutes_TakeAVersionSegmentThatOnlyAcceptsANumber()
    {
        foreach (var action in new[]
                 {
                     nameof(GameImagesController.GetHeaderImageAsync),
                     nameof(GameImagesController.GetEpicHeaderImageAsync),
                     nameof(GameImagesController.GetNameKeyedHeaderImageAsync)
                 })
        {
            var templates = typeof(GameImagesController)
                .GetMethod(action)!
                .GetCustomAttributes<HttpGetAttribute>()
                .Select(attribute => attribute.Template)
                .ToList();

            // The :long constraint turns a garbage segment into a 404 at routing, so the handler
            // never has to parse it. The versionless template stays beside it.
            Assert.Contains(templates, template => template!.EndsWith("/header/{version:long}", StringComparison.Ordinal));
            Assert.Contains(templates, template => template!.EndsWith("/header", StringComparison.Ordinal));
        }
    }

    private static readonly byte[] ArtBytes = [1, 2, 3, 4];

    private static readonly DateTime StoredAtUtc = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly long StoredVersion = new DateTimeOffset(StoredAtUtc).ToUnixTimeSeconds();

    private static GameImagesController CreateHeaderImageController(byte[] imageBytes, DateTime storedAtUtc)
    {
        var imageCacheService = CreateProxy<IImageCacheService>((method, _) =>
            method.Name == nameof(IImageCacheService.GetImageAsync)
                ? Task.FromResult<(byte[] imageBytes, string contentType, DateTime storedAtUtc)?>(
                    (imageBytes, "image/jpeg", storedAtUtc))
                : DefaultReturn(method.ReturnType));

        return CreateController(
            new BlockingServiceProvider(new ManualResetEventSlim(false), new ManualResetEventSlim(true)),
            CreateDefaultProxy<IUnifiedOperationTracker>(),
            conflict: null,
            new RecordingOperationQueue(new QueuedOperationResponse()),
            imageCacheService);
    }

    [Fact]
    public void PassEnd_AlwaysMovesTheCacheGeneration_RegardlessOfOutcome()
    {
        var source = ReadSource("Infrastructure", "Services", "Images", "GameImageFetchService.cs");

        // The generation bump used to be guarded on at least one phase having actually stored or
        // restored something. A force refresh deletes every row before the pass starts, so a pass
        // that stores nothing (every upstream fetch failed) or is canceled partway used to leave
        // the browser with no banners and no way to notice short of a reload. The bump is
        // unconditional now, so every pass ending - stored something, stored nothing, canceled -
        // still tells clients to look again.
        Assert.DoesNotContain("staleRefreshed > 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain("staleImages.Count > 0", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "missingSteamCount > 0 || missingEpicCount > 0", source, StringComparison.Ordinal);
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }

    private static GameImagesController CreateController(
        IServiceProvider fetchServiceProvider,
        IUnifiedOperationTracker tracker,
        OperationConflictResponse? conflict,
        IOperationQueue queue,
        IImageCacheService? imageCacheService = null,
        AppDbContext? context = null)
    {
        var images = imageCacheService ?? CreateDefaultProxy<IImageCacheService>();
        var fetchService = CreateFetchService(fetchServiceProvider, tracker, images);

        var conflictChecker = CreateProxy<IOperationConflictChecker>((method, args) =>
        {
            if (method.Name != nameof(IOperationConflictChecker.CheckAsync))
            {
                return DefaultReturn(method.ReturnType);
            }

            // The real checker throws on an aborted request while walking the active operations
            // (OperationConflictChecker.cs:49). Tests passing CancellationToken.None never see it.
            ((CancellationToken)args![2]!).ThrowIfCancellationRequested();
            return Task.FromResult(conflict);
        });

        return new GameImagesController(
            NullLogger<GameImagesController>.Instance,
            images,
            fetchService,
            context!,
            conflictChecker,
            queue)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static GameImageFetchService CreateFetchService(
        IServiceProvider fetchServiceProvider,
        IUnifiedOperationTracker tracker,
        IImageCacheService images)
    {
        var stateService = CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetServiceInterval) => null,
            nameof(IStateService.GetServiceRunOnStartup) => null,
            _ => DefaultReturn(method.ReturnType)
        });

        return new GameImageFetchService(
            fetchServiceProvider,
            NullLogger<GameImageFetchService>.Instance,
            new ConfigurationBuilder().Build(),
            stateService,
            CreateDefaultProxy<ISignalRNotificationService>(),
            images,
            tracker);
    }

    private static UnifiedOperationTracker NewTracker() => new(
        new ProcessManager(NullLogger<ProcessManager>.Instance),
        NullLogger<UnifiedOperationTracker>.Instance);

    private static async Task WaitForPassesToFinishAsync(UnifiedOperationTracker tracker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (tracker.GetActiveOperations(OperationType.GameImageFetch).Any())
        {
            Assert.True(DateTime.UtcNow < deadline, "a fetch pass never finished");
            await Task.Delay(10);
        }
    }

    /// <summary>
    /// Stands in for the fetch service's root provider and counts the passes that reach the point of
    /// opening their scope. The first one is held there until the test lets it go, so "a fetch pass
    /// is running right now" is an observable state rather than a timing guess, and a later pass
    /// arriving is what proves the refused request was not simply dropped.
    /// </summary>
    private sealed class CountingServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly ManualResetEventSlim _firstPassStarted = new(false);
        private readonly ManualResetEventSlim _releaseFirstPass = new(false);
        private readonly ManualResetEventSlim _secondPassStarted = new(false);
        private int _passes;

        public IServiceProvider ServiceProvider => this;

        public bool WaitForFirstPass() => _firstPassStarted.Wait(TimeSpan.FromSeconds(5));

        public void ReleaseFirstPass() => _releaseFirstPass.Set();

        public bool WaitForSecondPass() => _secondPassStarted.Wait(TimeSpan.FromSeconds(5));

        public IServiceScope CreateScope()
        {
            switch (Interlocked.Increment(ref _passes))
            {
                case 1:
                    _firstPassStarted.Set();
                    _releaseFirstPass.Wait(TimeSpan.FromSeconds(5));
                    break;
                case 2:
                    _secondPassStarted.Set();
                    break;
            }

            return this;
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType)
            => serviceType == typeof(IServiceScopeFactory) ? this : null;
    }

    /// <summary>
    /// Stands in for the fetch service's root provider. Handing out the scope factory is free, but
    /// the first service the pass resolves inside its scope is held until the test releases it, so
    /// "the pass is still running" is an observable state rather than a timing guess.
    /// </summary>
    private sealed class BlockingServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly ManualResetEventSlim _passStarted;
        private readonly ManualResetEventSlim _releasePass;

        public BlockingServiceProvider(ManualResetEventSlim passStarted, ManualResetEventSlim releasePass)
        {
            _passStarted = passStarted;
            _releasePass = releasePass;
        }

        public IServiceProvider ServiceProvider => this;

        /// <summary>The first service the pass asks for inside its scope, so a test can name it.</summary>
        public Type? FirstResolved { get; private set; }

        public IServiceScope CreateScope() => this;

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            FirstResolved ??= serviceType;
            _passStarted.Set();
            _releasePass.Wait(TimeSpan.FromSeconds(5));
            return null;
        }
    }

    private static T CreateDefaultProxy<T>() where T : class
        => CreateProxy<T>((method, _) => DefaultReturn(method.ReturnType));

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }

    private sealed class RecordingOperationQueue(QueuedOperationResponse response) : IOperationQueue
    {
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            bool reportRefusal = false)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            return Task.FromResult(response);
        }

        public string? GetWaitingBlockerName(Guid waitingOperationId) => null;
    }
}
