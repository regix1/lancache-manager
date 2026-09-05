namespace LancacheManager.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers a class as both a singleton and a hosted service.
    /// This replaces the two-line pattern of AddSingleton + AddHostedService with GetRequiredService.
    /// </summary>
    public static IServiceCollection AddSingletonHostedService<T>(this IServiceCollection services)
        where T : class, IHostedService
    {
        services.AddSingleton<T>();
        services.AddHostedService(provider => provider.GetRequiredService<T>());
        return services;
    }

    /// <summary>
    /// Registers a class as a singleton, and starts it as a hosted service only when there is a
    /// database to work against.
    ///
    /// A setup-only boot skips migrations, so the schema does not exist either and every tick of a
    /// database-backed service is a failed query logged at Error level. The scheduled-service loops
    /// then retry on ErrorRetryDelay rather than the service's own interval, so a six-hour job
    /// starts failing once a minute and buries the one log line telling the operator to submit
    /// credentials and restart. The connection retry policy multiplies each of those into several
    /// attempts against a server that is absent or refusing the credentials.
    ///
    /// The singleton is registered either way, so controllers that resolve one of these still work;
    /// only the hosted registration that starts the loop is withheld. Note that a service withheld
    /// here is also absent from the IEnumerable&lt;IHostedService&gt; that ServiceScheduleRegistry is
    /// built from, so it does not appear on the Schedules page until the restart that completing
    /// setup already requires.
    ///
    /// Isolated installations can set LANCACHE_MANAGER_DISABLE_BACKGROUND_SERVICES=true
    /// to use a real database without contacting daemon containers or scheduled integrations.
    /// </summary>
    public static IServiceCollection AddDatabaseBackedHostedService<T>(
        this IServiceCollection services,
        bool databaseAvailable)
        where T : class, IHostedService
    {
        services.AddSingleton<T>();

        if (databaseAvailable
            && !string.Equals(
                Environment.GetEnvironmentVariable("LANCACHE_MANAGER_DISABLE_BACKGROUND_SERVICES"),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService(provider => provider.GetRequiredService<T>());
        }

        return services;
    }
}
