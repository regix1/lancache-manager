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
        _settingsFilePath = _pathResolver.GetSettingsPath("gc-settings.json");
        var settingsDir = Path.GetDirectoryName(_settingsFilePath);
        if (!string.IsNullOrEmpty(settingsDir) && !Directory.Exists(settingsDir))
        {
            Directory.CreateDirectory(settingsDir);
        }
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

        _logger.LogInformation("GC settings updated: Enabled={Enabled}, ThresholdMB={ThresholdMB}",
            newSettings.Enabled, newSettings.MemoryThresholdMB);

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
                    // One-time legacy migration: pre-Option-B gc-settings.json files store
                    // Aggressiveness only. If we find such a file (Enabled default-false but
                    // a non-Disabled legacy Aggressiveness), flip Enabled=true and persist.
                    // Idempotent on re-read because after save the legacy field is serialized
                    // alongside Enabled=true and the branch condition is false next time.
#pragma warning disable CS0618 // Type or member is obsolete — migration path only
                    if (!settings.Enabled && settings.Aggressiveness != GcAggressiveness.Disabled)
                    {
                        _logger.LogInformation(
                            "Migrating legacy GC settings: Aggressiveness={Aggressiveness} -> Enabled=true, ThresholdMB={ThresholdMB}",
                            settings.Aggressiveness, settings.MemoryThresholdMB);
                        settings.Enabled = true;
                        // Persist migrated shape synchronously so subsequent reads see the
                        // normalized form. We're inside the ctor path, so fire-and-forget
                        // with .GetAwaiter().GetResult() is acceptable here.
                        try
                        {
                            SaveSettingsAsync(settings).GetAwaiter().GetResult();
                        }
                        catch (Exception saveEx)
                        {
                            _logger.LogWarning(saveEx, "Failed to persist migrated GC settings; will retry on next update");
                        }
                    }
#pragma warning restore CS0618

                    _logger.LogInformation("Loaded GC settings: Enabled={Enabled}, ThresholdMB={ThresholdMB}",
                        settings.Enabled, settings.MemoryThresholdMB);
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
}
