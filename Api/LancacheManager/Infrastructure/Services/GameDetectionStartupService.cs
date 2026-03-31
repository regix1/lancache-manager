using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Infrastructure.Services;

public class GameDetectionStartupService : ScheduledBackgroundService
{
    private readonly GameCacheDetectionService _detectionService;

    public GameDetectionStartupService(
        GameCacheDetectionService detectionService,
        ILogger<GameDetectionStartupService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _detectionService = detectionService;
    }

    protected override string ServiceName => "GameDetectionStartup";

    protected override bool RunOnStartup => true;

    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(30);

    protected override TimeSpan Interval => Timeout.InfiniteTimeSpan;

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        try
        {
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

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => Task.CompletedTask;
}
