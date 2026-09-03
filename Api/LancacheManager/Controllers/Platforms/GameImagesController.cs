using System.Security.Cryptography;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for game image serving.
/// Serves images from the local DB/cache only - no CDN fallback.
/// Images are pre-fetched by the background image download service.
/// </summary>
[ApiController]
[Route("api/game-images")]
public class GameImagesController : ControllerBase
{
    private readonly ILogger<GameImagesController> _logger;
    private readonly IImageCacheService _imageCacheService;
    private readonly GameImageFetchService _gameImageFetchService;
    private readonly AppDbContext _context;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;

    // Bumped on cache clear or new image fetch so the frontend can build cache-busted image URLs
    private static long _cacheGeneration = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>Gets the current image cache generation number.</summary>
    public static long CacheGeneration => Interlocked.Read(ref _cacheGeneration);

    /// <summary>Increments the cache generation to a new timestamp, invalidating cached image URLs.</summary>
    public static void IncrementCacheGeneration() =>
        Interlocked.Exchange(ref _cacheGeneration, DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>
    /// The version reported for a curated embedded banner, which has no row and so no stored-at second.
    /// </summary>
    /// <remarks>
    /// Negated so it can never equal a row's version. Both are otherwise unix seconds in one number
    /// space, and a rowless slug can gain a row inside the same second the generation moved: the
    /// embedded re-seed copies from the assembly with no network call
    /// (GameImageFetchService.cs:1126-1141), and a follow-up pass begins in the second the previous
    /// one bumped the generation. Under one number space that URL would keep its old value, and a
    /// browser that had already been told the response was immutable would hold superseded bytes for
    /// a year with no reload able to clear it. Disjoint spaces make the collision impossible rather
    /// than unlikely.
    /// </remarks>
    private static long EmbeddedBannerVersion => -CacheGeneration;

    public GameImagesController(
        ILogger<GameImagesController> logger,
        IImageCacheService imageCacheService,
        GameImageFetchService gameImageFetchService,
        AppDbContext context,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue)
    {
        _logger = logger;
        _imageCacheService = imageCacheService;
        _gameImageFetchService = gameImageFetchService;
        _context = context;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
    }

    /// <summary>
    /// Returns the cached Steam game header image.
    /// </summary>
    /// <remarks>
    /// Returns 404 if no image is stored in the database for this app. The optional trailing version
    /// is the one /available reported for this app; see ImageResponse for what it changes.
    /// </remarks>
    [HttpGet("{appId}/header")]
    [HttpGet("{appId}/header/{version:long}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHeaderImageAsync(
        int appId,
        long? version = null,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType, storedAtUtc) = await _imageCacheService.GetImageAsync(
            appId.ToString(), "steam", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for app {appId}" });
        }

        return ImageResponse(
            imageData, contentType ?? "image/jpeg", appId.ToString(), ImageVersion(storedAtUtc), version);
    }

    /// <summary>
    /// Returns the cached Epic game header image.
    /// </summary>
    /// <remarks>
    /// Returns 404 if no image is stored in the database for this Epic app. The optional trailing
    /// version is the one /available reported for this app; see ImageResponse for what it changes.
    /// </remarks>
    [HttpGet("epic/{epicAppId}/header")]
    [HttpGet("epic/{epicAppId}/header/{version:long}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEpicHeaderImageAsync(
        string epicAppId,
        long? version = null,
        CancellationToken cancellationToken = default)
    {
        var (imageData, contentType, storedAtUtc) = await _imageCacheService.GetImageAsync(
            epicAppId, "epicgames", cancellationToken) ?? default;

        if (imageData == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Game image not available for Epic app {epicAppId}" });
        }

        return ImageResponse(
            imageData, contentType ?? "image/jpeg", $"epic-{epicAppId}", ImageVersion(storedAtUtc), version);
    }

    /// <summary>
    /// Returns the cached banner image for a name-keyed service.
    /// </summary>
    /// <remarks>
    /// Applies to services (Blizzard/Riot) whose games are identified only by GameName. The slug
    /// is the normalized GameName produced by NameKeyedBannerSource.Slug, and the service is the
    /// canonical "blizzard"/"riot" key. Returns 404 if no image is stored for this (slug, service).
    /// The optional trailing version is the one /available reported for this slug; see ImageResponse
    /// for what it changes.
    /// </remarks>
    [HttpGet("name/{service}/{slug}/header")]
    [HttpGet("name/{service}/{slug}/header/{version:long}")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> GetNameKeyedHeaderImageAsync(
        string service,
        string slug,
        long? version = null,
        CancellationToken cancellationToken = default)
    {
        var canonicalService = NameKeyedBannerSource.NormalizeService(service);
        if (canonicalService == null)
        {
            return NotFound(new GameImageErrorResponse { Error = $"Unsupported name-keyed service '{service}'" });
        }

        // Steam-first: if this name-keyed game maps to a Steam appId AND a Steam header has been
        // fetched for it, serve Steam's header.jpg. Falls through to the curated embedded banner
        // (below) until the Steam fetch lands, or permanently if it never does.
        var steamAppId = NameKeyedSteamAppIds.TryGetSteamAppIdBySlug(canonicalService, slug);
        if (steamAppId != null)
        {
            var (steamImageData, steamContentType, steamStoredAtUtc) = await _imageCacheService.GetImageAsync(
                steamAppId.Value.ToString(), "steam", cancellationToken) ?? default;

            if (steamImageData != null)
            {
                return ImageResponse(
                    steamImageData, steamContentType ?? "image/jpeg", steamAppId.Value.ToString(),
                    ImageVersion(steamStoredAtUtc), version);
            }
        }

        var (imageData, contentType, storedAtUtc) = await _imageCacheService.GetImageAsync(
            slug, canonicalService, cancellationToken) ?? default;

        if (imageData != null)
        {
            return ImageResponse(
                imageData, contentType ?? "image/jpeg", $"{canonicalService}-{slug}",
                ImageVersion(storedAtUtc), version);
        }

        // Instant path: curated embedded banners (Blizzard/Riot) live in the assembly, so serve them
        // directly even before any GameImage row has been fetched/stored. This makes a curated banner
        // appear the moment its game card renders - no fetch, detection scan, or 30-min wait.
        if (NameKeyedBannerSource.TryGetEmbeddedBytesForSlug(canonicalService, slug, out var embeddedBytes, out var embeddedContentType))
        {
            // No row, so no stored-at time. These bytes live in the assembly and change only on a
            // deploy, which restarts the process and so moves the cache generation - the same number
            // /available reports for them, so a current request still matches.
            return ImageResponse(
                embeddedBytes, embeddedContentType, $"{canonicalService}-{slug}", EmbeddedBannerVersion, version);
        }

        return NotFound(new GameImageErrorResponse { Error = $"Game image not available for {canonicalService} '{slug}'" });
    }

    /// <summary>
    /// Returns the current image cache generation number.
    /// </summary>
    /// <remarks>
    /// The frontend uses this to build cache-busted image URLs on page load.
    /// </remarks>
    [HttpGet("cache-version")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(GameImageCacheVersionResponse), StatusCodes.Status200OK)]
    public ActionResult<GameImageCacheVersionResponse> GetCacheVersion() => Ok(new GameImageCacheVersionResponse { Version = CacheGeneration });

    /// <summary>
    /// Returns the app IDs that have cached game images, each with the version its bytes were stored at.
    /// </summary>
    /// <remarks>
    /// The frontend uses this to skip rendering image components for apps without images, and puts
    /// each version in that image's URL so one game's new artwork re-fetches one banner.
    /// </remarks>
    [HttpGet("available")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AvailableGameImagesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailableGameImagesResponse>> GetAvailableImageIdsAsync(CancellationToken cancellationToken = default)
    {
        // The service and two timestamp columns beside the id keep this a narrow column read:
        // ImageData is never loaded.
        var stored = await _context.GameImages
            .AsNoTracking()
            .Select(gi => new { gi.AppId, gi.Service, gi.UpdatedAtUtc, gi.FetchedAtUtc })
            .ToListAsync(cancellationToken);

        var steamVersions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var image in stored)
        {
            if (image.Service == "steam")
            {
                steamVersions[image.AppId] = ImageVersion(image.UpdatedAtUtc ?? image.FetchedAtUtc);
            }
        }

        var images = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var image in stored)
        {
            images[image.AppId] = SteamFirstVersion(image.Service, image.AppId, steamVersions)
                ?? ImageVersion(image.UpdatedAtUtc ?? image.FetchedAtUtc);
        }

        // Also advertise curated embedded name-keyed banners (Blizzard/Riot). Their JPEG bytes live
        // in the assembly and are served on-demand by GetNameKeyedHeaderImageAsync, so they are
        // "available" the instant a curated game's card renders - no fetched GameImage row required.
        // A fetched row for the same slug wins, because that is the one the header route serves.
        // This response is keyed by slug alone, so the service has to be recovered before asking
        // whether the slug is one of the Steam-first ones.
        foreach (var slug in NameKeyedBannerSource.EmbeddedBannerSlugs())
        {
            if (images.ContainsKey(slug))
            {
                continue;
            }

            images[slug] = _nameKeyedServices
                .Select(service => SteamFirstVersion(service, slug, steamVersions))
                .FirstOrDefault(version => version != null) ?? EmbeddedBannerVersion;
        }

        // Also advertise a curated slug the serve route answers Steam-first. Those games have no row
        // of their own and no embedded banner, so neither loop above reaches them, yet the route
        // returns Steam's header for them. The row deciding whether to draw a banner asks by slug, so
        // a slug missing here reads as "no artwork" for a game whose banner the server would serve
        // perfectly well - which is what left Minecraft Dungeons blank while its Steam id was
        // advertised all along. Only slugs whose Steam header is actually stored qualify. [50]
        foreach (var (service, slug, steamAppId) in NameKeyedSteamAppIds.SteamBackedSlugs())
        {
            if (images.ContainsKey(slug))
            {
                continue;
            }

            if (steamVersions.TryGetValue(steamAppId.ToString(), out var steamVersion))
            {
                images[slug] = steamVersion;
            }
        }

        return Ok(new AvailableGameImagesResponse { Images = images });
    }

    /// <summary>
    /// Clears the game image cache and starts the re-fetch that refills it.
    /// </summary>
    /// <remarks>
    /// Returns once the cache is cleared and the re-fetch has started, instead of holding the request
    /// open until every banner has been downloaded again. Refreshing Epic's image URLs is part of
    /// that pass rather than of this request, because it calls out to Epic's catalog. The pass
    /// reports its own completion through the GameImagesUpdated event, which carries the generation
    /// the pass finished on. A conflicting operation is never rejected outright.
    ///
    /// How the re-fetch survives depends on what is in the way, because the rows are already gone by
    /// the time either path runs. A different operation, such as a database reset or a cache clear,
    /// is parked on the wait queue and starts once that conflict clears. An image fetch pass that is
    /// already running is NOT parked: the queue treats a second request under the same name as an
    /// idempotent accept and answers "already running" without a waiter, which would leave every
    /// banner deleted and nothing to refill them. StartFetchInBackgroundAsync announces the request before
    /// it tries for the execution lock and the running pass starts one more on its way out, so that
    /// follow-up reads the emptied table and refetches everything.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("cache")]
    [ProducesResponseType(typeof(ImageCacheClearResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ClearImageCacheAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== ClearImageCache START ===");

        await _imageCacheService.ClearCacheAsync();
        _logger.LogInformation("Image cache cleared");

        IncrementCacheGeneration();

        // Wait-queue model: a conflict with a DIFFERENT operation is parked (visible waiting card),
        // never 409'd. The delegate captures only the singleton fetch service, so it stays valid at
        // promotion time when this request is long gone.
        Task<Guid?> StartImageFetchAsync() =>
            _gameImageFetchService.StartFetchInBackgroundAsync(refreshEpicImageUrls: true, RunTrigger.Manual);

        // Every row is marked for re-fetch from here on, so no path out of this method may leave
        // without a re-fetch either running or on its way. Clearing takes no cancellation token, so
        // it completes even when the caller has already gone away - somebody pressing this and
        // closing the tab. What runs next does not: the conflict check throws on an aborted request
        // as it walks the active operations, and the queue throws on its gate. Uncovered, that
        // leaves every banner stale until the next scheduled pass.
        OperationConflictResponse? conflict;
        try
        {
            conflict = await _conflictChecker.CheckAsync(
                OperationType.GameImageFetch,
                ConflictScope.Bulk(),
                cancellationToken);

            // A conflict with another image fetch is the one the queue cannot hold. It reads a
            // same-name duplicate as an idempotent accept and answers "already running" without
            // parking a waiter (OperationQueueService.cs:139-152), so the re-fetch would be dropped.
            // The fetch service covers this case itself: see the null branch below.
            if (conflict != null && conflict.ActiveOperationType != nameof(OperationType.GameImageFetch))
            {
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.GameImageFetch, ConflictScope.Bulk(), "Game Image Fetch",
                    StartImageFetchAsync, cancellationToken));
            }
        }
        catch
        {
            // StartFetchInBackgroundAsync announces the request before it reaches anything that can fail,
            // so this arms the refill whether or not it goes on to take the lock. The original
            // failure is still what the caller sees.
            await _gameImageFetchService.StartFetchInBackgroundAsync(refreshEpicImageUrls: true, RunTrigger.Manual);
            throw;
        }

        var operationId = await StartImageFetchAsync();

        if (operationId == null)
        {
            // A pass already holds the execution lock. StartFetchInBackgroundAsync announced this request
            // before trying to take it, and the running pass starts one more on its way out, so the
            // work is armed rather than lost. That follow-up reads the rows this request just
            // backdated, which the running pass had already read past.
            _logger.LogInformation(
                "=== ClearImageCache END === re-fetch armed behind the running pass");

            return Accepted(new ImageCacheClearResponse
            {
                Message = "Image cache cleared and re-fetch armed behind the running pass",
                CacheGeneration = CacheGeneration
            });
        }

        _logger.LogInformation(
            "=== ClearImageCache END === re-fetch operation: {OperationId}", operationId);

        return Accepted(new ImageCacheClearResponse
        {
            Message = "Image cache cleared and re-fetch started",
            CacheGeneration = CacheGeneration
        });
    }

    /// <summary>
    /// The version of the row GetNameKeyedHeaderImageAsync will actually serve for this slug, or null
    /// when that route serves the slug's own row or its embedded bytes instead.
    /// </summary>
    /// <remarks>
    /// That route is Steam-first: a curated game that also exists on Steam is served Steam's
    /// header.jpg once one has been fetched. Advertising the slug's own version there would put a
    /// number in the URL that the served bytes never had, and two rows' timestamps landing in the
    /// same second would then be read as "this URL is current" and cached for a year holding art
    /// that was never behind it. Reporting the served row's version keeps one clock per URL, so the
    /// number can only match when it really is that row's, and it moves the moment those bytes do.
    /// </remarks>
    private static long? SteamFirstVersion(
        string? service,
        string slug,
        Dictionary<string, long> steamVersions)
    {
        var steamAppId = NameKeyedSteamAppIds.TryGetSteamAppIdBySlug(service, slug);

        return steamAppId != null && steamVersions.TryGetValue(steamAppId.Value.ToString(), out var version)
            ? version
            : null;
    }

    /// <summary>
    /// The services a name-keyed slug can belong to, for the curated embedded banners that have no
    /// stored row to read a service from.
    /// </summary>
    private static readonly string[] _nameKeyedServices =
    [
        NameKeyedBannerSource.BlizzardService,
        NameKeyedBannerSource.RiotService,
        NameKeyedBannerSource.XboxService
    ];

    /// <summary>
    /// The version of an image's bytes: the second they were stored. It moves only when the bytes are
    /// replaced, so an unchanged banner keeps its URL and one game's new artwork moves one URL.
    /// </summary>
    private static long ImageVersion(DateTime storedAtUtc) =>
        new DateTimeOffset(DateTime.SpecifyKind(storedAtUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>
    /// Returns an image response with ETag-based conditional request support, cached for a year when
    /// the caller asked for the version these bytes were actually stored at.
    /// </summary>
    /// <remarks>
    /// A page holding many banners is the case that matters here. A URL that names the current version
    /// can never go out of date, because new artwork is a new version and so a different URL, so the
    /// browser can keep it without ever asking again - a hundred banners on a full retro page cost no
    /// round trips at all on a second load.
    ///
    /// A stale tab asks for a version that is no longer current, and it still gets the current bytes so
    /// the banner keeps showing rather than vanishing. That response must not be kept: caching it for a
    /// year under a URL that will never be requested again pins artwork that has already been replaced.
    /// It carries no-cache, and the ETag then makes the revalidation a bodyless 304. A request with no
    /// version segment at all is in the same position and is treated the same way.
    /// </remarks>
    private IActionResult ImageResponse(
        byte[] imageBytes,
        string contentType,
        string etagPrefix,
        long currentVersion,
        long? requestedVersion)
    {
        var hash = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant();
        var etag = $"\"{etagPrefix}-{hash}\"";

        Response.Headers["Cache-Control"] = requestedVersion == currentVersion
            ? "public, max-age=31536000, immutable"
            : "no-cache";
        Response.Headers["ETag"] = etag;

        var ifNoneMatch = Request.Headers["If-None-Match"].ToString();
        if (!string.IsNullOrEmpty(ifNoneMatch) && (ifNoneMatch.Contains(etag) || ifNoneMatch.Trim() == "*"))
        {
            return StatusCode(304);
        }

        return File(imageBytes, contentType);
    }
}
