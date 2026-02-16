using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace LancacheManager.Core.Services;

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
        public bool Use24HourFormat { get; set; }
        public bool ShowDatasourceLabels { get; set; } = true;
        public bool ShowYearInDates { get; set; }
        public string? RefreshRate { get; set; } // Refresh rate for guest users (null = use default)
        public bool? RefreshRateLocked { get; set; } // Per-session lock override (null = use global, true/false = override)
        public string[]? AllowedTimeFormats { get; set; } // Allowed time formats for this user (null = all formats)
        public int? MaxThreadCount { get; set; } // Per-session max thread count limit (null = use system default)
    }

    /// <summary>
    /// Get user preferences for a session
    /// </summary>
    public UserPreferencesDto? GetPreferences(Guid sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.SessionId == sessionId);

            return preferences != null ? ToDto(preferences) : null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preferences for session: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// Save or update user preferences
    /// </summary>
    public bool SavePreferences(Guid sessionId, UserPreferencesDto preferencesDto)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the session exists
            var session = context.UserSessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
            {
                _logger.LogWarning("Session not found: {SessionId}", sessionId);
                return false;
            }

            var existingPreferences = context.UserPreferences
                .FirstOrDefault(p => p.SessionId == sessionId);

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
                existingPreferences.Use24HourFormat = preferencesDto.Use24HourFormat;
                existingPreferences.ShowDatasourceLabels = preferencesDto.ShowDatasourceLabels;
                existingPreferences.ShowYearInDates = preferencesDto.ShowYearInDates;
                existingPreferences.RefreshRate = preferencesDto.RefreshRate;
                existingPreferences.RefreshRateLocked = preferencesDto.RefreshRateLocked;
                existingPreferences.AllowedTimeFormats = SerializeAllowedTimeFormats(preferencesDto.AllowedTimeFormats);
                existingPreferences.MaxThreadCount = preferencesDto.MaxThreadCount;
                existingPreferences.UpdatedAtUtc = DateTime.UtcNow;
            }
            else
            {
                // Create new preferences
                var newPreferences = new UserPreferences
                {
                    SessionId = sessionId,
                    SelectedTheme = preferencesDto.SelectedTheme,
                    SharpCorners = preferencesDto.SharpCorners,
                    DisableFocusOutlines = preferencesDto.DisableFocusOutlines,
                    DisableTooltips = preferencesDto.DisableTooltips,
                    PicsAlwaysVisible = preferencesDto.PicsAlwaysVisible,
                    DisableStickyNotifications = preferencesDto.DisableStickyNotifications,
                    UseLocalTimezone = preferencesDto.UseLocalTimezone,
                    Use24HourFormat = preferencesDto.Use24HourFormat,
                    ShowDatasourceLabels = preferencesDto.ShowDatasourceLabels,
                    ShowYearInDates = preferencesDto.ShowYearInDates,
                    RefreshRate = preferencesDto.RefreshRate,
                    RefreshRateLocked = preferencesDto.RefreshRateLocked,
                    AllowedTimeFormats = SerializeAllowedTimeFormats(preferencesDto.AllowedTimeFormats),
                    MaxThreadCount = preferencesDto.MaxThreadCount,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                context.UserPreferences.Add(newPreferences);
            }

            context.SaveChanges();
            _logger.LogInformation("Saved preferences for session: {SessionId}", sessionId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving preferences for session: {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Update a specific preference field and return the updated full preferences
    /// This prevents race conditions by reading from the same transaction
    /// </summary>
    public UserPreferencesDto? UpdatePreferenceAndGet<T>(Guid sessionId, string preferenceKey, T value)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the session exists
            var session = context.UserSessions.FirstOrDefault(s => s.Id == sessionId);
            if (session == null)
            {
                _logger.LogWarning("Session not found when updating preference: {SessionId}", sessionId);
                return null;
            }

            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.SessionId == sessionId);

            if (preferences == null)
            {
                // Create new preferences if they don't exist
                preferences = new UserPreferences
                {
                    SessionId = sessionId,
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
                case "use24hourformat":
                    preferences.Use24HourFormat = GetValueAsBoolean(value);
                    break;
                case "showdatasourcelabels":
                    preferences.ShowDatasourceLabels = GetValueAsBoolean(value);
                    break;
                case "showyearindates":
                    preferences.ShowYearInDates = GetValueAsBoolean(value);
                    break;
                case "refreshrate":
                    preferences.RefreshRate = GetValueAsString(value);
                    break;
                case "refreshratelocked":
                    preferences.RefreshRateLocked = GetNullableBoolean(value);
                    break;
                case "allowedtimeformats":
                    preferences.AllowedTimeFormats = SerializeAllowedTimeFormats(GetValueAsStringArray(value));
                    break;
                case "maxthreadcount":
                    preferences.MaxThreadCount = GetNullableInt(value);
                    break;
                default:
                    _logger.LogWarning("Unknown preference key: {Key}", preferenceKey);
                    return null;
            }

            preferences.UpdatedAtUtc = DateTime.UtcNow;
            context.SaveChanges();

            // Return the updated preferences from the same context to avoid race conditions
            return ToDto(preferences);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating preference {Key} for session: {SessionId}", preferenceKey, sessionId);
            return null;
        }
    }

    /// <summary>
    /// Delete user preferences
    /// </summary>
    public bool DeletePreferences(Guid sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.SessionId == sessionId);

            if (preferences != null)
            {
                context.UserPreferences.Remove(preferences);
                context.SaveChanges();
                _logger.LogInformation("Deleted preferences for session: {SessionId}", sessionId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting preferences for session: {SessionId}", sessionId);
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
    /// Helper method to convert value to nullable boolean, handling JsonElement
    /// </summary>
    private bool? GetNullableBoolean<T>(T value)
    {
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
                return null;
            return jsonElement.GetBoolean();
        }
        if (value == null)
            return null;
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

    /// <summary>
    /// Helper method to convert value to nullable int, handling JsonElement
    /// </summary>
    private int? GetNullableInt<T>(T value)
    {
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
                return null;
            return jsonElement.GetInt32();
        }
        if (value == null)
            return null;
        return Convert.ToInt32(value);
    }

    /// <summary>
    /// Helper method to convert value to string array, handling JsonElement
    /// </summary>
    private string[]? GetValueAsStringArray<T>(T value)
    {
        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Null)
                return null;
            if (jsonElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var item in jsonElement.EnumerateArray())
                {
                    var str = item.GetString();
                    if (str != null)
                        list.Add(str);
                }
                return list.ToArray();
            }
        }
        return value as string[];
    }

    /// <summary>
    /// Serialize string array to JSON for database storage
    /// </summary>
    private static string? SerializeAllowedTimeFormats(string[]? formats)
    {
        if (formats == null || formats.Length == 0)
            return null;
        return JsonSerializer.Serialize(formats);
    }

    /// <summary>
    /// Parse JSON string to string array for DTO
    /// </summary>
    private static string[]? ParseAllowedTimeFormats(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<string[]>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a UserPreferences entity to a UserPreferencesDto
    /// </summary>
    private static UserPreferencesDto ToDto(UserPreferences prefs) => new()
    {
        SelectedTheme = prefs.SelectedTheme,
        SharpCorners = prefs.SharpCorners,
        DisableFocusOutlines = prefs.DisableFocusOutlines,
        DisableTooltips = prefs.DisableTooltips,
        PicsAlwaysVisible = prefs.PicsAlwaysVisible,
        DisableStickyNotifications = prefs.DisableStickyNotifications,
        UseLocalTimezone = prefs.UseLocalTimezone,
        Use24HourFormat = prefs.Use24HourFormat,
        ShowDatasourceLabels = prefs.ShowDatasourceLabels,
        ShowYearInDates = prefs.ShowYearInDates,
        RefreshRate = prefs.RefreshRate,
        RefreshRateLocked = prefs.RefreshRateLocked,
        AllowedTimeFormats = ParseAllowedTimeFormats(prefs.AllowedTimeFormats),
        MaxThreadCount = prefs.MaxThreadCount
    };
}
