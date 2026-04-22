using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Core.Services;

/// <summary>
/// Periodically removes completed and expired cache clearing operation records
/// that are older than 24 hours. This is a housekeeping service — actual cache
/// clearing is triggered manually from the Cache Management page.
/// </summary>
public class OperationHistoryCleanupService : ScheduledBackgroundService
{
    private readonly IStateService _stateService;

    protected override string ServiceName => "OperationHistoryCleanupService";
    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);
    public override bool DefaultRunOnStartup => true;
    public override string ServiceKey => "operationHistoryCleanup";

    public OperationHistoryCleanupService(
        ILogger<OperationHistoryCleanupService> logger,
        IConfiguration configuration,
        IStateService stateService)
        : base(logger, configuration)
    {
        _stateService = stateService;
        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
        => ExecuteWorkAsync(stoppingToken);

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        CleanupOldOperations();
        return Task.CompletedTask;
    }

    private void CleanupOldOperations()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);

            var stateOps = _stateService.GetCacheClearOperations().ToList();
            var toRemove = stateOps
                .Where(op => op.EndTime.HasValue && op.EndTime.Value < cutoff)
                .Select(op => op.Id)
                .ToList();

            if (toRemove.Count > 0)
            {
                foreach (var id in toRemove)
                {
                    _stateService.RemoveCacheClearOperation(id);
                }
                _logger.LogDebug("Cleaned up {Count} old cache clear operations from state", toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old operations");
        }
    }
}
