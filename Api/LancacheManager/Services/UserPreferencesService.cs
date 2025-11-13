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
        public bool HideAboutSections { get; set; }
        public bool DisableStickyNotifications { get; set; }
    }

    /// <summary>
    /// Get user preferences for a session
    /// </summary>
    public UserPreferencesDto? GetPreferences(string sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .FirstOrDefault(p => p.SessionId == sessionId);

            if (preferences != null)
            {
                return new UserPreferencesDto
                {
                    SelectedTheme = preferences.SelectedTheme,
                    SharpCorners = preferences.SharpCorners,
                    DisableFocusOutlines = preferences.DisableFocusOutlines,
                    DisableTooltips = preferences.DisableTooltips,
                    PicsAlwaysVisible = preferences.PicsAlwaysVisible,
                    HideAboutSections = preferences.HideAboutSections,
                    DisableStickyNotifications = preferences.DisableStickyNotifications
                };
            }

            return null;
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
    public bool SavePreferences(string sessionId, UserPreferencesDto preferencesDto)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Ensure the session exists
            var session = context.UserSessions.FirstOrDefault(s => s.SessionId == sessionId);
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
                existingPreferences.HideAboutSections = preferencesDto.HideAboutSections;
                existingPreferences.DisableStickyNotifications = preferencesDto.DisableStickyNotifications;
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
                    HideAboutSections = preferencesDto.HideAboutSections,
                    DisableStickyNotifications = preferencesDto.DisableStickyNotifications,
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
    /// Update a specific preference field
    /// </summary>
    public bool UpdatePreference<T>(string sessionId, string preferenceKey, T value)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

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
                case "hideaboutsections":
                    preferences.HideAboutSections = GetValueAsBoolean(value);
                    break;
                case "disablestickynotifications":
                    preferences.DisableStickyNotifications = GetValueAsBoolean(value);
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
            _logger.LogError(ex, "Error updating preference {Key} for session: {SessionId}", preferenceKey, sessionId);
            return false;
        }
    }

    /// <summary>
    /// Delete user preferences
    /// </summary>
    public bool DeletePreferences(string sessionId)
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
