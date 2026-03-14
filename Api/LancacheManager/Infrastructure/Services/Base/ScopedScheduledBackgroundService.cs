namespace LancacheManager.Infrastructure.Services.Base;

/// <summary>
/// Base class for background services that need a scoped service provider
/// (e.g., for database context access).
/// </summary>
public abstract class ScopedScheduledBackgroundService : ScheduledBackgroundService
{
    protected readonly IServiceProvider _serviceProvider;

    protected ScopedScheduledBackgroundService(
        IServiceProvider serviceProvider,
        ILogger logger,
        IConfiguration configuration)
        : base(logger, configuration)
    {
        _serviceProvider = serviceProvider;
    }

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        await ExecuteScopedWorkAsync(scope.ServiceProvider, stoppingToken);
    }

    /// <summary>
    /// Execute work with access to scoped services.
    /// </summary>
    protected abstract Task ExecuteScopedWorkAsync(
        IServiceProvider scopedServices,
        CancellationToken stoppingToken);
}
