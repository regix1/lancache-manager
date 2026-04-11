using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.Base;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service that runs game cache detection on a user-configurable interval.
/// Whether it also runs at startup is controlled by the user via the Schedules UI
/// (persisted in IStateService.GetServiceRunOnStartup) — defaults to true so that
/// existing installs continue to seed the detection cache on first boot.
/// </summary>
public class GameDetectionService : ScheduledBackgroundService
{
    private readonly GameCacheDetectionService _detectionService;
    private readonly IStateService _stateService;
    private readonly IPathResolver _pathResolver;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CacheReconciliationService _cacheReconciliationService;

    public GameDetectionService(
        GameCacheDetectionService detectionService,
        IStateService stateService,
        IPathResolver pathResolver,
        IServiceScopeFactory scopeFactory,
        CacheReconciliationService cacheReconciliationService,
        ILogger<GameDetectionService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _detectionService = detectionService;
        _stateService = stateService;
        _pathResolver = pathResolver;
        _scopeFactory = scopeFactory;
        _cacheReconciliationService = cacheReconciliationService;

        LoadStateOverrides(stateService);
    }

    protected override string ServiceName => "GameDetection";

    public override bool DefaultRunOnStartup => true;

    protected override TimeSpan StartupDelay => TimeSpan.Zero;

    protected override TimeSpan Interval => TimeSpan.FromDays(30);

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
            // Uses async signaling — resumes instantly when setup completes, no polling.
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

            // Run recovery, self-healing, and cleanup ONCE on startup — not on every dashboard load.
            _logger.LogInformation("[GameDetection] Running detection data reconciliation...");
            await _detectionService.ReconcileCachedDetectionDataAsync();

            var cached = await _detectionService.GetCachedDetectionAsync();
            if (cached != null)
            {
                _logger.LogInformation("[GameDetection] Game detection data already cached, skipping startup scan");
                return;
            }

            _logger.LogInformation("[GameDetection] No cached game detection data found, starting incremental detection scan");
            await _detectionService.StartDetectionAsync(incremental: true);
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
            _logger.LogInformation("[GameDetection] Running scheduled game detection scan");
            await _detectionService.StartDetectionAsync(incremental: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Error during scheduled game detection scan");
        }
    }
}
