using Microsoft.Extensions.Configuration;

namespace LancacheManager.Infrastructure.Extensions;

/// <summary>
/// Extension methods for IConfiguration
/// </summary>
public static class ConfigurationExtensions
{
    /// <summary>
    /// Gets a required configuration value, throwing if not found.
    /// </summary>
    public static T GetRequired<T>(this IConfiguration configuration, string key)
    {
        var value = configuration.GetValue<T>(key);
        if (value is null)
        {
            throw new InvalidOperationException($"Configuration key '{key}' is required but was not found.");
        }
        return value;
    }

    /// <summary>
    /// Gets a configuration section and binds it to a type.
    /// </summary>
    public static T GetSection<T>(this IConfiguration configuration, string sectionName) where T : new()
    {
        var section = new T();
        configuration.GetSection(sectionName).Bind(section);
        return section;
    }

    /// <summary>
    /// Gets a connection string or throws if not found.
    /// </summary>
    public static string GetRequiredConnectionString(this IConfiguration configuration, string name)
    {
        return configuration.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is required but was not found.");
    }

    /// <summary>
    /// Gets a configuration value with a fallback chain.
    /// </summary>
    public static T GetValueOrFallback<T>(this IConfiguration configuration, T fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration.GetValue<T>(key);
            if (value is not null)
            {
                return value;
            }
        }
        return fallback;
    }
}
