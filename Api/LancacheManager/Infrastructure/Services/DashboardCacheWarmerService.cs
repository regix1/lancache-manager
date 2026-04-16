using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Base;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Warms the IMemoryCache entry behind <c>/api/dashboard/batch</c> on startup and at a periodic
/// interval thereafter. Without this, the FIRST user request after a server restart would run
/// nine parallel DB queries against a cold connection pool — often 10s+ or a gateway timeout on
/// resource-constrained hosts. Running the compute in the background decouples user latency
/// from the pool warm-up.
/// </summary>
public class DashboardCacheWarmerService : ScheduledBackgroundService
{
    private readonly IDashboardBatchService _batchService;

    public override string ServiceKey => "dashboardCacheWarmer";
    public override bool DefaultRunOnStartup => true;

    protected override string ServiceName => "DashboardCacheWarmer";
    protected override TimeSpan StartupDelay => TimeSpan.FromSeconds(5);
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public DashboardCacheWarmerService(
        IDashboardBatchService batchService,
        IStateService stateService,
        IConfiguration configuration,
        ILogger<DashboardCacheWarmerService> logger)
        : base(logger, configuration)
    {
        _batchService = batchService;
        LoadStateOverrides(stateService);
    }

    protected override Task OnStartupAsync(CancellationToken stoppingToken)
        => WarmAsync(stoppingToken);

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => WarmAsync(stoppingToken);

    private async Task WarmAsync(CancellationToken ct)
    {
        try
        {
            var started = DateTime.UtcNow;
            _ = await _batchService.GetBatchAsync(null, null, null, ct);
            var elapsed = DateTime.UtcNow - started;
            _logger.LogInformation(
                "Dashboard batch cache warmed (live view, no event filter) in {ElapsedMs} ms",
                (long)elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException)
        {
            // Shutdown — let the loop exit normally
            throw;
        }
        catch (Exception ex)
        {
            // Warming failure must never crash startup or the background loop.
            _logger.LogWarning(ex, "Dashboard batch cache warm failed — will retry on next interval");
        }
    }
}
