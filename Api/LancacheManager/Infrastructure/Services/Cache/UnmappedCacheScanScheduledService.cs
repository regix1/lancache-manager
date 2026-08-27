using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Scheduled service that refreshes the stored unmapped cache scan, the list of cache files no
/// game or service detection row claims.
/// </summary>
public class UnmappedCacheScanScheduledService : ScheduledBackgroundService
{
    private readonly UnmappedCacheService _unmappedCacheService;
    private readonly IOperationQueue _operationQueue;
    private readonly TimeSpan _defaultInterval;

    protected override string ServiceName => "UnmappedCacheScan";
    protected override TimeSpan Interval => _defaultInterval;

    public override bool DefaultRunOnStartup => false;
    public override string ServiceKey => "unmappedCacheScan";

    public UnmappedCacheScanScheduledService(
        UnmappedCacheService unmappedCacheService,
        IOperationQueue operationQueue,
        IStateService stateService,
        ILogger<UnmappedCacheScanScheduledService> logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _unmappedCacheService = unmappedCacheService;
        _operationQueue = operationQueue;
        _defaultInterval = TimeSpan.FromHours(configuration.GetValue("UnmappedCacheScan:IntervalHours", 24));
        LoadStateOverrides(stateService);
    }

    protected override async Task OnStartupAsync(CancellationToken stoppingToken)
    {
        await ScanAsync("Startup", stoppingToken);
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        await ScanAsync("Scheduled", stoppingToken);
    }

    private async Task ScanAsync(string trigger, CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();

        try
        {
            // Deliberately no stopping token in the delegate: it may run at queue promotion, long
            // after this call returned (the operation owns its own CTS via the tracker).
            async Task<Guid?> StartScanAsync() => await _unmappedCacheService.StartScanAsync();

            // Enqueueing rather than starting directly is what runs this walk through the same
            // conflict checker the manual trigger uses: the queue re-checks under its gate and
            // either starts now or parks the run behind the operation blocking it.
            var outcome = await _operationQueue.EnqueueAsync(
                OperationType.UnmappedCacheScan,
                ConflictScope.Bulk(),
                UnmappedCacheService.ScanOperationName,
                StartScanAsync,
                stoppingToken);

            if (outcome.Queued)
            {
                _logger.LogInformation(
                    "[UnmappedCacheScan] Unmapped cache scan queued (trigger: {Trigger}, waiting operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
            else if (outcome.AlreadyRunning)
            {
                _logger.LogInformation(
                    "[UnmappedCacheScan] Unmapped cache scan already requested (trigger: {Trigger}, operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
            else
            {
                _logger.LogInformation(
                    "[UnmappedCacheScan] Unmapped cache scan started (trigger: {Trigger}, operation: {OperationId})",
                    trigger,
                    outcome.OperationId);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("[UnmappedCacheScan] Scan request cancelled (trigger: {Trigger})", trigger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[UnmappedCacheScan] Unmapped cache scan failed (trigger: {Trigger})", trigger);
        }
    }
}
