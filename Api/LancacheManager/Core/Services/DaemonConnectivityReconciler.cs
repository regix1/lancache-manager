namespace LancacheManager.Core.Services;

/// <summary>
/// Periodically re-reports Battle.net/Riot Docker connectivity into the unified activity registry.
/// <see cref="PrefillDaemonServiceBase.ReportPlatformActivityAsync"/> otherwise only runs as a side
/// effect of a daemon session's presence changing (see ReportSessionActivityAsync) - Docker itself can go
/// down or come back with zero session activity happening, and nothing else re-checks it. Without this,
/// the registry's integration/{battlenet,riot}/connected entry can sit on a stale value indefinitely once
/// no session runs to trigger a refresh - and since the frontend ORs it with its own REST fallback, a
/// stale-true registry entry can outrank a freshly-fetched false value.
///
/// A plain BackgroundService on purpose, not ScheduledBackgroundService/ConfigurableScheduledService:
/// ServiceScheduleRegistry auto-registers any IHostedService of either of those types as a user-facing
/// card on the Schedules page, which is wrong for an internal reconciliation detail.
/// </summary>
public class DaemonConnectivityReconciler : BackgroundService
{
    private static readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    private readonly BattleNetDaemonService _battleNetDaemon;
    private readonly RiotDaemonService _riotDaemon;
    private readonly ILogger<DaemonConnectivityReconciler> _logger;

    public DaemonConnectivityReconciler(
        BattleNetDaemonService battleNetDaemon,
        RiotDaemonService riotDaemon,
        ILogger<DaemonConnectivityReconciler> logger)
    {
        _battleNetDaemon = battleNetDaemon;
        _riotDaemon = riotDaemon;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _battleNetDaemon.ReportPlatformActivityAsync();
                await _riotDaemon.ReportPlatformActivityAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile Battle.net/Riot connectivity activity");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
