using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services.EpicMapping;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
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

    // A pass reads the list of games it will fetch art for when it starts, so rows written after
    // that are invisible to it. Set before every attempt to take the execution lock and cleared by
    // the attempt that takes it, so a caller refused the lock leaves exactly one follow-up pass
    // behind instead of leaving its banners for the next scheduled tick half an hour later.
    private static int _followUpPassWanted;

    // Max concurrent HTTP requests for image fetching
    private static readonly SemaphoreSlim _httpThrottle = new(5, 5);

    // Every GameImagesUpdated costs each connected client one forced /available refetch
    // (useAvailableGameImages bypasses its own freshness window and in-flight dedupe on a
    // version change), so a long pass storing hundreds of banners cannot emit on every batch.
    // 2000 ms keeps that refetch to at most one every two seconds per client even during the
    // densest run of stores, while still letting banners fill in before the pass ends. This is
    // not a value that should scale with the machine, the disk or the network the way concurrency
    // or scan timeouts do: it bounds how often a BROWSER is forced to hit an endpoint, and a
    // browser's own request budget is the same number on every deployment, fast box or slow.
    private readonly ProgressEmitGate _bannerEmitGate = new(2000);

    // Cumulative count of images stored or refreshed so far in the pass currently running - the
    // revision _bannerEmitGate compares to decide whether new work is worth telling clients
    // about. Reset at the top of FetchImagesAsync so a new pass does not inherit the last one's
    // count.
    private long _bannerRevision;

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
    /// Public trigger so other services can request an immediate image fetch after ALL game
    /// detection, mapping, and DB saves are complete. Starts a fetch pass on a background task owned
    /// by this singleton and returns its tracked operation id, or null when a pass is already
    /// running. The pass reports through the same ScheduledRunReporter lifecycle as a scheduled
    /// run, so it shows live progress; <paramref name="trigger"/> and the service's notification
    /// mode decide whether the card is visible.
    /// </summary>
    /// <returns>
    /// The tracked operation id of the pass this call started, or null when a pass was already
    /// holding the execution lock.
    ///
    /// A null is not a dropped request, and callers may rely on that. Every call records that a pass
    /// is wanted before it attempts the lock, and whichever pass holds the lock starts one more on
    /// its way out, so a caller answered with null needs no fallback of its own. There is one
    /// null-returning path and it is after that record, so no null can mean "refused and forgotten".
    ///
    /// Two limits on what the follow-up carries. It runs with <paramref name="refreshEpicImageUrls"/>
    /// false whatever the refused caller asked for, so a refused request for a fresh Epic catalog
    /// read is served from the stored URLs instead. And however many requests were refused, one
    /// follow-up pass runs, which is right because a pass reads the whole outstanding work list. The
    /// record is in memory and does not survive a restart.
    /// </returns>
    /// <remarks>
    /// The request is recorded before the lock is attempted rather than after a refusal because a
    /// record written after a failed acquire can land in the moment the running pass is releasing
    /// the lock and reading that record, and would then be seen by nobody. Recording first means a
    /// caller whose acquire fails has already published its request, and a caller that arrives after
    /// the release takes the lock itself. The acquire clears the record before the pass reads its
    /// work list, so anything erased there is covered by that pass.
    ///
    /// The execution lock is taken here rather than inside the background task so the caller learns
    /// straight away whether a run actually began. The run's lifetime belongs to this singleton, so
    /// an HTTP caller can report it and return.
    /// </remarks>
    /// <param name="refreshEpicImageUrls">
    /// Re-reads the Epic catalog before the pass so the Epic phase downloads current art. It is a
    /// network call to Epic, so it belongs to this background task rather than to the request that
    /// starts it, and only the "clear the cache and fetch everything again" path asks for it. The
    /// scheduled cadence leaves it off: Epic's own catalog refresh runs on its own interval, which
    /// the user sets in hours, and re-polling it every half hour would ignore that setting.
    /// </param>
    public async Task<Guid?> StartFetchInBackgroundAsync(bool refreshEpicImageUrls, RunTrigger trigger)
    {
        // Announced before the attempt rather than after a refusal: a request announced after a
        // failed acquire can land in the moment a finishing pass is releasing the lock and reading
        // the flag, and would then be seen by nobody.
        Interlocked.Exchange(ref _followUpPassWanted, 1);

        if (!_executionLock.Wait(0))
        {
            _logger.LogDebug("[GameImageFetch] Not starting - another fetch is already running");
            return null;
        }

        // This pass reads its work list below, after this point, so it covers everything announced
        // so far.
        Interlocked.Exchange(ref _followUpPassWanted, 0);

        // Started eagerly (not lazily inside FetchImagesAsync) so the caller gets the operation id
        // back. The reporter owns the tracked operation and the cancellation source from here on.
        var reporter = new ScheduledRunReporter(
            _notifications,
            _operationTracker,
            ServiceKey,
            OperationType.GameImageFetch,
            _eventNames,
            $"{StageBase}.complete",
            EffectiveNotificationMode.AllowsTrigger(trigger),
            CancellationToken.None);
        await reporter.StartAsync($"{StageBase}.starting");
        var operationId = reporter.OperationId;

        _ = Task.Run(async () =>
        {
            string? error = null;
            try
            {
                using var scope = _serviceProvider.CreateScope();

                if (refreshEpicImageUrls)
                {
                    // Epic stores the URL the Epic phase downloads from, so re-read the catalog
                    // before the pass gets there. Do it after and a just-cleared cache is refilled
                    // from exactly the art the user pressed the button to replace.
                    var epicMapping = scope.ServiceProvider.GetService<EpicMappingService>();
                    if (epicMapping is { IsAuthenticated: true })
                    {
                        try
                        {
                            var refreshedUrls = await epicMapping.RefreshImagesAsync(reporter.Token);
                            _logger.LogInformation(
                                "[GameImageFetch] Refreshed {Count} Epic image URLs", refreshedUrls);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Epic's catalog call fails on an expired session or a bad response.
                            // The stored URLs still fetch, so the pass carries on.
                            _logger.LogWarning(ex, "[GameImageFetch] Epic image URL refresh failed");
                        }
                    }
                }

                await FetchImagesAsync(scope.ServiceProvider, reporter, reporter.Token);
            }
            catch (OperationCanceledException) when (reporter.Token.IsCancellationRequested)
            {
                // DisposeAsync below completes the run as cancelled.
            }
            catch (Exception ex)
            {
                error = ex.Message;
                _logger.LogWarning(ex, "[GameImageFetch] Background fetch failed");
            }
            finally
            {
                // Release the service-local gate before completing the operation, so a queued waiter
                // promoted by the completion can acquire it.
                _executionLock.Release();
                if (error != null)
                {
                    await reporter.CompleteAsync(success: false, error: error);
                }
                await reporter.DisposeAsync();
                StartFollowUpPass();
            }
        }, CancellationToken.None);

        return operationId;
    }

    /// <summary>
    /// Starts one more pass when a start was refused the execution lock while this one held it. That
    /// caller had just written rows this pass read past, so without the follow-up their banners wait
    /// for the next scheduled tick.
    /// </summary>
    /// <remarks>
    /// Called after the lock is released, so the pass it starts can take it. Epic's catalog is not
    /// re-read: that belongs to the clear-the-cache path and to Epic's own refresh interval, and the
    /// stored URLs are what this pass needs to fetch the art the previous one missed.
    /// </remarks>
    private void StartFollowUpPass()
    {
        if (Interlocked.Exchange(ref _followUpPassWanted, 0) == 1)
        {
            _ = StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        }
    }

    // AppIds a log-processing trigger has already started a pass for. A game whose art cannot be
    // fetched at all (delisted, no banner published) stays missing after the pass, and without this
    // set it would start a new pass on every log-processing run for as long as it appears in the
    // logs. The scheduled sweep remains the retry path for those.
    private readonly HashSet<long> _artTriggerAttemptedAppIds = new();
    private readonly object _artTriggerLock = new();

    /// <summary>
    /// Starts a fetch pass when a download has a Steam game identity but no stored banner yet.
    /// Called after each log-processing run: the Rust processor maps depots itself, so the
    /// SteamKit2 mapping trigger never fires for games it identified, and their banners would
    /// otherwise stay blank until the next scheduled tick up to half an hour later.
    /// </summary>
    public async Task StartFetchForMissingArtAsync(CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Same filter as the Steam phase, so a trigger never starts a pass that finds nothing.
        var steamAppIds = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId != null && d.GameAppId != 0 && !string.IsNullOrEmpty(d.GameName))
            .Select(d => d.GameAppId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var existingSteamIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == "steam")
            .Select(g => g.AppId)
            .ToListAsync(ct);

        var existingSet = existingSteamIds.ToHashSet();

        var anyNew = false;
        lock (_artTriggerLock)
        {
            foreach (var appId in steamAppIds)
            {
                if (!existingSet.Contains(appId.ToString()) && _artTriggerAttemptedAppIds.Add(appId))
                {
                    anyNew = true;
                }
            }
        }

        if (anyNew)
        {
            await StartFetchInBackgroundAsync(refreshEpicImageUrls: false, RunTrigger.Scheduled);
        }
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
        // Prevent concurrent execution - a programmatic trigger and a scheduled run can overlap. A run that
        // loses this race did no work, so it returns before starting the reporter (no card).
        if (!await _executionLock.WaitAsync(0, stoppingToken))
        {
            _logger.LogDebug("[GameImageFetch] Skipping - another fetch is already running");
            return;
        }

        // Same as the background start: this pass reads its work list below, so it covers every
        // request announced up to here.
        Interlocked.Exchange(ref _followUpPassWanted, 0);

        try
        {
            await FetchImagesAsync(scopedServices, reporter, stoppingToken);
        }
        finally
        {
            _executionLock.Release();
            StartFollowUpPass();
        }
    }

    // Progress runs over four equal-weight bands in phase order: Steam, Epic, name-keyed, stale
    // refresh. Each phase reports its own 0-to-1 fraction and the run maps that into the phase's
    // band, so no phase has to know where its slice of the bar starts.
    private const double PhaseBandPercent = 25;
    private const int SteamBand = 0;
    private const int EpicBand = 1;
    private const int NameKeyedBand = 2;
    private const int StaleBand = 3;

    /// <summary>
    /// How a phase reports itself: <paramref name="fraction"/> runs 0 to 1 within that phase.
    /// </summary>
    private delegate Task ReportPhaseProgress(double fraction, int processed, int total);

    private static async Task ReportBandAsync(
        ScheduledRunReporter? reporter,
        int band,
        double fraction,
        int processed,
        int total)
    {
        if (reporter == null)
        {
            return;
        }

        await reporter.ReportAsync(
            band * PhaseBandPercent + fraction * PhaseBandPercent,
            $"{StageBase}.running",
            new Dictionary<string, object?>
            {
                ["processed"] = processed,
                ["total"] = total
            });
    }

    /// <summary>
    /// Makes newly stored banners visible while a pass is still running. Called after every batch
    /// in every phase; skips entirely when the batch stored nothing, so a stretch of failed fetches
    /// never touches the gate, and <see cref="_bannerEmitGate"/> holds the rest to at most one
    /// broadcast every two seconds.
    /// </summary>
    private async Task EmitIncrementalBannerUpdateAsync(int storedInBatch)
    {
        if (storedInBatch <= 0)
        {
            return;
        }

        _bannerRevision += storedInBatch;
        if (!_bannerEmitGate.ShouldEmit(StageBase, _bannerRevision))
        {
            return;
        }

        GameImagesController.IncrementCacheGeneration();
        _imageCacheService.EvictMemoryCache();
        await _notifications.NotifyAllAsync(SignalREvents.GameImagesUpdated, new
        {
            cacheGeneration = GameImagesController.CacheGeneration
        });
    }

    private async Task FetchImagesAsync(
        IServiceProvider scopedServices,
        ScheduledRunReporter? reporter,
        CancellationToken stoppingToken)
    {
        var db = scopedServices.GetRequiredService<AppDbContext>();
        var httpClientFactory = scopedServices.GetRequiredService<IHttpClientFactory>();
        var client = httpClientFactory.CreateClient("SteamImages");

        var totalDownloads = await db.Downloads.CountAsync(stoppingToken);
        if (totalDownloads == 0)
        {
            _logger.LogInformation("[GameImageFetch] Downloads table is empty - log processing hasn't completed yet, will retry next cycle");
            if (reporter is { IsStarted: true })
            {
                // The background-trigger path starts its reporter eagerly, so this run must still
                // reach a terminal instead of being disposed as a failure.
                await reporter.CompleteAsync(success: true, skipped: true);
            }
            return;
        }

        // There is work to do (downloads exist): start the run now so the "no downloads yet" retry never
        // surfaces a card. The background-trigger path arrives already started.
        if (reporter is { IsStarted: false })
        {
            await reporter.StartAsync($"{StageBase}.starting");
        }

        ReportPhaseProgress InBand(int band) =>
            (fraction, processed, total) => ReportBandAsync(reporter, band, fraction, processed, total);

        _bannerRevision = 0;
        _bannerEmitGate.Reset();

        try
        {
            var nameKeyedDownloads = await LoadNameKeyedDownloadsAsync(db, stoppingToken);
            var (steamMappedAppIds, steamCoveredSlugs) = ResolveNameKeyedSteamApps(nameKeyedDownloads);

            // A phase answers null when cancellation stopped it partway. The run ends there: no
            // summary log and no reporter completion, but the finally below still emits once so a
            // pass that ends early still tells clients it is over.
            var missingSteamCount = await FetchMissingSteamImagesAsync(
                db, client, steamMappedAppIds, InBand(SteamBand), stoppingToken);
            if (missingSteamCount == null) return;
            await ReportBandAsync(reporter, SteamBand, 1, missingSteamCount.Value, missingSteamCount.Value);

            var missingEpicCount = await FetchMissingEpicImagesAsync(
                db, client, InBand(EpicBand), stoppingToken);
            if (missingEpicCount == null) return;
            await ReportBandAsync(reporter, EpicBand, 1, missingEpicCount.Value, missingEpicCount.Value);

            var nameKeyed = await FetchMissingNameKeyedImagesAsync(
                scopedServices, db, client, nameKeyedDownloads, steamCoveredSlugs, InBand(NameKeyedBand), stoppingToken);
            if (nameKeyed == null) return;
            await ReportBandAsync(reporter, NameKeyedBand, 1, nameKeyed.Value.Attempted, nameKeyed.Value.Attempted);

            var staleRefreshed = await RefreshStaleImagesAsync(db, client, InBand(StaleBand), stoppingToken);
            if (staleRefreshed == null) return;

            _logger.LogInformation(
                "[GameImageFetch] Complete: {NewSteam} new Steam, {NewEpic} new Epic, {NewNameKeyed} new Blizzard/Riot, {Stale} refreshed",
                missingSteamCount.Value, missingEpicCount.Value, nameKeyed.Value.Stored, staleRefreshed.Value);

            if (reporter != null)
            {
                await reporter.CompleteAsync(success: true);
            }
        }
        finally
        {
            // Ungated and unconditional: this is the only way a pass that stored nothing (every
            // upstream fetch failed) or was canceled partway still tells clients it ended. The
            // version bump is the only thing that clears the image-error sets the parent views keep,
            // so a banner that failed once and gave up gets its next attempt from here. Runs exactly
            // once per pass regardless of which return above was taken.
            GameImagesController.IncrementCacheGeneration();
            _imageCacheService.EvictMemoryCache();
            await _notifications.NotifyAllAsync(SignalREvents.GameImagesUpdated, new
            {
                cacheGeneration = GameImagesController.CacheGeneration
            });
        }
    }

    /// <summary>
    /// Reads the name-keyed (Blizzard/Riot/Xbox) downloads once for the whole run: the Steam phase
    /// needs the ones that also exist on Steam, and the name-keyed phase needs all of them.
    /// </summary>
    private static async Task<List<(string Service, string GameName)>> LoadNameKeyedDownloadsAsync(
        AppDbContext db,
        CancellationToken ct)
    {
        var rows = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId == null
                && !string.IsNullOrEmpty(d.GameName)
                && (d.Service == "blizzard" || d.Service == "battle.net" || d.Service == "battlenet"
                    || d.Service == "riot" || d.Service == "riotgames"
                    || d.Service == "xbox" || d.Service == "xboxlive" || d.Service == "microsoft"))
            .Select(d => new { d.Service, d.GameName })
            .Distinct()
            .ToListAsync(ct);

        return rows.Select(r => (r.Service, GameName: r.GameName!)).ToList();
    }

    /// <summary>
    /// Name-keyed downloads have GameAppId == null but some of those games ALSO exist on Steam.
    /// Resolving them up front lets them ride the SAME Steam fetch path ("Steam-first"): the row lands
    /// under Service="steam", AppId=&lt;steamAppId&gt; and reuses all the Steam CDN/store URL logic.
    /// </summary>
    /// <returns>
    /// The Steam appIds to fetch on top of the ones the Downloads table already carries, and the
    /// (canonical service, slug) pairs the name-keyed phase must skip because Steam now covers them.
    /// </returns>
    private static (List<long> SteamAppIds, HashSet<(string Service, string Slug)> CoveredSlugs) ResolveNameKeyedSteamApps(
        IReadOnlyList<(string Service, string GameName)> nameKeyedDownloads)
    {
        var steamAppIds = new List<long>();
        var coveredSlugs = new HashSet<(string Service, string Slug)>();

        foreach (var t in nameKeyedDownloads)
        {
            var steamAppId = NameKeyedSteamAppIds.TryGetSteamAppId(t.Service, t.GameName);
            if (steamAppId == null) continue;

            steamAppIds.Add(steamAppId.Value);
            var canonicalService = NameKeyedBannerSource.NormalizeService(t.Service);
            if (canonicalService != null)
            {
                coveredSlugs.Add((canonicalService, NameKeyedBannerSource.Slug(t.GameName)));
            }
        }

        return (steamAppIds, coveredSlugs);
    }

    /// <summary>
    /// Fetches a banner for every Steam appId in the Downloads table that has no GameImage row yet,
    /// plus the name-keyed games that resolved to a Steam appId.
    /// </summary>
    /// <returns>How many images the phase set out to fetch, or null when cancellation stopped it.</returns>
    private async Task<int?> FetchMissingSteamImagesAsync(
        AppDbContext db,
        HttpClient client,
        IReadOnlyList<long> nameKeyedSteamAppIds,
        ReportPhaseProgress report,
        CancellationToken ct)
    {
        var steamAppIds = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId != null && d.GameAppId != 0 && !string.IsNullOrEmpty(d.GameName))
            .Select(d => d.GameAppId!.Value)
            .Distinct()
            .ToListAsync(ct);

        steamAppIds.AddRange(nameKeyedSteamAppIds);

        var existingSteamIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == "steam")
            .Select(g => g.AppId)
            .ToListAsync(ct);

        var missingSteamIds = steamAppIds
            .Distinct()
            .Select(id => id.ToString())
            .Except(existingSteamIds)
            .ToList();

        if (missingSteamIds.Count == 0)
        {
            return 0;
        }

        // Pre-load PICS URLs for all missing Steam apps in a single batch query (eliminates N+1)
        var missingAppIdLongs = missingSteamIds
            .Select(id => long.TryParse(id, out var v) ? v : (long?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToList();

        var picsUrlMap = await DownloadGameImageUrlQueries.GetLatestUrlsForSteamAppsAsync(
            db, missingAppIdLongs, ct);

        // Pre-load SteamDepotMappings for parent app lookup (eliminates N+1 in FindParentAppId)
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
            .ToListAsync(ct);

        var depotOwnerLookup = depotOwnerMap
            .GroupBy(m => m.DepotId)
            .ToDictionary(g => g.Key, g => g.Select(m => m.AppId).ToList());

        // Pre-load download depot IDs per app (for Strategy 2 fallback)
        var downloadDepotMap = await db.Downloads
            .AsNoTracking()
            .Where(d => d.GameAppId != null && missingAppIdLongs.Contains(d.GameAppId.Value) && d.DepotId.HasValue)
            .Select(d => new { AppId = d.GameAppId!.Value, DepotId = d.DepotId!.Value })
            .Distinct()
            .ToListAsync(ct);

        var downloadDepotLookup = downloadDepotMap
            .GroupBy(x => x.AppId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.DepotId).ToList());

        var done = 0;
        foreach (var batch in missingSteamIds.Chunk(50))
        {
            if (ct.IsCancellationRequested) return null;

            var tasks = batch.Select(appId =>
                FetchSteamImageAsync(db, client, appId, picsUrlMap, depotOwnerLookup, downloadDepotLookup, ct));

            var stored = await Task.WhenAll(tasks);
            var storedInBatch = stored.Count(wasStored => wasStored);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await EmitIncrementalBannerUpdateAsync(storedInBatch);

            done += batch.Length;
            await report(done / (double)missingSteamIds.Count, done, missingSteamIds.Count);
        }

        return missingSteamIds.Count;
    }

    /// <summary>
    /// Fetches a banner for every EpicGameMapping that carries an ImageUrl and has no GameImage row
    /// yet.
    /// </summary>
    /// <returns>How many images the phase set out to fetch, or null when cancellation stopped it.</returns>
    private async Task<int?> FetchMissingEpicImagesAsync(
        AppDbContext db,
        HttpClient client,
        ReportPhaseProgress report,
        CancellationToken ct)
    {
        var epicMappings = await db.EpicGameMappings
            .AsNoTracking()
            .Where(m => m.ImageUrl != null)
            .ToListAsync(ct);

        var existingEpicIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == "epicgames")
            .Select(g => g.AppId)
            .ToListAsync(ct);

        var missingEpicMappings = epicMappings
            .Where(m => !existingEpicIds.Contains(m.AppId))
            .ToList();

        var done = 0;
        foreach (var batch in missingEpicMappings.Chunk(50))
        {
            if (ct.IsCancellationRequested) return null;

            var tasks = batch.Select(mapping =>
                FetchEpicImageAsync(db, client, mapping, ct));

            var stored = await Task.WhenAll(tasks);
            var storedInBatch = stored.Count(wasStored => wasStored);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await EmitIncrementalBannerUpdateAsync(storedInBatch);

            done += batch.Length;
            await report(done / (double)missingEpicMappings.Count, done, missingEpicMappings.Count);
        }

        return missingEpicMappings.Count;
    }

    /// <summary>
    /// Fetches banners for downloads identified only by GameName (Blizzard/Riot/Xbox), stored under
    /// (AppId = slug(GameName), Service = "blizzard"|"riot"|"xbox"). Source URLs come from the curated
    /// official-CDN banner map keyed on the exact GameName (Blizzard/Riot) or from the DisplayCatalog
    /// ImageUrl (Xbox). Games in <paramref name="steamCoveredSlugs"/> are SKIPPED - they render
    /// Steam's header.jpg (Steam-first), so only unmapped name-keyed games fall back to the curated
    /// or DisplayCatalog banner.
    /// </summary>
    /// <returns>
    /// How many images the phase set out to fetch and how many it stored, or null when cancelled.
    /// </returns>
    private async Task<(int Attempted, int Stored)?> FetchMissingNameKeyedImagesAsync(
        IServiceProvider scopedServices,
        AppDbContext db,
        HttpClient client,
        IReadOnlyList<(string Service, string GameName)> nameKeyedDownloads,
        HashSet<(string Service, string Slug)> steamCoveredSlugs,
        ReportPhaseProgress report,
        CancellationToken ct)
    {
        await BackfillXboxBannerUrlsAsync(scopedServices, ct);

        var xboxImageUrlBySlug = await LoadXboxBannerUrlsBySlugAsync(db, nameKeyedDownloads, ct);

        var nameKeyedJobs = nameKeyedDownloads
            .Select(t =>
            {
                var service = NameKeyedBannerSource.NormalizeService(t.Service);
                var slug = NameKeyedBannerSource.Slug(t.GameName);
                var url = service == NameKeyedBannerSource.XboxService
                    ? (xboxImageUrlBySlug.TryGetValue(slug, out var xboxUrl) ? xboxUrl : null)
                    : NameKeyedBannerSource.TryGetUrl(t.Service, t.GameName);
                return new { Service = service, Slug = slug, Url = url };
            })
            .Where(j => j.Service != null && j.Url != null
                && !steamCoveredSlugs.Contains((j.Service!, j.Slug)))
            .GroupBy(j => (j.Service!, j.Slug))
            .Select(g => g.First())
            .ToList();

        var existingNameKeyedIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.Service == NameKeyedBannerSource.BlizzardService
                || g.Service == NameKeyedBannerSource.RiotService
                || g.Service == NameKeyedBannerSource.XboxService)
            .Select(g => new { g.Service, g.AppId })
            .ToListAsync(ct);

        var existingNameKeyedSet = existingNameKeyedIds
            .Select(g => (g.Service, g.AppId))
            .ToHashSet();

        var missingNameKeyedJobs = nameKeyedJobs
            .Where(j => !existingNameKeyedSet.Contains((j.Service!, j.Slug)))
            .ToList();

        var stored = 0;
        var done = 0;
        foreach (var batch in missingNameKeyedJobs.Chunk(50))
        {
            if (ct.IsCancellationRequested) return null;

            var tasks = batch.Select(job =>
                FetchNameKeyedImageAsync(db, client, job.Service!, job.Slug, job.Url!, ct));

            var added = await Task.WhenAll(tasks);
            var storedInBatch = added.Count(a => a);
            stored += storedInBatch;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            await EmitIncrementalBannerUpdateAsync(storedInBatch);

            done += batch.Length;
            await report(done / (double)missingNameKeyedJobs.Count, done, missingNameKeyedJobs.Count);
        }

        return (missingNameKeyedJobs.Count, stored);
    }

    /// <summary>
    /// Fills in DisplayCatalog banner URLs for any XboxGameMapping that still has none, before the
    /// ImageUrl map is read. The per-resolve fetch (EnsureBannerArtAsync) only runs for the products
    /// resolved in a single pass and never retries a transient miss, so without this an art-less title
    /// (e.g. Minecraft Dungeons) would never get its banner here. Best-effort: a backfill failure must
    /// never abort the image run.
    /// </summary>
    private async Task BackfillXboxBannerUrlsAsync(IServiceProvider scopedServices, CancellationToken ct)
    {
        try
        {
            var xboxMappingService = scopedServices.GetRequiredService<LancacheManager.Core.Services.Xbox.XboxMappingService>();
            await xboxMappingService.BackfillMissingBannerArtAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown - let cancellation propagate rather than logging it as a non-fatal failure.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameImageFetch] Xbox banner URL backfill failed (non-fatal)");
        }
    }

    /// <summary>
    /// Xbox banners are NOT curated/embedded - they are fetched from the Microsoft Store
    /// DisplayCatalog at mapping time and stored on XboxGameMapping.ImageUrl. This reads that
    /// GameName-slug -> ImageUrl map so the name-keyed phase can fetch + store an Xbox GameImage under
    /// (Service = "xbox", AppId = slug), the same way it does for Blizzard/Riot.
    /// </summary>
    private static async Task<Dictionary<string, string>> LoadXboxBannerUrlsBySlugAsync(
        AppDbContext db,
        IReadOnlyList<(string Service, string GameName)> nameKeyedDownloads,
        CancellationToken ct)
    {
        var xboxImageUrlBySlug = new Dictionary<string, string>(StringComparer.Ordinal);
        var xboxGameNames = nameKeyedDownloads
            .Where(t => NameKeyedBannerSource.NormalizeService(t.Service) == NameKeyedBannerSource.XboxService
                        && !string.IsNullOrEmpty(t.GameName))
            .Select(t => t.GameName)
            .Distinct()
            .ToList();
        if (xboxGameNames.Count == 0)
        {
            return xboxImageUrlBySlug;
        }

        var xboxMappings = await db.XboxGameMappings
            .AsNoTracking()
            .Where(m => m.ImageUrl != null && m.ImageUrl != "" && xboxGameNames.Contains(m.Title))
            .Select(m => new { m.Title, m.ImageUrl })
            .ToListAsync(ct);

        foreach (var m in xboxMappings)
        {
            var slug = NameKeyedBannerSource.Slug(m.Title);
            if (!xboxImageUrlBySlug.ContainsKey(slug))
            {
                xboxImageUrlBySlug[slug] = m.ImageUrl!;
            }
        }

        return xboxImageUrlBySlug;
    }

    /// <summary>
    /// Re-fetches stored images older than seven days.
    /// </summary>
    /// <returns>How many images were actually re-stored, or null when cancellation stopped it.</returns>
    private async Task<int?> RefreshStaleImagesAsync(
        AppDbContext db,
        HttpClient client,
        ReportPhaseProgress report,
        CancellationToken ct)
    {
        // Ids first, rows a batch at a time. Every stored image is older than seven days once the
        // service has been up that long, so selecting the rows themselves would hold the whole image
        // table, bytes included, for the length of the phase. The cutoff is read once so the set does
        // not shrink underneath the loop as each batch moves FetchedAtUtc forward.
        var staleCutoff = DateTime.UtcNow.AddDays(-7);
        var staleIds = await db.GameImages
            .AsNoTracking()
            .Where(g => g.FetchedAtUtc < staleCutoff)
            .Select(g => g.Id)
            .ToListAsync(ct);

        var done = 0;
        var refreshed = 0;
        foreach (var batch in staleIds.Chunk(50))
        {
            if (ct.IsCancellationRequested) return null;

            // Loaded inside the loop rather than before it: the tracker is cleared after each save
            // below, so rows read ahead of their own batch would be detached by the time they were
            // edited and their new bytes would never reach the database.
            var images = await db.GameImages
                .Where(g => batch.Contains(g.Id))
                .ToListAsync(ct);

            var tasks = images.Select(image =>
                RefreshImageAsync(client, image, ct));

            var changedInBatch = (await Task.WhenAll(tasks)).Count(changed => changed);
            refreshed += changedInBatch;

            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            // Published only when a batch actually produced different bytes, which is what keeps the
            // routine sweep quiet. EvictMemoryCache cancels the token every cached entry is linked
            // to, so it drops EVERY banner rather than the fifty just handled; a pass over two
            // thousand stale images runs forty batches, and announcing all of them would send every
            // banner on every open page back to the database. Art older than seven days has usually
            // not changed at all, so on a scheduled sweep this stays silent and costs nothing.
            //
            // Clearing the image cache backdates every row into this phase, so that path lands here
            // too, and there an announcement is the whole point: it is what puts genuinely new art on
            // screen while the pass is still running rather than only at the end.
            await EmitIncrementalBannerUpdateAsync(changedInBatch);

            done += batch.Length;
            await report(done / (double)staleIds.Count, done, staleIds.Count);
        }

        return refreshed;
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

    /// <summary>
    /// Builds a Steam CDN image URL from a specific (domain, basePath) endpoint, appId, and file
    /// name. The one place that knows this URL shape - every Steam CDN URL built below routes
    /// through it instead of interpolating the string separately.
    /// </summary>
    private static string GetSteamCdnImageUrl(string domain, string basePath, long appId, string fileName)
        => $"https://{domain}{basePath}/{appId}/{fileName}";

    /// <summary>
    /// Steam header banner URL for an appId, using the first (preferred) Steam CDN endpoint and the
    /// header.jpg file name. Used to record the SourceUrl once a multi-CDN fetch has already
    /// succeeded. That fetch reports neither which of the three endpoints answered nor which file name
    /// it answered with, so BOTH halves of this URL are the preferred guess rather than the truth: an
    /// image that came from the capsule_616x353.jpg pass is still recorded as header.jpg. Whatever
    /// re-reads the stored URL has to be able to fall through when it 404s.
    /// </summary>
    internal static string GetSteamHeaderImageUrl(long appId)
        => GetSteamCdnImageUrl(_steamCdnEndpoints[0].Domain, _steamCdnEndpoints[0].BasePath, appId, "header.jpg");

    /// <summary>
    /// Picks the image URL for an Epic game: Steam's header.jpg when a curated Steam appId exists
    /// for this game's name (Steam-first, wins over Epic's own art), otherwise Epic's own
    /// <paramref name="epicImageUrl"/>. A pure branch so it is testable without a live HTTP call or
    /// the embedded Steam appId map.
    /// </summary>
    internal static string GetEpicImageUrl(long? steamAppId, string epicImageUrl)
        => steamAppId != null
            ? GetSteamHeaderImageUrl(steamAppId.Value)
            : EpicApiDirectClient.EnsureResizeParams(epicImageUrl);

    private async Task<bool> FetchSteamImageAsync(
        AppDbContext db,
        HttpClient client,
        string appId,
        Dictionary<long, string> picsUrlMap,
        Dictionary<long, List<long>> depotOwnerLookup,
        Dictionary<long, List<long>> downloadDepotLookup,
        CancellationToken ct)
    {
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
            return true;
        }

        var parentAppId = FindParentAppId(appId, depotOwnerLookup, downloadDepotLookup);
        if (parentAppId == null)
        {
            _logger.LogDebug("[GameImageFetch] No valid image found for Steam app {AppId} and no parent app found", appId);
            return false;
        }

        _logger.LogInformation("[GameImageFetch] No image for app {AppId}, trying parent app {ParentAppId}", appId, parentAppId);

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
            return true;
        }

        _logger.LogDebug("[GameImageFetch] No valid image found for app {AppId} or parent app {ParentAppId}", appId, parentAppId);
        return false;
    }

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
            var url = GetSteamCdnImageUrl(domain, basePath, appId, "header.jpg");
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
            var url = GetSteamCdnImageUrl(domain, basePath, appId, "capsule_616x353.jpg");
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
                var sourceUrl = GetSteamHeaderImageUrl(appIdForCdn);
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

    private async Task<bool> FetchEpicImageAsync(
        AppDbContext db,
        HttpClient client,
        EpicGameMapping mapping,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(mapping.ImageUrl)) return false;

        // Steam-first: a curated Steam appId for this Epic game's name wins over Epic's own art.
        // The GameImage row below still keys off the Epic appId/service - only the fetched bytes'
        // source changes. Epic is appId-keyed rather than name-keyed, so it looks up the "epic"
        // section directly instead of going through NameKeyedBannerSource.NormalizeService.
        var steamAppId = NameKeyedSteamAppIds.TryGetSteamAppIdForSection("epic", mapping.Name);
        var url = GetEpicImageUrl(steamAppId, mapping.ImageUrl);

        try
        {
            await _httpThrottle.WaitAsync(ct);
            try
            {
                var response = await client.GetAsync(url, ct);
                if (!response.IsSuccessStatusCode) return false;

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);
                if (bytes.Length < MinImageBytes)
                {
                    _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) for Epic {AppId} from {Url}", bytes.Length, mapping.AppId, url);
                    return false;
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
                return true;
            }
            finally
            {
                _httpThrottle.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to fetch Epic image {AppId} from {Url}", mapping.AppId, url);
            return false;
        }
    }

    /// <summary>
    /// Re-fetches one stored image. Returns true when new bytes were stored, which is what tells the
    /// caller the cache generation has to move.
    /// </summary>
    // Overwrites a row's art in place and answers whether the bytes actually changed.
    //
    // FetchedAtUtc always moves, so a row that came back identical is not picked up as stale again on
    // the next pass. UpdatedAtUtc moves only on a real change, because the banner URL is versioned by
    // it: bumping it re-downloads that banner on every open page, and a re-fetch of unchanged art
    // would otherwise cost every client a download per pass for nothing. A row that has never changed
    // has a null UpdatedAtUtc and would fall back to FetchedAtUtc, so pin it before the clock moves.
    private static bool StoreRefreshedImage(GameImage image, byte[] bytes, string contentType)
    {
        var changed = !image.ImageData.AsSpan().SequenceEqual(bytes);

        if (changed)
        {
            image.ImageData = bytes;
            image.ContentType = contentType;
            image.UpdatedAtUtc = DateTime.UtcNow;
        }
        else
        {
            image.UpdatedAtUtc ??= image.FetchedAtUtc;
        }

        image.FetchedAtUtc = DateTime.UtcNow;
        return changed;
    }

    private async Task<bool> RefreshImageAsync(
        HttpClient client,
        GameImage image,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(image.SourceUrl)) return false;

        // Hard-coded embedded banners (embedded://{slug}): re-seed from the embedded JPEG bytes,
        // never the network. These name-keyed banners are NEVER auto-updated over HTTP.
        if (NameKeyedBannerSource.TryGetEmbeddedBytes(image.SourceUrl, out var embeddedBytes, out var embeddedContentType))
        {
            if (embeddedBytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny embedded image ({Size} bytes) during refresh for {AppId}", embeddedBytes.Length, image.AppId);
                return false;
            }

            return StoreRefreshedImage(image, embeddedBytes, embeddedContentType);
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
                        image.SourceUrl = GetSteamHeaderImageUrl(appIdLong);
                        return StoreRefreshedImage(image, cdnBytes, "image/jpeg");
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

            if (!response.IsSuccessStatusCode) return false;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length < MinImageBytes)
            {
                _logger.LogDebug("[GameImageFetch] Skipping tiny image ({Size} bytes) during refresh for {AppId}", bytes.Length, image.AppId);
                return false;
            }

            return StoreRefreshedImage(
                image, bytes, response.Content.Headers.ContentType?.MediaType ?? "image/jpeg");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[GameImageFetch] Failed to refresh image {AppId}", image.AppId);
            return false;
        }
    }
}
