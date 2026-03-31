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

    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(30);

    protected override TimeSpan Interval => Timeout.InfiniteTimeSpan;

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Wait for setup/initialization to complete before running detection.
            // The setup wizard must finish first so the cache paths and configuration
            // are available for the detection scan.
            if (!await WaitForSetupAsync(stoppingToken))
            {
                _logger.LogInformation("[GameDetectionStartup] Setup not completed within timeout, skipping startup scan");
                return;
            }

            var cached = await _detectionService.GetCachedDetectionAsync();
            if (cached != null)
            {
                _logger.LogInformation("[GameDetectionStartup] Game detection data already cached, skipping startup scan");
                return;
            }

            _logger.LogInformation("[GameDetectionStartup] No cached game detection data found, starting incremental detection scan");
            await _detectionService.StartDetectionAsync(incremental: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetectionStartup] Error during startup game detection scan");
        }
    }

    private async Task<bool> WaitForSetupAsync(CancellationToken stoppingToken)
    {
        if (_stateService.GetSetupCompleted())
            return true;

        _logger.LogInformation("[GameDetectionStartup] Waiting for setup to complete before running detection...");

        // Poll every 5 seconds for up to 5 minutes
        const int maxAttempts = 60;
        for (var i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

            if (_stateService.GetSetupCompleted())
            {
                _logger.LogInformation("[GameDetectionStartup] Setup completed, proceeding with detection");
                return true;
            }
        }

        return false;
    }

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;
}
