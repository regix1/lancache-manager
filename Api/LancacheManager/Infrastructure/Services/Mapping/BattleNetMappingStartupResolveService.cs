using LancacheManager.Core.Services.BattleNet;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// One-shot startup pass of <see cref="BattleNetMappingService.ResolveDownloadsAsync"/>.
/// The Blizzard TACT catalog is compiled into the binary, so its contents only ever change
/// when a new build ships; without this pass, downloads left unnamed by an older catalog
/// stay unnamed until the next log-processing batch happens to run (indefinitely on a quiet
/// cache). Running the resolve once after boot makes a catalog update take effect
/// immediately. The post-ingest resolve in RustLogProcessorService remains the steady-state
/// path; this covers only the catalog-changed-between-runs window.
/// </summary>
public class BattleNetMappingStartupResolveService : BackgroundService
{
    /// <summary>Lets migrations and DB warmup settle before the first query.</summary>
    private static readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(30);

    private readonly BattleNetMappingService _mappingService;
    private readonly ILogger<BattleNetMappingStartupResolveService> _logger;

    public BattleNetMappingStartupResolveService(
        BattleNetMappingService mappingService,
        ILogger<BattleNetMappingStartupResolveService> logger)
    {
        _mappingService = mappingService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
            var resolved = await _mappingService.ResolveDownloadsAsync(stoppingToken);
            if (resolved > 0)
            {
                _logger.LogInformation(
                    "Startup Blizzard mapping pass resolved {Count} downloads left unnamed by an older catalog",
                    resolved);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Startup Blizzard mapping pass cancelled by shutdown");
        }
        catch (Exception ex)
        {
            // Best-effort backfill: the post-ingest resolve covers the same rows on the next
            // processed batch, so a startup failure must never take the app down.
            _logger.LogWarning(ex, "Startup Blizzard mapping pass failed (non-fatal)");
        }
    }
}
