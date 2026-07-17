using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Background service that periodically fetches and stores game banner images
/// for Steam and Epic games in the database.
/// Runs on startup (after setup completes) and every 30 minutes.
/// Image fetching is deferred until after all game detection, mapping, and DB saves are complete.
/// </summary>
public class GameImageFetchService : ScopedScheduledBackgroundService
{
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IImageCacheService _imageCacheService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private static readonly SemaphoreSlim _executionLock = new(1, 1);

    // Max concurrent HTTP requests for image fetching
    private static readonly SemaphoreSlim _httpThrottle = new(5, 5);

    private const string StageBase = "signalr.scheduledRun.gameImageFetch";
    private static readonly ScheduledRunEventNames _eventNames = new(
        SignalREvents.GameImageFetchStarted,
        SignalREvents.GameImageFetchProgress,
        SignalREvents.GameImageFetchComplete);

    protected override string ServiceName => "GameImageFetch";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(30);
    public override bool DefaultRunOnStartup => false;
    protected override TimeSpan StartupDelay => TimeSpan.Zero;
    protected override bool SupportsNotifications => true;

    // Routine background chore: scheduled runs stay quiet by default; manually triggered runs
    // still notify.
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    public override string ServiceKey => "gameImageFetch";

    public GameImageFetchService(
        IServiceProvider serviceProvider,
        ILogger<GameImageFetchService> logger,
        IConfiguration configuration,
        IStateService stateService,
        ISignalRNotificationService notifications,
        IImageCacheService imageCacheService,
        IUnifiedOperationTracker operationTracker)
        : base(serviceProvider, logger, configuration)
    {
        _stateService = stateService;
        _notifications = notifications;
        _imageCacheService = imageCacheService;
        _operationTracker = operationTracker;

        LoadStateOverrides(stateService);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        // Wait for setup to complete before running scheduled image fetches.
        // Image fetching must only run AFTER all game detection, mapping, and DB saves complete.
        await _stateService.WaitForSetupCompletedAsync(stoppingToken);

        // Run the normal scheduled fetch path once at startup so "run on startup"
        // performs real work instead of only waiting for setup to finish.
        await base.ExecuteWorkAsync(stoppingToken);
    }

    /// <summary>
    /// Public trigger so other services can request an immediate image fetch
    /// after ALL game detection, mapping, and DB saves are complete. This is a programmatic trigger
    /// (not a scheduled run), so it does not surface a Schedules progress card.
    /// </summary>
    public async Task FetchImagesNowAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("[GameImageFetch] Triggered by external service");
        using var scope = _serviceProvider.CreateScope();
        await RunFetchAsync(scope.ServiceProvider, reporter: null, ct);
    }

    protected override async Task ExecuteWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken)
    {
        // Scheduled / startup / Run Now path: surface a progress card via a run reporter. The reporter
        // only starts once real fetch work is confirmed (inside FetchImagesAsync), so a run with no
        // downloads yet never shows a card.
        var show = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.GameImageFetch,
            _eventNames,
            $"{StageBase}.complete",
            show,
            stoppingToken);

        await RunFetchAsync(scopedServices, reporter, stoppingToken);
    }

    private async Task RunFetchAsync(
        IServiceProvider scopedServices,
        ScheduledRunReporter? reporter,
        CancellationToken stoppingToken)
    {
        // Prevent concurrent execution - FetchImagesNowAsync and scheduled runs can overlap. A run that
        // loses this race did no work, so it returns before starting the reporter (no card).
        if (!await _executionLock.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("[GameImageFetch] Skipping - another fetch is already running");
            return;
        }

        try
        {
            await FetchImagesAsync(scopedServices, reporter, stoppingToken);
        }
        finally
        {
            _executionLock.Release();
        }
    }

    private async Task FetchImagesAsync(
        IServiceProvider scopedServices,
        ScheduledRunReporter? reporter,
        CancellationToken stoppingToken)
    {
        var db = scopedServices.GetRequiredService<AppDbContext>();
        var httpClientFactory = scopedServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("SteamImages");

        // 1. STEAM: Get all unique GameAppIds that don't have a GameImage yet
        var totalDownloads = await db.Downloads.CountAsync(stoppingToken);
        if (totalDownloads == 0)
        {
            _logger.LogInformation("[GameImageFetch] Downloads table is empty - log processing hasn't completed yet, will retry next cycle");
            return;
        }

        // There is work to do (downloads exist): start the run now so the "no downloads yet" retry never
        // surfaces a card. Progress is real and monotonic across four equal-weight bands (Steam 0-25,
        // Epic 25-50, name-keyed 50-75, stale refresh 75-100), reported per batch within each band.
        if (reporter != null)
        {
            await reporter.StartAsync($"{StageBase}.starting");
        }

        async Task ReportPhaseAsync(double percent, int processed, int total)
        {
            if (reporter == null)
            {
                return;
            }

            await reporter.ReportAsync(percent, $"{StageBase}.running", new Dictionary<string, object?>
            {
                ["processed"] = processed,
                ["total"] = total
            });
        }

        var steamAppIds = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId != null && d.GameAppId != 0 && !string.IsNullOrEmpty(d.GameName))
            .Select(d => d.GameAppId!.Value)
            .Distinct()
            .ToListAsync(stoppingToken);

        // Name-keyed (Blizzard/Riot) downloads have GameAppId == null but some of those games ALSO
        // exist on Steam. Resolve those to a Steam appId up front so they ride the SAME Steam fetch
        // path below ("Steam-first"): the row lands under Service="steam", AppId=<steamAppId> and
        // reuses all the Steam CDN/store URL logic. Pass 3 then SKIPS the curated embedded banner
        // for these games (only unmapped name-keyed games fall back to the curated path).
        var nameKeyedDownloads = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId == null
                && !string.IsNullOrEmpty(d.GameName)
                && (d.Service == "blizzard" || d.Service == "battle.net" || d.Service == "battlenet"
                    || d.Service == "riot" || d.Service == "riotgames"
                    || d.Service == "xbox" || d.Service == "xboxlive" || d.Service == "microsoft"))
            .Select(d => new { d.Service, d.GameName })
            .Distinct()
            .ToListAsync(stoppingToken);

        // (canonical service, slug) of name-keyed games that mapped to a Steam appId - used below to
        // skip the curated embedded fetch for exactly those games.
        var steamMappedNameKeyedSlugs = new HashSet<(string Service, string Slug)>();

        foreach (var t in nameKeyedDownloads)
        {
            var steamAppId = NameKeyedSteamAppIds.TryGetSteamAppId(t.Service, t.GameName);
            if (steamAppId == null) continue;

            steamAppIds.Add(steamAppId.Value);
            var canonicalService = NameKeyedBannerSource.NormalizeService(t.Service);
            if (canonicalService != null)
            {
                steamMappedNameKeyedSlugs.Add((canonicalService, NameKeyedBannerSource.Slug(t.GameName!)));
            }
        }

        var existingSteamIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == "steam")
            .Select(g => g.AppId)
            .ToListAsync(stoppingToken);

        var missingSteamIds = steamAppIds
            .Distinct()
            .Select(id => id.ToString())
            .Except(existingSteamIds)
            .ToList();

        if (missingSteamIds.Count > 0)
        {
            // Pre-load PICS URLs for all missing Steam apps in a single batch query (eliminates N+1)
            var missingAppIdLongs = missingSteamIds
                .Select(id => long.TryParse(id, out var v) ? v : (long?)null)
                .Where(v => v.HasValue)
                .Select(v => v!.Value)
                .ToList();

            var picsUrlMap = await DownloadGameImageUrlQueries.GetLatestUrlsForSteamAppsAsync(
                db, missingAppIdLongs, stoppingToken);

            // Pre-load SteamDepotMappings for parent app lookup (eliminates N+1 in FindParentAppIdAsync)
            // Candidate depot IDs include appId, appId+1, appId-1 for each missing app
            var candidateDepotIds = missingAppIdLongs
                .SelectMany(id => new[]
                {
                    id,
                    id + 1 <= uint.MaxValue ? id + 1 : id,
                    id - 1 > 0 ? id - 1 : id
                })
                .Distinct()
                .ToList();

            var depotOwnerMap = await db.SteamDepotMappings
                .AsNoTracking()
                .Where(m => candidateDepotIds.Contains(m.DepotId) && m.IsOwner)
                .Select(m => new { m.DepotId, m.AppId })
                .ToListAsync(stoppingToken);

            var depotOwnerLookup = depotOwnerMap
                .GroupBy(m => m.DepotId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.AppId).ToList());

            // Pre-load download depot IDs per app (for Strategy 2 fallback)
            var downloadDepotMap = await db.Downloads
                .AsNoTracking()
                .Where(d => d.GameAppId != null && missingAppIdLongs.Contains(d.GameAppId.Value) && d.DepotId.HasValue)
                .Select(d => new { AppId = d.GameAppId!.Value, DepotId = d.DepotId!.Value })
                .Distinct()
                .ToListAsync(stoppingToken);

            var downloadDepotLookup = downloadDepotMap
                .GroupBy(x => x.AppId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.DepotId).ToList());

            var steamDone = 0;
            foreach (var batch in missingSteamIds.Chunk(50))
            {
                if (stoppingToken.IsCancellationRequested) return;

                var tasks = batch.Select(appId =>
                    FetchSteamImageAsync(db, client, appId, picsUrlMap, depotOwnerLookup, downloadDepotLookup, stoppingToken));

                await Task.WhenAll(tasks);
                await db.SaveChangesAsync(stoppingToken);
                db.ChangeTracker.Clear();

                steamDone += batch.Length;
                await ReportPhaseAsync(steamDone / (double)missingSteamIds.Count * 25, steamDone, missingSteamIds.Count);
            }
        }

        // Steam phase done - advance the band even when there was nothing to fetch.
        await ReportPhaseAsync(25, missingSteamIds.Count, missingSteamIds.Count);

        // 2. EPIC: Get all EpicGameMappings with ImageUrl that don't have a GameImage yet
        var epicMappings = await db.EpicGameMappings
            .AsNoTracking()
            .Where(m => m.ImageUrl != null)
            .ToListAsync(stoppingToken);

        var existingEpicIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == "epicgames")
            .Select(g => g.AppId)
            .ToListAsync(stoppingToken);

        var missingEpicMappings = epicMappings
            .Where(m => !existingEpicIds.Contains(m.AppId))
            .ToList();

        var epicDone = 0;
        foreach (var batch in missingEpicMappings.Chunk(50))
        {
            if (stoppingToken.IsCancellationRequested) return;

            var tasks = batch.Select(mapping =>
                FetchEpicImageAsync(db, client, mapping, stoppingToken));

            await Task.WhenAll(tasks);
            await db.SaveChangesAsync(stoppingToken);
            db.ChangeTracker.Clear();

            epicDone += batch.Length;
            await ReportPhaseAsync(25 + epicDone / (double)missingEpicMappings.Count * 25, epicDone, missingEpicMappings.Count);
        }

        // Epic phase done - advance the band even when there was nothing to fetch.
        await ReportPhaseAsync(50, missingEpicMappings.Count, missingEpicMappings.Count);

        // Backfill DisplayCatalog banner URLs for any XboxGameMapping that still has none before we
        // read the ImageUrl map below. The per-resolve fetch (EnsureBannerArtAsync) only runs for the
        // products resolved in a single pass and never retries a transient miss, so without this an
        // art-less title (e.g. Minecraft Dungeons) would never get its banner here. Best-effort: a
        // backfill failure must never abort the image run. Covers both the 30-min schedule and the
        // Downloads "Run Now" button (FetchImagesNowAsync routes through here).
        try
        {
            var xboxMappingService = scopedServices.GetRequiredService<LancacheManager.Services.Xbox.XboxMappingService>();
            await xboxMappingService.BackfillMissingBannerArtAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutdown - let cancellation propagate rather than logging it as a non-fatal failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameImageFetch] Xbox banner URL backfill failed (non-fatal)");
        }

        // Xbox banners are NOT curated/embedded - they are fetched from the Microsoft Store
        // DisplayCatalog at mapping time and stored on XboxGameMapping.ImageUrl. Pre-load that
        // GameName-slug -> ImageUrl map so the name-keyed pass below can fetch + store an Xbox
        // GameImage under (Service = "xbox", AppId = slug), the same way it does for Blizzard/Riot.
        var xboxImageUrlBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        var xboxGameNames = nameKeyedDownloads
            .Where(t => NameKeyedBannerSource.NormalizeService(t.Service) == NameKeyedBannerSource.XboxService
                        && !string.IsNullOrEmpty(t.GameName))
            .Select(t => t.GameName!)
            .Distinct()
            .ToList();
        if (xboxGameNames.Count > 0)
        {
            var xboxMappings = await db.XboxGameMappings
                .AsNoTracking()
                .Where(m => m.ImageUrl != null && m.ImageUrl != "" && xboxGameNames.Contains(m.Title))
                .Select(m => new { m.Title, m.ImageUrl })
                .ToListAsync(stoppingToken);

            foreach (var m in xboxMappings)
            {
                var slug = NameKeyedBannerSource.Slug(m.Title);
                if (!xboxImageUrlBySlug.ContainsKey(slug))
                {
                    xboxImageUrlBySlug[slug] = m.ImageUrl!;
                }
            }
        }

        // 3. NAME-KEYED (Blizzard/Riot/Xbox): Downloads identified only by GameName (no Steam appId,
        // no Epic catalog id). Source URLs come from the curated official-CDN banner map keyed on the
        // exact GameName (Blizzard/Riot) or the DisplayCatalog ImageUrl (Xbox). Stored under
        // (AppId = slug(GameName), Service = "blizzard"|"riot"|"xbox"). Games that mapped to a Steam
        // appId above are SKIPPED here - they render Steam's header.jpg (Steam-first); only unmapped
        // name-keyed games fall back to the curated/DisplayCatalog banner.
        // Reuses nameKeyedDownloads resolved before the Steam pass (no second query).
        var nameKeyedJobs = nameKeyedDownloads
            .Select(t =>
            {
                var service = NameKeyedBannerSource.NormalizeService(t.Service);
                var slug = NameKeyedBannerSource.Slug(t.GameName!);
                // Xbox draws its URL from the DisplayCatalog ImageUrl map; the others from the
                // curated banner source.
                var url = service == NameKeyedBannerSource.XboxService
                    ? (xboxImageUrlBySlug.TryGetValue(slug, out var xboxUrl) ? xboxUrl : null)
                    : NameKeyedBannerSource.TryGetUrl(t.Service, t.GameName);
                return new { Service = service, Slug = slug, Url = url };
            })
            .Where(j => j.Service != null && j.Url != null
                && !steamMappedNameKeyedSlugs.Contains((j.Service!, j.Slug)))
            .GroupBy(j => (j.Service!, j.Slug))
            .Select(g => g.First())
            .ToList();

        var existingNameKeyedIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == NameKeyedBannerSource.BlizzardService
                || g.Service == NameKeyedBannerSource.RiotService
                || g.Service == NameKeyedBannerSource.XboxService)
            .Select(g => new { g.Service, g.AppId })
            .ToListAsync(stoppingToken);

        var existingNameKeyedSet = existingNameKeyedIds
            .Select(g => (g.Service, g.AppId))
            .ToHashSet();

        var missingNameKeyedJobs = nameKeyedJobs
            .Where(j => !existingNameKeyedSet.Contains((j.Service!, j.Slug)))
            .ToList();

        var newNameKeyedImages = 0;
        var nameKeyedDone = 0;
        foreach (var batch in missingNameKeyedJobs.Chunk(50))
        {
            if (stoppingToken.IsCancellationRequested) return;

            var tasks = batch.Select(job =>
                FetchNameKeyedImageAsync(db, client, job.Service!, job.Slug, job.Url!, stoppingToken));

            var added = await Task.WhenAll(tasks);
            newNameKeyedImages += added.Count(a => a);
            await db.SaveChangesAsync(stoppingToken);
            db.ChangeTracker.Clear();

            nameKeyedDone += batch.Length;
            await ReportPhaseAsync(50 + nameKeyedDone / (double)missingNameKeyedJobs.Count * 25, nameKeyedDone, missingNameKeyedJobs.Count);
        }

        // Name-keyed phase done - advance the band even when there was nothing to fetch.
        await ReportPhaseAsync(75, missingNameKeyedJobs.Count, missingNameKeyedJobs.Count);

        // 4. Re-fetch stale images (older than 7 days)
        var staleImages = await db.GameImages
            .Where(g => g.FetchedAtUtc < DateTime.UtcNow.AddDays(-7))
            .ToListAsync(stoppingToken);

        var staleDone = 0;
        foreach (var batch in staleImages.Chunk(50))
        {
            if (stoppingToken.IsCancellationRequested) return;

            var tasks = batch.Select(image =>
                RefreshImageAsync(client, image, stoppingToken));

            await Task.WhenAll(tasks);
            await db.SaveChangesAsync(stoppingToken);
            db.ChangeTracker.Clear();

            staleDone += batch.Length;
            await ReportPhaseAsync(75 + staleDone / (double)staleImages.Count * 25, staleDone, staleImages.Count);
        }

        _logger.LogInformation(
            "[GameImageFetch] Complete: {NewSteam} new Steam, {NewEpic} new Epic, {NewNameKeyed} new Blizzard/Riot, {Stale} refreshed",
            missingSteamIds.Count, missingEpicMappings.Count, newNameKeyedImages, staleImages.Count);

        if (missingSteamIds.Count > 0 || missingEpicMappings.Count > 0 || newNameKeyedImages > 0)
        {
            GameImagesController.IncrementCacheGeneration();
            _imageCacheService.EvictMemoryCache();
            await _notifications.NotifyAllAsync(SignalREvents.GameImagesUpdated, new
            {
                newSteamImages = missingSteamIds.Count,
                newEpicImages = missingEpicMappings.Count,
                newNameKeyedImages,
                cacheGeneration = GameImagesController.CacheGeneration
            });
        }

        if (reporter != null)
        {
            await reporter.CompleteAsync(success: true);
        }
    }

    /// <summary>
    /// Fetches a curated official-CDN banner for a name-keyed service (Blizzard/Riot) and stores
    /// it under (AppId = slug, Service = service). Applies the same MinImageBytes quality gate as
    /// the Steam/Epic passes. Returns true if an image was stored.
    /// </summary>
    private async Task<bool> FetchNameKeyedImageAsync(
        AppDbContext db,
        HttpClient client,
        string service,
        string slug,
        string url,
        CancellationToken ct)
    {
        try
        {
            // Hard-coded embedded banners (embedded://{slug}): bytes come from the embedded JPEG
            // resource, never the network. These 20 name-keyed banners are NEVER fetched at runtime.
            if (NameKeyedBannerSource.TryGetEmbeddedBytes(url, out var embeddedBytes, out var embeddedContentType))
            {
                if (embeddedBytes.Length < MinImageBytes)
                {
                    _logger.LogDebug("[GameImageFetch] Skipping tiny embedded image ({Size} bytes) for {Service} {Slug} from {Url}", embeddedBytes.Length, service, slug, url);
                    return false;
                }

                lock (db)
                {
                    db.GameImages.Add(new GameImage
                    {
                        AppId = slug,
                        Service = service,
                        ImageData = embeddedBytes,
                        ContentType = embeddedContentType,
                        SourceUrl = url,
                        FetchedAtUtc = DateTime.UtcNow
                    });
                }

                return true;
            }

            await _httpThrottle.WaitAsync(ct);
            try
            {
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return false;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < MinImageBytes)
                {
                    _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for {Service} {Slug} from {Url}", bytes.Length, service, slug, url);
                    return false;
                }

                lock (db)
                {
                    db.GameImages.Add(new GameImage
                    {
                        AppId = slug,
                        Service = service,
                        ImageData = bytes,
                        // Some official CDNs (e.g. callofduty.com) serve real JPEG/PNG bytes as
                        // application/octet-stream; coerce non-image content-types so the browser renders it.
                        ContentType = (response.Content.Headers.ContentType?.MediaType is string mt && mt.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) ? mt : "image/jpeg",
                        SourceUrl = url,
                        FetchedAtUtc = DateTime.UtcNow
                    });
                }

                return true;
            }
            finally
            {
                _httpThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch {Service} image {Slug} from {Url}", service, slug, url);
            return false;
        }
    }

    // Minimum image size - Steam returns tiny ~1-2KB placeholder images for some apps
    private const int MinImageBytes = 5000;

    /// <summary>
    /// Steam CDN endpoints to try, in priority order.
    /// shared.* uses the newer /store_item_assets/ path format.
    /// cdn.akamai uses the legacy /steam/apps/ path format (still works for some games).
    /// </summary>
    private static readonly (string Domain, string BasePath)[] _steamCdnEndpoints =
    [
        ("shared.akamai.steamstatic.com", "/store_item_assets/steam/apps"),
        ("shared.fastly.steamstatic.com", "/store_item_assets/steam/apps"),
        ("cdn.akamai.steamstatic.com", "/steam/apps")
    ];

    private async Task FetchSteamImageAsync(
        AppDbContext db,
        HttpClient client,
        string appId,
        Dictionary<long, string> picsUrlMap,
        Dictionary<long, List<long>> depotOwnerLookup,
        Dictionary<long, List<long>> downloadDepotLookup,
        CancellationToken ct)
    {
        // Try fetching the image using the given appId
        var imageBytes = await TryGetSteamImageAsync(client, appId, picsUrlMap, ct);

        if (imageBytes != null)
        {
            lock (db)
            {
                db.GameImages.Add(new GameImage
                {
                    AppId = appId,
                    Service = "steam",
                    ImageData = imageBytes.Value.bytes,
                    ContentType = imageBytes.Value.contentType,
                    SourceUrl = imageBytes.Value.sourceUrl,
                    FetchedAtUtc = DateTime.UtcNow
                });
            }
            return;
        }

        // Try to find a parent app ID via pre-loaded depot mappings
        var parentAppId = FindParentAppId(appId, depotOwnerLookup, downloadDepotLookup);
        if (parentAppId == null)
        {
            _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} and no parent app found", appId);
            return;
        }

        _logger.LogInformation("[GameImageFetch] No image for app {AppId}, trying parent app {ParentAppId}", appId, parentAppId);

        // Try fetching using the parent's app ID
        var parentBytes = await TryGetSteamImageAsync(client, parentAppId, picsUrlMap, ct);
        if (parentBytes != null)
        {
            // Store under the ORIGINAL appId so frontend lookups work
            lock (db)
            {
                db.GameImages.Add(new GameImage
                {
                    AppId = appId,
                    Service = "steam",
                    ImageData = parentBytes.Value.bytes,
                    ContentType = parentBytes.Value.contentType,
                    SourceUrl = parentBytes.Value.sourceUrl,
                    FetchedAtUtc = DateTime.UtcNow
                });
            }
            _logger.LogInformation("[GameImageFetch] Successfully fetched image for app {AppId} using parent app {ParentAppId}", appId, parentAppId);
        }
        else
        {
            _logger.LogDebug("[GameImageFetch] No valid image found for app {AppId} or parent app {ParentAppId}", appId, parentAppId);
        }
    }

    /// <summary>
    /// Tries to fetch a Steam game header image from multiple CDN domains.
    /// Returns the image bytes from the first domain that responds successfully,
    /// or null if all domains fail.
    /// </summary>
    /// <summary>
    /// Tries to fetch a Steam game header image from multiple CDN domains.
    /// Returns the image bytes from the first domain that responds successfully,
    /// or null if all domains fail.
    /// </summary>
    private async Task<byte[]?> TryFetchFromSteamCdnAsync(HttpClient client, long appId, CancellationToken ct)
    {
        // First pass: try header.jpg across all CDN endpoints
        foreach (var (domain, basePath) in _steamCdnEndpoints)
        {
            var url = $"https://{domain}{basePath}/{appId}/header.jpg";
            try
            {
                await _httpThrottle.WaitAsync(ct);
                try
                {
                    var response = await client.GetAsync(url, ct);
                    if (!response.IsSuccessStatusCode) continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length >= MinImageBytes)
                    {
                        _logger.LogDebug("[GameImageFetch] CDN hit for Steam app {AppId}: {Url}", appId, url);
                        return bytes;
                    }
                }
                finally
                {
                    _httpThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] CDN fetch failed for Steam app {AppId} from {Url}", appId, url);
            }
        }

        // Second pass: try capsule_616x353.jpg across all CDN endpoints
        foreach (var (domain, basePath) in _steamCdnEndpoints)
        {
            var url = $"https://{domain}{basePath}/{appId}/capsule_616x353.jpg";
            try
            {
                await _httpThrottle.WaitAsync(ct);
                try
                {
                    var response = await client.GetAsync(url, ct);
                    if (!response.IsSuccessStatusCode) continue;

                    var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                    if (bytes.Length >= MinImageBytes)
                    {
                        _logger.LogDebug("[GameImageFetch] CDN capsule hit for Steam app {AppId}: {Url}", appId, url);
                        return bytes;
                    }
                }
                finally
                {
                    _httpThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] CDN capsule fetch failed for Steam app {AppId} from {Url}", appId, url);
            }
        }

        return null;
    }

    private async Task<(byte[] bytes, string contentType, string sourceUrl)?> TryGetSteamImageAsync(
        HttpClient client,
        string appId,
        Dictionary<long, string> picsUrlMap,
        CancellationToken ct)
    {
        // Tier 1: PICS-sourced URL from pre-loaded dictionary (eliminates per-game DB query)
        if (long.TryParse(appId, out var appIdLong) && picsUrlMap.TryGetValue(appIdLong, out var picsUrl))
        {
            try
            {
                await _httpThrottle.WaitAsync(ct);
                try
                {
                    var response = await client.GetAsync(picsUrl, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                        if (bytes.Length >= MinImageBytes)
                        {
                            return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg", picsUrl);
                        }

                        _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for {AppId} from {Url}", bytes.Length, appId, picsUrl);
                    }
                }
                finally
                {
                    _httpThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Steam image {AppId} from PICS URL {Url}", appId, picsUrl);
            }
        }

        // Tier 2: Multi-CDN fallback (shared.akamai, shared.fastly, cdn.akamai)
        if (long.TryParse(appId, out var appIdForCdn))
        {
            var cdnBytes = await TryFetchFromSteamCdnAsync(client, appIdForCdn, ct);
            if (cdnBytes != null)
            {
                var sourceUrl = $"https://{_steamCdnEndpoints[0].Domain}{_steamCdnEndpoints[0].BasePath}/{appId}/header.jpg";
                return (cdnBytes, "image/jpeg", sourceUrl);
            }
        }

        // Tier 3: Steam Store API fallback - newer games use hash-based paths that aren't predictable from the app ID alone
        var storeUrl = await GetStoreHeaderImageUrlAsync(client, appId, ct);
        if (storeUrl != null)
        {
            try
            {
                await _httpThrottle.WaitAsync(ct);
                try
                {
                    var response = await client.GetAsync(storeUrl, ct);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                        if (bytes.Length >= MinImageBytes)
                        {
                            _logger.LogInformation("[GameImageFetch] Tier 3 (Store API) succeeded for Steam app {AppId}: {Url}", appId, storeUrl);
                            return (bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg", storeUrl);
                        }
                    }
                }
                finally
                {
                    _httpThrottle.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[GameImageFetch] Tier 3 fetch failed for Steam app {AppId} from {Url}", appId, storeUrl);
            }
        }

        _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} after trying all tiers", appId);
        return null;
    }

    /// <summary>
    /// Queries the Steam Store API to get the actual header_image URL for an app.
    /// Newer games use shared.akamai.steamstatic.com with hash-based paths
    /// that aren't available at the predictable cdn.akamai.steamstatic.com paths.
    /// </summary>
    private async Task<string?> GetStoreHeaderImageUrlAsync(
        HttpClient client,
        string appId,
        CancellationToken ct)
    {
        try
        {
            await _httpThrottle.WaitAsync(ct);
            try
            {
                var response = await client.GetAsync(
                    $"https://store.steampowered.com/api/appdetails?appids={appId}", ct);

                if (!response.IsSuccessStatusCode) return null;

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty(appId, out var appElement) &&
                    appElement.TryGetProperty("success", out var success) && success.GetBoolean() &&
                    appElement.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("header_image", out var headerImage))
                {
                    var url = headerImage.GetString();
                    if (!string.IsNullOrEmpty(url))
                    {
                        _logger.LogDebug("[GameImageFetch] Store API returned header_image for {AppId}: {Url}", appId, url);
                        return url;
                    }
                }
            }
            finally
            {
                _httpThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Store API lookup failed for {AppId}", appId);
        }

        return null;
    }

    /// <summary>
    /// Tries to find a parent app ID for a DLC/sub-app using pre-loaded depot mapping dictionaries.
    /// No DB queries - uses dictionary lookups only.
    /// </summary>
    private string? FindParentAppId(
        string appId,
        Dictionary<long, List<long>> depotOwnerLookup,
        Dictionary<long, List<long>> downloadDepotLookup)
    {
        if (!long.TryParse(appId, out var appIdLong))
            return null;

        // Strategy 1: Check if appId, appId+1, or appId-1 appears as a DepotId mapped to a different owner app
        var candidateDepotIds = new List<long> { appIdLong };
        if (appIdLong + 1 <= uint.MaxValue)
            candidateDepotIds.Add(appIdLong + 1);
        if (appIdLong - 1 > 0)
            candidateDepotIds.Add(appIdLong - 1);

        foreach (var depotId in candidateDepotIds)
        {
            if (depotOwnerLookup.TryGetValue(depotId, out var ownerAppIds))
            {
                var differentOwner = ownerAppIds.FirstOrDefault(id => id != appIdLong);
                if (differentOwner != 0)
                    return differentOwner.ToString();
            }
        }

        // Strategy 2: Find depots for this app from pre-loaded download depot lookup,
        // then check owner mapping
        if (downloadDepotLookup.TryGetValue(appIdLong, out var depotIds))
        {
            foreach (var depotId in depotIds)
            {
                if (depotOwnerLookup.TryGetValue(depotId, out var ownerAppIds))
                {
                    var differentOwner = ownerAppIds.FirstOrDefault(id => id != appIdLong);
                    if (differentOwner != 0)
                        return differentOwner.ToString();
                }
            }
        }

        return null;
    }

    private async Task FetchEpicImageAsync(
        AppDbContext db,
        HttpClient client,
        EpicGameMapping mapping,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mapping.ImageUrl)) return;

        var url = EpicApiDirectClient.EnsureResizeParams(mapping.ImageUrl);
        try
        {
            await _httpThrottle.WaitAsync(ct);
            try
            {
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < MinImageBytes)
                {
                    _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for Epic {AppId} from {Url}", bytes.Length, mapping.AppId, url);
                    return;
                }

                lock (db)
                {
                    db.GameImages.Add(new GameImage
                    {
                        AppId = mapping.AppId,
                        Service = "epicgames",
                        ImageData = bytes,
                        ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg",
                        SourceUrl = url,
                        FetchedAtUtc = DateTime.UtcNow
                    });
                }
            }
            finally
            {
                _httpThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Epic image {AppId} from {Url}", mapping.AppId, url);
        }
    }

    private async Task RefreshImageAsync(
        HttpClient client,
        GameImage image,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(image.SourceUrl)) return;

        // Hard-coded embedded banners (embedded://{slug}): re-seed from the embedded JPEG bytes,
        // never the network. These name-keyed banners are NEVER auto-updated over HTTP.
        if (NameKeyedBannerSource.TryGetEmbeddedBytes(image.SourceUrl, out var embeddedBytes, out var embeddedContentType))
        {
            if (embeddedBytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny embedded image ({Size} bytes) during refresh for {AppId}", embeddedBytes.Length, image.AppId);
                return;
            }

            image.ImageData = embeddedBytes;
            image.ContentType = embeddedContentType;
            image.FetchedAtUtc = DateTime.UtcNow;
            image.UpdatedAtUtc = DateTime.UtcNow;
            return;
        }

        try
        {
            // Tier 1: Try the stored SourceUrl (PICS hash URL or previously resolved URL)
            HttpResponseMessage response;
            await _httpThrottle.WaitAsync(ct);
            try
            {
                response = await client.GetAsync(image.SourceUrl, ct);
            }
            finally
            {
                _httpThrottle.Release();
            }

            // If the primary SourceUrl fails for Steam images, fall back through Tier 2 and Tier 3.
            // Steam PICS-sourced URLs contain hash paths that can expire/404.
            if (!response.IsSuccessStatusCode && image.Service == "steam")
            {
                _logger.LogDebug("[GameImageFetch] Primary URL failed ({Status}) for Steam {AppId}, trying multi-CDN Tier 2",
                    (int)response.StatusCode, image.AppId);

                // Tier 2: Multi-CDN fallback (shared.akamai, shared.fastly, cdn.akamai)
                if (long.TryParse(image.AppId, out var appIdLong))
                {
                    var cdnBytes = await TryFetchFromSteamCdnAsync(client, appIdLong, ct);
                    if (cdnBytes != null)
                    {
                        var cdnUrl = $"https://{_steamCdnEndpoints[0].Domain}{_steamCdnEndpoints[0].BasePath}/{image.AppId}/header.jpg";
                        image.SourceUrl = cdnUrl;
                        image.ImageData = cdnBytes;
                        image.ContentType = "image/jpeg";
                        image.FetchedAtUtc = DateTime.UtcNow;
                        image.UpdatedAtUtc = DateTime.UtcNow;
                        return;
                    }
                }

                // Tier 3: Steam Store API - newer games use hash-based paths that aren't predictable from the app ID alone
                var storeUrl = await GetStoreHeaderImageUrlAsync(client, image.AppId, ct);
                if (storeUrl != null)
                {
                    await _httpThrottle.WaitAsync(ct);
                    try
                    {
                        response = await client.GetAsync(storeUrl, ct);
                        if (response.IsSuccessStatusCode)
                        {
                            image.SourceUrl = storeUrl;
                        }
                    }
                    finally
                    {
                        _httpThrottle.Release();
                    }
                }
            }

            if (!response.IsSuccessStatusCode) return;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) during refresh for {AppId}", bytes.Length, image.AppId);
                return;
            }

            image.ImageData = bytes;
            image.ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            image.FetchedAtUtc = DateTime.UtcNow;
            image.UpdatedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to refresh image {AppId}", image.AppId);
        }
    }
}
