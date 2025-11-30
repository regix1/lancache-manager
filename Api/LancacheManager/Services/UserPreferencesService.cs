using LancacheManager.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LancacheManager.Services;

public class UserPreferencesService
{
    private readonly ILogger<UserPreferencesService> _logger;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;

    public UserPreferencesService(ILogger<UserPreferencesService> logger, IDbContextFactory<AppDbContext> contextFactory)
    {
        _logger = logger;
        _contextFactory = contextFactory;
    }

    public class UserPreferencesDto
    {
        public string? SelectedTheme { get; set; }
        public bool SharpCorners { get; set; }
        public bool DisableFocusOutlines { get; set; }
        public bool DisableTooltips { get; set; }
        public bool PicsAlwaysVisible { get; set; }
        public bool DisableStickyNotifications { get; set; }
        public bool UseLocalTimezone { get; set; }
    }

    /// <summary>
    /// Get user preferences for a device
    /// </summary>
    public UserPreferencesDto? GetPreferences(string deviceId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.DeviceId == deviceId);

            if (preferences != null)
            {
                return new UserPreferencesDto
                {
                    SelectedTheme = preferences.SelectedTheme,
                    SharpCorners = preferences.SharpCorners,
                    DisableFocusOutlines = preferences.DisableFocusOutlines,
                    DisableTooltips = preferences.DisableTooltips,
                    PicsAlwaysVisible = preferences.PicsAlwaysVisible,
                    DisableStickyNotifications = preferences.DisableStickyNotifications,
                    UseLocalTimezone = preferences.UseLocalTimezone
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preferences for device: {DeviceId}", deviceId);
            return null;
        }
    }

    /// <summary>
    /// Save or update user preferences
    /// </summary>
    public bool SavePreferences(string deviceId, UserPreferencesDto preferencesDto)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the device/session exists
            var session = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId);
            if (session == null)
            {
                _logger.LogWarning("Device/session not found: {DeviceId}", deviceId);
                return false;
            }

            var existingPreferences = context.UserPreferences
                .FirstOrDefault(p => p.DeviceId == deviceId);

            if (existingPreferences != null)
            {
                // Update existing preferences
                existingPreferences.SelectedTheme = preferencesDto.SelectedTheme;
                existingPreferences.SharpCorners = preferencesDto.SharpCorners;
                existingPreferences.DisableFocusOutlines = preferencesDto.DisableFocusOutlines;
                existingPreferences.DisableTooltips = preferencesDto.DisableTooltips;
                existingPreferences.PicsAlwaysVisible = preferencesDto.PicsAlwaysVisible;
                existingPreferences.DisableStickyNotifications = preferencesDto.DisableStickyNotifications;
                existingPreferences.UseLocalTimezone = preferencesDto.UseLocalTimezone;
                existingPreferences.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                // Create new preferences
                var newPreferences = new UserPreferences
                {
                    DeviceId = deviceId,
                    SelectedTheme = preferencesDto.SelectedTheme,
                    SharpCorners = preferencesDto.SharpCorners,
                    DisableFocusOutlines = preferencesDto.DisableFocusOutlines,
                    DisableTooltips = preferencesDto.DisableTooltips,
                    PicsAlwaysVisible = preferencesDto.PicsAlwaysVisible,
                    DisableStickyNotifications = preferencesDto.DisableStickyNotifications,
                    UseLocalTimezone = preferencesDto.UseLocalTimezone,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                context.UserPreferences.Add(newPreferences);
            }

            context.SaveChanges();
            _logger.LogInformation("Saved preferences for device: {DeviceId}", deviceId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving preferences for device: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Update a specific preference field
    /// </summary>
    public bool UpdatePreference<T>(string deviceId, string preferenceKey, T value)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the device/session exists
            var session = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId);
            if (session == null)
            {
                _logger.LogWarning("Device/session not found: {DeviceId}", deviceId);
                return false;
            }

            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.DeviceId == deviceId);

            if (preferences == null)
            {
                // Create new preferences if they don't exist
                preferences = new UserPreferences
                {
                    DeviceId = deviceId,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                context.UserPreferences.Add(preferences);
            }

            // Update the specific preference
            switch (preferenceKey.ToLowerInvariant())
            {
                case "selectedtheme":
                    preferences.SelectedTheme = GetValueAsString(value);
                    break;
                case "sharpcorners":
                    preferences.SharpCorners = GetValueAsBoolean(value);
                    break;
                case "disablefocusoutlines":
                    preferences.DisableFocusOutlines = GetValueAsBoolean(value);
                    break;
                case "disabletooltips":
                    preferences.DisableTooltips = GetValueAsBoolean(value);
                    break;
                case "picsalwaysvisible":
                    preferences.PicsAlwaysVisible = GetValueAsBoolean(value);
                    break;
                case "disablestickynotifications":
                    preferences.DisableStickyNotifications = GetValueAsBoolean(value);
                    break;
                case "uselocaltimezone":
                    preferences.UseLocalTimezone = GetValueAsBoolean(value);
                    break;
                default:
                    _logger.LogWarning("Unknown preference key: {Key}", preferenceKey);
                    return false;
            }

            preferences.UpdatedAtUtc = DateTime.UtcNow;
            context.SaveChanges();

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating preference {Key} for device: {DeviceId}", preferenceKey, deviceId);
            return false;
        }
    }

    /// <summary>
    /// Update a specific preference field and return the updated full preferences
    /// This prevents race conditions by reading from the same transaction
    /// </summary>
    public UserPreferencesDto? UpdatePreferenceAndGet<T>(string deviceId, string preferenceKey, T value)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the device/session exists
            var session = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId);
            if (session == null)
            {
                _logger.LogWarning("Device/session not found when updating preference: {DeviceId}", deviceId);
                return null;
            }

            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.DeviceId == deviceId);

            if (preferences == null)
            {
                // Create new preferences if they don't exist
                preferences = new UserPreferences
                {
                    DeviceId = deviceId,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                context.UserPreferences.Add(preferences);
            }

            // Update the specific preference
            switch (preferenceKey.ToLowerInvariant())
            {
                case "selectedtheme":
                    preferences.SelectedTheme = GetValueAsString(value);
                    break;
                case "sharpcorners":
                    preferences.SharpCorners = GetValueAsBoolean(value);
                    break;
                case "disablefocusoutlines":
                    preferences.DisableFocusOutlines = GetValueAsBoolean(value);
                    break;
                case "disabletooltips":
                    preferences.DisableTooltips = GetValueAsBoolean(value);
                    break;
                case "picsalwaysvisible":
                    preferences.PicsAlwaysVisible = GetValueAsBoolean(value);
                    break;
                case "disablestickynotifications":
                    preferences.DisableStickyNotifications = GetValueAsBoolean(value);
                    break;
                case "uselocaltimezone":
                    preferences.UseLocalTimezone = GetValueAsBoolean(value);
                    break;
                default:
                    _logger.LogWarning("Unknown preference key: {Key}", preferenceKey);
                    return null;
            }

            preferences.UpdatedAtUtc = DateTime.UtcNow;
            context.SaveChanges();

            // Return the updated preferences from the same context to avoid race conditions
            return new UserPreferencesDto
            {
                SelectedTheme = preferences.SelectedTheme,
                SharpCorners = preferences.SharpCorners,
                DisableFocusOutlines = preferences.DisableFocusOutlines,
                DisableTooltips = preferences.DisableTooltips,
                PicsAlwaysVisible = preferences.PicsAlwaysVisible,
                DisableStickyNotifications = preferences.DisableStickyNotifications,
                UseLocalTimezone = preferences.UseLocalTimezone
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating preference {Key} for device: {DeviceId}", preferenceKey, deviceId);
            return null;
        }
    }

    /// <summary>
    /// Delete user preferences
    /// </summary>
    public bool DeletePreferences(string deviceId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.DeviceId == deviceId);

            if (preferences != null)
            {
                context.UserPreferences.Remove(preferences);
                context.SaveChanges();
                _logger.LogInformation("Deleted preferences for device: {DeviceId}", deviceId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting preferences for device: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Helper method to convert value to boolean, handling JsonElement
    /// </summary>
    private bool GetValueAsBoolean<T>(T value)
    {
        if (value is JsonElement jsonElement)
        {
            return jsonElement.GetBoolean();
        }
        return Convert.ToBoolean(value);
    }

    /// <summary>
    /// Helper method to convert value to string, handling JsonElement
    /// </summary>
    private string? GetValueAsString<T>(T value)
    {
        if (value is JsonElement jsonElement)
        {
            return jsonElement.ValueKind == JsonValueKind.Null ? null : jsonElement.GetString();
        }
        return value as string;
    }
}
