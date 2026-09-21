using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

public sealed class PersistentContainerImageService : ScheduledBackgroundService
{
    private readonly IServiceScopeFactory _scopes;

    public PersistentContainerImageService(
        ILogger<PersistentContainerImageService> logger,
        IConfiguration configuration,
        IServiceScopeFactory scopes)
        : base(logger, configuration)
    {
        _scopes = scopes;
    }

    protected override string ServiceName => "PersistentContainerImage";

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    public override string ServiceKey => "persistentContainerImage";

    protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        => RunOnceAsync(stoppingToken);

    internal async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopes.CreateScope();
        var services = scope.ServiceProvider;
        var tasks = Enum.GetValues<PrefillPlatform>()
            .Select(platform => ProcessPlatformAsync(services, platform, stoppingToken))
            .ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task ProcessPlatformAsync(
        IServiceProvider services,
        PrefillPlatform platform,
        CancellationToken stoppingToken)
    {
        stoppingToken.ThrowIfCancellationRequested();
        try
        {
            var daemon = PrefillDaemonServiceBase.ResolveDaemon(services, platform);
            if (daemon is not null)
            {
                await daemon.ReconcilePersistentImageAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Persistent container image reconciliation failed for platform {Platform}",
                platform);
        }
    }
}
