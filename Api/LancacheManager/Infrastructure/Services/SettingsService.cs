using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Interfaces;

namespace LancacheManager.Infrastructure.Services;

public class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _settingsFilePath;
    private GcSettings _currentSettings;
    private static readonly object _lock = new object();

    public SettingsService(ILogger<SettingsService> logger, IPathResolver pathResolver)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _settingsFilePath = Path.Combine(_pathResolver.GetDataDirectory(), "gc-settings.json");
        _currentSettings = LoadSettings();
    }

    public GcSettings GetSettings()
    {
        lock (_lock)
        {
            return _currentSettings;
        }
    }

    public async Task<GcSettings> UpdateSettingsAsync(GcSettings newSettings)
    {
        lock (_lock)
        {
            // Validate settings
            if (newSettings.MemoryThresholdMB < 512)
            {
                throw new ArgumentException("Memory threshold must be at least 512MB");
            }

            if (newSettings.MemoryThresholdMB > 32768)
            {
                throw new ArgumentException("Memory threshold must not exceed 32GB");
            }

            _currentSettings = newSettings;
        }

        // Save to file
        await SaveSettingsAsync(newSettings);

        _logger.LogInformation("GC settings updated: Aggressiveness={Aggressiveness}, ThresholdMB={ThresholdMB}",
            newSettings.Aggressiveness, newSettings.MemoryThresholdMB);

        return newSettings;
    }

    private GcSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<GcSettings>(json);
                if (settings != null)
                {
                    _logger.LogInformation("Loaded GC settings: Aggressiveness={Aggressiveness}, ThresholdMB={ThresholdMB}",
                        settings.Aggressiveness, settings.MemoryThresholdMB);
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load GC settings, using defaults");
        }

        return new GcSettings();
    }

    private async Task SaveSettingsAsync(GcSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_settingsFilePath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save GC settings");
            throw;
        }
    }

    public (long thresholdBytes, TimeSpan minTimeBetweenChecks, bool onPageLoadOnly, bool disabled) GetComputedSettings()
    {
        var settings = GetSettings();
        var thresholdBytes = settings.MemoryThresholdMB * 1024L * 1024L;
        var onPageLoadOnly = settings.Aggressiveness == GcAggressiveness.OnPageLoad;
        var disabled = settings.Aggressiveness == GcAggressiveness.Disabled;

        var minTimeBetweenChecks = settings.Aggressiveness switch
        {
            GcAggressiveness.Disabled => TimeSpan.MaxValue,
            GcAggressiveness.OnPageLoad => TimeSpan.FromSeconds(5), // Cooldown period to prevent spam
            GcAggressiveness.Every60Minutes => TimeSpan.FromMinutes(60),
            GcAggressiveness.Every60Seconds => TimeSpan.FromSeconds(60),
            GcAggressiveness.Every30Seconds => TimeSpan.FromSeconds(30),
            GcAggressiveness.Every10Seconds => TimeSpan.FromSeconds(10),
            GcAggressiveness.Every5Seconds => TimeSpan.FromSeconds(5),
            GcAggressiveness.Every1Second => TimeSpan.FromSeconds(1),
            _ => TimeSpan.FromSeconds(5)
        };

        return (thresholdBytes, minTimeBetweenChecks, onPageLoadOnly, disabled);
    }
}
