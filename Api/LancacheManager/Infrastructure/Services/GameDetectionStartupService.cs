using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Infrastructure.Services;

public class GameDetectionStartupService : ScheduledBackgroundService
{
    private readonly GameCacheDetectionService _detectionService;
    private readonly IStateService _stateService;

    public GameDetectionStartupService(
        GameCacheDetectionService detectionService,
        IStateService stateService,
        ILogger<GameDetectionStartupService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _detectionService = detectionService;
        _stateService = stateService;
    }

    protected override string ServiceName => "GameDetectionStartup";

    protected override bool RunOnStartup => true;

    protected override TimeSpan StartupDelay => TimeSpan.Zero;

    protected override TimeSpan Interval => Timeout.InfiniteTimeSpan;

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for setup/initialization to complete before running detection.
            // Uses async signaling — resumes instantly when setup completes, no polling.
            _logger.LogInformation("[GameDetectionStartup] Waiting for setup to complete...");
            await _stateService.WaitForSetupCompletedAsync(stoppingToken);
            _logger.LogInformation("[GameDetectionStartup] Setup completed");

            // Wait for log processing to complete before running detection.
            // Detection needs LogEntries in the database to map games to downloads.
            _logger.LogInformation("[GameDetectionStartup] Waiting for logs to be processed...");
            await _stateService.WaitForLogsProcessedAsync(stoppingToken);
            _logger.LogInformation("[GameDetectionStartup] Logs processed");

            var cached = await _detectionService.GetCachedDetectionAsync();
            if (cached != null)
            {
                _logger.LogInformation("[GameDetectionStartup] Game detection data already cached, skipping startup scan");
                return;
            }

            _logger.LogInformation("[GameDetectionStartup] No cached game detection data found, starting incremental detection scan");
            await _detectionService.StartDetectionAsync(incremental: true);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GameDetectionStartup] Cancelled during startup wait");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetectionStartup] Error during startup game detection scan");
        }
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;
}
