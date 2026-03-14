namespace LancacheManager.Extensions;

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
}
