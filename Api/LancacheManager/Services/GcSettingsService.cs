using System.Text.Json;

namespace LancacheManager.Services;

public enum GcAggressiveness
{
    OnPageLoad,
    Low,
    Medium,
    High,
    VeryHigh
}

public class GcSettings
{
    public GcAggressiveness Aggressiveness { get; set; } = GcAggressiveness.OnPageLoad;
    public long MemoryThresholdMB { get; set; } = 3072; // 3GB default
}

public class GcSettingsService
{
    private readonly ILogger<GcSettingsService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _settingsFilePath;
    private GcSettings _currentSettings;
    private static readonly object _lock = new object();

    public GcSettingsService(ILogger<GcSettingsService> logger, IPathResolver pathResolver)
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

    public (long thresholdBytes, TimeSpan minTimeBetweenChecks, bool onPageLoadOnly) GetComputedSettings()
    {
        var settings = GetSettings();
        var thresholdBytes = settings.MemoryThresholdMB * 1024L * 1024L;
        var onPageLoadOnly = settings.Aggressiveness == GcAggressiveness.OnPageLoad;

        var minTimeBetweenChecks = settings.Aggressiveness switch
        {
            GcAggressiveness.OnPageLoad => TimeSpan.FromSeconds(5), // Cooldown period to prevent spam
            GcAggressiveness.Low => TimeSpan.FromSeconds(5),
            GcAggressiveness.Medium => TimeSpan.FromSeconds(2),
            GcAggressiveness.High => TimeSpan.FromSeconds(1),
            GcAggressiveness.VeryHigh => TimeSpan.FromMilliseconds(500),
            _ => TimeSpan.FromSeconds(2)
        };

        return (thresholdBytes, minTimeBetweenChecks, onPageLoadOnly);
    }
}
