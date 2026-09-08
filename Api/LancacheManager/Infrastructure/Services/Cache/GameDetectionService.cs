using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service that runs game cache detection on a user-configurable interval.
/// Whether it also runs at startup is controlled by the Schedules UI
/// (persisted in IStateService.GetServiceRunOnStartup) - defaults to false.
/// Game detection is never triggered by other services (depot mapping, Epic refresh, etc.).
/// </summary>
public class GameDetectionService : ScheduledBackgroundService
{
    private readonly GameCacheDetectionService _detectionService;
    private readonly IStateService _stateService;
    private readonly IPathResolver _pathResolver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CacheReconciliationService _cacheReconciliationService;
    private readonly IOperationQueue _operationQueue;

    public GameDetectionService(
        GameCacheDetectionService detectionService,
        IStateService stateService,
        IPathResolver pathResolver,
        IServiceScopeFactory scopeFactory,
        CacheReconciliationService cacheReconciliationService,
        IOperationQueue operationQueue,
        ILogger<GameDetectionService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _detectionService = detectionService;
        _stateService = stateService;
        _pathResolver = pathResolver;
        _scopeFactory = scopeFactory;
        _cacheReconciliationService = cacheReconciliationService;
        _operationQueue = operationQueue;

        LoadStateOverrides(stateService);
    }

    /// <summary>
    /// Route automatic detection through the same heavy-operation queue as manual requests.
    /// This closes the conflict-check/start race and gives a blocked scheduled run a cancellable
    /// purple waiting card in the universal notification menu instead of silently skipping it.
    /// </summary>
    private async Task QueueDetectionAsync(string runKind, CancellationToken ct)
    {
        // Stamp the run-stable display flag from the effective mode and the trigger that produced this
        // run; the detection service carries it verbatim through every lifecycle event.
        var notice = CurrentRunNotice;
        var showNotification = notice.ShowNotification;

        // Resolve the run once. Hybrid picks incremental or full from the clock, so asking twice could
        // log one scan and start the other. Read at the top of each run rather than held on this
        // instance, so a mode saved while a run is in flight takes effect on the next run and not
        // partway through this one.
        var incremental = _stateService.GetGameDetectionScanMode().IsIncremental(
            _stateService.GetGameDetectionLastFullScan(), DateTime.UtcNow);

        // Reported where the mode is resolved rather than beside the outcome below. A run the
        // detection service refuses - a datasource whose cache-key scheme cannot be determined -
        // throws out of EnqueueAsync, so the outcome line never runs and the scan a refused run
        // would have performed was the one thing the log could not answer.
        _logger.LogInformation(
            "[GameDetection] {RunKind} run resolved to the {ScanMode} scan",
            runKind,
            incremental ? "incremental" : "full");

        // Exceptions must PROPAGATE to the queue: a thrown start (capability denial) is a
        // permanent refusal for this run, and the queue fails the waiting card immediately with
        // the real reason. Mapping it to null instead re-labels it as the transient
        // gate-still-unwinding signal, which the queue retries for 30 seconds while the card
        // shows a stale blocker, before failing with an unrelated "start gate" error. A null
        // return stays reserved for the genuinely transient case (a detection already active).
        Task<Guid?> StartDetectionAsync() =>
            _detectionService.StartDetectionAsync(incremental: incremental, showNotification: showNotification, notice: notice);

        var outcome = await _operationQueue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            StartDetectionAsync,
            ct,
            reportRefusal: true,
            showWaitingCard: showNotification,
            notice: notice);

        var disposition = outcome.Queued
            ? "queued"
            : outcome.AlreadyRunning
                ? "already requested"
                : "started";
        _logger.LogInformation(
            "[GameDetection] {RunKind} detection {Disposition} (operation: {OperationId})",
            runKind,
            disposition,
            outcome.OperationId);
    }

    protected override string ServiceName => "GameDetection";

    protected override bool SupportsNotifications => true;

    public override bool DefaultRunOnStartup => false;

    protected override TimeSpan StartupDelay => TimeSpan.Zero;

    protected override TimeSpan Interval => TimeSpan.FromHours(6);

    public override string ServiceKey => "gameDetection";

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Check for required binary upfront before waiting for setup
            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();
            if (!File.Exists(rustBinaryPath))
            {
                _logger.LogWarning("[GameDetection] Game detection binary not found at {Path}, game detection disabled", rustBinaryPath);
                return;
            }

            // Wait for setup/initialization to complete before running detection.
            // Uses async signaling - resumes instantly when setup completes, no polling.
            _logger.LogInformation("[GameDetection] Waiting for setup to complete...");
            await _stateService.WaitForSetupCompletedAsync(stoppingToken);
            _logger.LogInformation("[GameDetection] Setup completed");

            // Wait for log processing to complete before running detection.
            // Detection needs LogEntries in the database to map games to downloads.
            _logger.LogInformation("[GameDetection] Waiting for logs to be processed...");
            await _stateService.WaitForLogsProcessedAsync(stoppingToken);
            _logger.LogInformation("[GameDetection] Logs processed");

            // Wait for CacheReconciliationService to complete its first startup eviction scan.
            // This ensures evicted games are upserted into CachedGameDetections BEFORE we read from it,
            // so GetCachedDetectionAsync sees a consistent state even in "Remove" eviction mode.
            _logger.LogInformation("[GameDetection] Waiting for CacheReconciliationService first startup scan to complete...");
            var completionTask = _cacheReconciliationService.FirstStartupScanComplete;
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            if (await Task.WhenAny(completionTask, timeoutTask) == timeoutTask)
            {
                _logger.LogWarning("[GameDetection] Timed out waiting for CacheReconciliationService first scan; proceeding anyway");
            }
            else
            {
                _logger.LogInformation("[GameDetection] CacheReconciliationService first scan complete");
            }

            // Skip detection if there are no downloads in the database yet
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (!await context.Downloads.AnyAsync(stoppingToken))
            {
                _logger.LogInformation("[GameDetection] No downloads in database, skipping startup detection scan");
                return;
            }

            // Run recovery, self-healing, and cleanup ONCE on startup - not on every dashboard load.
            _logger.LogInformation("[GameDetection] Running detection data reconciliation...");
            await _detectionService.ReconcileDetectionDataAsync();

            // Gate on persisted detection rows, not a non-null GetCachedDetectionAsync() response.
            // LoadDetectionAsync may return response-only partial-eviction synthetics with no
            // CachedGameDetection/CachedServiceDetection rows; those must not suppress a real scan.
            var hasPersistedDetections =
                await context.CachedGameDetections.AnyAsync(stoppingToken)
                || await context.CachedServiceDetections.AnyAsync(stoppingToken);
            if (hasPersistedDetections)
            {
                _logger.LogInformation("[GameDetection] Game detection data already cached, skipping startup scan");
                // Warm the in-memory detection cache for subsequent dashboard reads.
                await _detectionService.GetCachedDetectionAsync();
                return;
            }

            _logger.LogInformation("[GameDetection] No cached game detection data found, requesting a detection scan");
            await QueueDetectionAsync("Startup", stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GameDetection] Cancelled during startup wait");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Error during startup game detection scan");
        }
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.LogInformation("[GameDetection] Requesting scheduled game detection scan");
            await QueueDetectionAsync("Scheduled", stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Error during scheduled game detection scan");
        }
    }
}
