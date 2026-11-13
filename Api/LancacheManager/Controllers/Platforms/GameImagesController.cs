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
    private readonly IOperationWaitlist _operationQueue;

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
    /// The second this process started, negated. The shipped files are part of the assembly, so they
    /// can only change with a deploy, and a deploy is a new process with a later start second: the
    /// number moves exactly when the bytes can, and stays put across image passes, so an unchanged
    /// banner is not downloaded again every time a pass ends.
    ///
    /// Negated so it can never equal a row's version, which is a positive unix second. A version that
    /// two different byte sets could share would let a browser that was told the response was
    /// immutable keep superseded bytes for a year with no reload able to clear it. Disjoint spaces
    /// make that collision impossible rather than unlikely.
    /// </remarks>
    private static long EmbeddedBannerVersion { get; } = -DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public GameImagesController(
        ILogger<GameImagesController> logger,
        IImageCacheService imageCacheService,
        GameImageFetchService gameImageFetchService,
        AppDbContext context,
        IOperationWaitlist operationQueue)
    {
        _logger = logger;
        _imageCacheService = imageCacheService;
        _gameImageFetchService = gameImageFetchService;
        _context = context;
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
        // (below) until the Steam fetch lands, or permanently if it never does. A game listed in the
        // Steam map shows Steam's art even when a banner for it ships too.
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

        // Curated embedded banners (Blizzard/Riot) live in the assembly, so they are served straight
        // from it, before any stored row for the slug. A stored row is only a copy of the file that
        // shipped when it was written, and after a deploy that replaces the file it would keep
        // answering with the old art. Serving from the assembly also makes a curated banner appear
        // the moment its game card renders - no fetch, detection scan, or 30-min wait.
        if (NameKeyedBannerSource.TryGetEmbeddedBytesForSlug(canonicalService, slug, out var embeddedBytes, out var embeddedContentType))
        {
            return ImageResponse(
                embeddedBytes, embeddedContentType, $"{canonicalService}-{slug}", EmbeddedBannerVersion, version);
        }

        var (imageData, contentType, storedAtUtc) = await _imageCacheService.GetImageAsync(
            slug, canonicalService, cancellationToken) ?? default;

        if (imageData != null)
        {
            return ImageResponse(
                imageData, contentType ?? "image/jpeg", $"{canonicalService}-{slug}",
                ImageVersion(storedAtUtc), version);
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
            // The header route never serves a stored row for a slug whose banner ships in the
            // assembly, so that row's stored-at second is not the version of anything it serves.
            // The curated loop below versions the slug instead.
            if (NameKeyedBannerSource.HasEmbeddedBanner(image.Service, image.AppId))
            {
                continue;
            }

            images[image.AppId] = SteamFirstVersion(image.Service, image.AppId, steamVersions)
                ?? ImageVersion(image.UpdatedAtUtc ?? image.FetchedAtUtc);
        }

        // Also advertise curated embedded name-keyed banners (Blizzard/Riot). Their JPEG bytes live
        // in the assembly and are served on-demand by GetNameKeyedHeaderImageAsync, so they are
        // "available" the instant a curated game's card renders - no fetched GameImage row required.
        // A row another service stored under the same slug keeps its entry, because the header route
        // serves that row for that service; the curated slug's own rows were skipped above. This
        // response is keyed by slug alone, so the service has to be recovered before asking whether
        // the slug is one of the Steam-first ones.
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
        // advertised all along. Only slugs whose Steam header is actually stored qualify.
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
    /// the time either path runs. A conflicting operation, whether a database reset, a cache clear or an
    /// image fetch pass already running, is parked on the wait queue and starts once that conflict
    /// clears. A pass that holds the execution lock but has not registered its operation yet conflicts
    /// with nothing, so StartFetchInBackgroundAsync announces the request before it tries for the lock
    /// and the running pass starts one more on its way out; that follow-up reads the emptied table and
    /// refetches everything from the stored Epic URLs.
    /// </remarks>
    [Authorize(Policy = "AccountHolder")]
    [HttpDelete("cache")]
    [ProducesResponseType(typeof(ImageCacheClearResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(OperationStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ClearImageCacheAsync()
    {
        _logger.LogInformation("=== ClearImageCache START ===");

        await _imageCacheService.ClearCacheAsync();
        _logger.LogInformation("Image cache cleared");

        IncrementCacheGeneration();

        // Wait-queue model: every conflict is parked (a waiting card),
        // never 409'd. The delegate captures only the singleton fetch service, so it stays valid at
        // promotion time when this request is long gone.
        Task<Guid?> StartImageFetchAsync() =>
            _gameImageFetchService.StartFetchInBackgroundAsync(refreshEpicImageUrls: true, RunTrigger.Manual);

        // Every row is marked for re-fetch from here on, so no path out of this method may leave
        // without a re-fetch either running or on its way. Neither the clear nor the queue calls
        // below take the request's token, so both finish even when the caller has already gone
        // away - somebody pressing this and closing the tab - and the re-fetch still waits behind
        // any job the queue makes it wait for.
        Guid? operationId = null;

        // The queue checks and starts under one lock, so a clear and this re-fetch sent at the same
        // moment cannot both start. A start that finds a pass holding the execution lock answers null
        // and is armed behind it, so the lock is held only until the run registers.
        var conflict = await _operationQueue.StartUnlessBusyAsync(
            OperationType.GameImageFetch, ConflictScope.Bulk(),
            async () => operationId = await StartImageFetchAsync(),
            CancellationToken.None,
            new GameImageFetchRequestKey(RefreshEpicImageUrls: true));

        // Every conflict parks the re-fetch on the wait queue, a running image fetch pass included: the
        // queue never answers an image fetch with a pass already running, because that pass read its
        // work list before the rows were deleted (OperationWaitlistService.GetDuplicateId). The re-fetch
        // runs a pass of its own, with the Epic URL refresh, once the conflict clears.
        if (conflict != null)
        {
            // The waiting click is drawn in the image fetch schedule's mode, as the run it starts is.
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.GameImageFetch, ConflictScope.Bulk(), "Game Image Fetch",
                StartImageFetchAsync, CancellationToken.None,
                // A waiting scheduled pass skips the Epic URL refresh this re-fetch needs, so it never joins one.
                new GameImageFetchRequestKey(RefreshEpicImageUrls: true),
                notice: new RunNotice(_gameImageFetchService.EffectiveNotificationMode, RunTrigger.Manual)));
        }

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
