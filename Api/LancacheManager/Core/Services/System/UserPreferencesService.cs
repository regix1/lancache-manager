using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

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
        public bool UseUtcTimezone { get; set; }
        public bool Use24HourFormat { get; set; }
        public bool ShowDatasourceLabels { get; set; } = true;
        public string? RefreshRate { get; set; } // Refresh rate for guest users (null = use default)
        public bool? RefreshRateLocked { get; set; } // Per-session lock override (null = use global, true/false = override)
        public string[]? AllowedTimeFormats { get; set; } // Allowed time formats for this user (null = all formats)
        public int? SteamMaxThreadCount { get; set; } // Per-session Steam max thread count limit (null = use system default)
        public int? EpicMaxThreadCount { get; set; } // Per-session Epic max thread count limit (null = use system default)

        /// <summary>
        /// Creates a UserPreferencesDto with default values.
        /// Used when no session exists or no preferences have been saved yet.
        /// </summary>
        public static UserPreferencesDto Default() => new()
        {
            SelectedTheme = null,
            SharpCorners = false,
            DisableFocusOutlines = false,
            DisableTooltips = false,
            PicsAlwaysVisible = false,
            DisableStickyNotifications = false,
            UseLocalTimezone = false,
            UseUtcTimezone = false,
            Use24HourFormat = true,
            ShowDatasourceLabels = true,
            RefreshRate = null,
            RefreshRateLocked = null,
            AllowedTimeFormats = null
        };
    }

    /// <summary>
    /// The three columns that together name one clock. They are sent and stored as a set because no two of
    /// them describe a state on their own.
    /// </summary>
    public class ClockPreferences
    {
        public bool UseUtcTimezone { get; set; }
        public bool UseLocalTimezone { get; set; }
        public bool Use24HourFormat { get; set; }
    }

    /// <summary>
    /// Get user preferences for a session
    /// </summary>
    /// <returns>
    /// Null both when the session has no preferences row yet and when the read failed. Callers
    /// fall back to the defaults either way, so a reader whose stored preferences could not be
    /// read sees the defaults rather than an error.
    /// </returns>
    public UserPreferencesDto? GetPreferences(Guid sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = context.UserPreferences
                .AsNoTracking()
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
    /// Save or update user preferences and return what was stored. The stored preferences are not always
    /// the ones that were sent: the clock columns are settled against each other and a refresh rate the
    /// server does not recognise is dropped. Returns null when nothing was saved.
    /// </summary>
    public async Task<UserPreferencesDto?> SavePreferencesAsync(
        Guid sessionId,
        UserPreferencesDto preferencesDto,
        bool preserveAdminFields = false)
    {
        if (preserveAdminFields)
        {
            // A guest can send only its own fields. Existing administrator-owned columns are left
            // unmodified in the context that performs the write; for a first insert, redaction supplies
            // their empty values. Keeping this decision here closes the read-then-write window in the
            // controller where an administrator could commit newer values between the two calls.
            RedactAdminFields(preferencesDto);
        }

        // Two first-time saves for one session each find no row and each insert. The unique index on
        // SessionId lets one of them commit, and the loser re-reads on a fresh context and writes onto the
        // row the winner just created, so both callers are answered with what is stored instead of one
        // being handed an unhandled unique-key error. Npgsql does not treat a unique violation as
        // transient, so the connection retry policy never covers this. Same shape as the per-key write
        // loop below.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var insertedNewRow = false;
            try
            {
                using var context = _contextFactory.CreateDbContext();

                // Ensure the session exists
                var session = await context.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Session not found: {SessionId}", sessionId);
                    return null;
                }

                var existingPreferences = await context.UserPreferences
                    .FirstOrDefaultAsync(p => p.SessionId == sessionId);

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
                    existingPreferences.UseUtcTimezone = preferencesDto.UseUtcTimezone;
                    existingPreferences.Use24HourFormat = preferencesDto.Use24HourFormat;
                    existingPreferences.ShowDatasourceLabels = preferencesDto.ShowDatasourceLabels;
                    existingPreferences.RefreshRate = RefreshRateExtensions.TryParseWire(preferencesDto.RefreshRate);
                    if (!preserveAdminFields)
                    {
                        existingPreferences.RefreshRateLocked = preferencesDto.RefreshRateLocked;
                        existingPreferences.AllowedTimeFormats = SerializeAllowedTimeFormats(preferencesDto.AllowedTimeFormats);
                        existingPreferences.SteamMaxThreadCount = preferencesDto.SteamMaxThreadCount;
                        existingPreferences.EpicMaxThreadCount = preferencesDto.EpicMaxThreadCount;
                    }
                    existingPreferences.UpdatedAtUtc = DateTime.UtcNow;
                    NormalizeClockPreferences(existingPreferences);
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
                        UseUtcTimezone = preferencesDto.UseUtcTimezone,
                        Use24HourFormat = preferencesDto.Use24HourFormat,
                        ShowDatasourceLabels = preferencesDto.ShowDatasourceLabels,
                        RefreshRate = RefreshRateExtensions.TryParseWire(preferencesDto.RefreshRate),
                        RefreshRateLocked = preferencesDto.RefreshRateLocked,
                        AllowedTimeFormats = SerializeAllowedTimeFormats(preferencesDto.AllowedTimeFormats),
                        SteamMaxThreadCount = preferencesDto.SteamMaxThreadCount,
                        EpicMaxThreadCount = preferencesDto.EpicMaxThreadCount,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    NormalizeClockPreferences(newPreferences);
                    context.UserPreferences.Add(newPreferences);
                    insertedNewRow = true;
                }

                await context.SaveChangesAsync();
                _logger.LogInformation("Saved preferences for session: {SessionId}", sessionId);
                return await ReadStoredPreferencesAsync(context, sessionId);
            }
            catch (DbUpdateException ex) when (insertedNewRow && attempt == 0)
            {
                _logger.LogDebug(
                    ex,
                    "Preferences row for session {SessionId} was created concurrently; saving onto the existing row",
                    sessionId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving preferences for session: {SessionId}", sessionId);
                return null;
            }
        }

        _logger.LogError("Gave up saving preferences for session: {SessionId}", sessionId);
        return null;
    }

    /// <summary>
    /// Writes the admin's guest defaults onto a session that has no preferences row yet, so a guest who
    /// logs in AFTER the defaults were set gets them. Without this only the guests already connected when
    /// an admin changed a default ever see it, because the broadcast is all that carries it and a new
    /// session falls back to <see cref="UserPreferencesDto.Default"/>.
    ///
    /// A session that already has a row is left exactly as it is and the method reports false. That row is
    /// the person's own saved choice, and a default must never overrule it.
    ///
    /// Every failure is caught and logged rather than thrown. This runs while a session is being created,
    /// and a cosmetic default that could not be written must never be the reason a login fails: the guest
    /// simply gets the built-in defaults, which is what happened before this existed.
    /// </summary>
    public async Task<bool> SeedGuestDefaultsAsync(Guid sessionId, UserPreferencesDto defaults)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            if (await context.UserPreferences.AnyAsync(p => p.SessionId == sessionId))
            {
                return false;
            }

            var preferences = new UserPreferences
            {
                SessionId = sessionId,
                UseLocalTimezone = defaults.UseLocalTimezone,
                UseUtcTimezone = defaults.UseUtcTimezone,
                Use24HourFormat = defaults.Use24HourFormat,
                SharpCorners = defaults.SharpCorners,
                DisableTooltips = defaults.DisableTooltips,
                ShowDatasourceLabels = defaults.ShowDatasourceLabels,
                UpdatedAtUtc = DateTime.UtcNow
            };
            NormalizeClockPreferences(preferences);

            context.UserPreferences.Add(preferences);
            await context.SaveChangesAsync();
            _logger.LogInformation("Seeded guest defaults for session: {SessionId}", sessionId);
            return true;
        }
        catch (DbUpdateException ex)
        {
            // The row this seed looked for was written between the check above and the save. That is the
            // outcome the false return already describes and the one the method is built around: the
            // session has preferences of its own and a default must not overrule them. Reporting it as an
            // error made a normal race look like a fault worth investigating.
            _logger.LogDebug(
                ex,
                "Preferences row for session {SessionId} was created concurrently; guest defaults were left unapplied",
                sessionId);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error seeding guest defaults for session: {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Update a specific preference field and return the updated full preferences
    /// This prevents race conditions by reading from the same transaction
    /// </summary>
    public Task<UserPreferencesDto?> UpdatePreferenceAsync(Guid sessionId, PreferenceKey preferenceKey, JsonElement value)
        => ApplyPreferenceWritesAsync(sessionId, new[] { new PreferenceWrite(preferenceKey, value) });

    /// <summary>
    /// Writes the three clock columns together and returns the updated full preferences. Between them they
    /// hold one three-way choice, so sending them as separate requests lets a later choice be applied
    /// in the middle of an earlier one and leaves the row saying something neither caller asked for.
    ///
    /// Local is written first and UTC last. UTC is the one that reshapes its siblings, so writing it last
    /// is what makes it outrank local when a caller sends both clocks on.
    /// </summary>
    public Task<UserPreferencesDto?> UpdateClockPreferencesAsync(Guid sessionId, ClockPreferences clock)
        => ApplyPreferenceWritesAsync(sessionId, new[]
        {
            new PreferenceWrite(PreferenceKey.UseLocalTimezone, JsonSerializer.SerializeToElement(clock.UseLocalTimezone)),
            new PreferenceWrite(PreferenceKey.Use24HourFormat, JsonSerializer.SerializeToElement(clock.Use24HourFormat)),
            new PreferenceWrite(PreferenceKey.UseUtcTimezone, JsonSerializer.SerializeToElement(clock.UseUtcTimezone))
        });

    /// <summary>
    /// Applies every write in one SaveChanges, so no reader and no normalisation step ever sees part of a
    /// caller's choice landed and the rest still missing.
    /// </summary>
    /// <returns>
    /// The stored preferences, or null when nothing was written - either the save failed or both
    /// attempts lost the row race. The controller turns null into a 500, so this failure does
    /// reach the reader.
    /// </returns>
    private async Task<UserPreferencesDto?> ApplyPreferenceWritesAsync(Guid sessionId, IReadOnlyList<PreferenceWrite> writes)
    {
        // A session carries no preferences row until its first write, and several keys can go out at once.
        // Each one finds no row, each one inserts, and the unique index on SessionId lets exactly one of
        // them commit. A loser re-reads on a fresh context and updates the row the winner just wrote, so
        // the whole burst is kept instead of most of it being dropped. Npgsql does not treat a unique
        // violation as transient, so the connection retry policy never covers this.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var insertedNewRow = false;
            try
            {
                using var context = _contextFactory.CreateDbContext();

                // Ensure the session exists
                var session = await context.UserSessions.FirstOrDefaultAsync(s => s.Id == sessionId);
                if (session == null)
                {
                    _logger.LogWarning("Session not found when updating preference: {SessionId}", sessionId);
                    return null;
                }

                var preferences = await context.UserPreferences
                    .FirstOrDefaultAsync(p => p.SessionId == sessionId);

                if (preferences == null)
                {
                    // Create new preferences if they don't exist
                    preferences = new UserPreferences
                    {
                        SessionId = sessionId,
                        UpdatedAtUtc = DateTime.UtcNow
                    };
                    context.UserPreferences.Add(preferences);
                    insertedNewRow = true;
                }

                foreach (var write in writes)
                {
                    if (!ApplyPreference(preferences, write.Key, write.Value))
                    {
                        return null;
                    }
                }

                preferences.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync();

                return await ReadStoredPreferencesAsync(context, sessionId);
            }
            catch (DbUpdateException ex) when (insertedNewRow && attempt == 0)
            {
                _logger.LogDebug(
                    ex,
                    "Preferences row for session {SessionId} was created concurrently; applying {Keys} to the existing row",
                    sessionId,
                    DescribeKeys(writes));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating preference {Keys} for session: {SessionId}", DescribeKeys(writes), sessionId);
                return null;
            }
        }

        _logger.LogError("Gave up updating preference {Keys} for session: {SessionId}", DescribeKeys(writes), sessionId);
        return null;
    }

    private static string DescribeKeys(IReadOnlyList<PreferenceWrite> writes) =>
        string.Join(", ", writes.Select(write => write.Key));

    private readonly record struct PreferenceWrite(PreferenceKey Key, JsonElement Value);

    /// <summary>
    /// Writes one preference onto the entity. Returns false for a key this service cannot write, which is
    /// the caller's signal to give up rather than save.
    /// </summary>
    private bool ApplyPreference(UserPreferences preferences, PreferenceKey preferenceKey, JsonElement value)
    {
        switch (preferenceKey)
        {
            case PreferenceKey.SelectedTheme:
                preferences.SelectedTheme = GetValueAsString(value);
                break;
            case PreferenceKey.SharpCorners:
                preferences.SharpCorners = GetValueAsBoolean(value);
                break;
            case PreferenceKey.DisableFocusOutlines:
                preferences.DisableFocusOutlines = GetValueAsBoolean(value);
                break;
            case PreferenceKey.DisableTooltips:
                preferences.DisableTooltips = GetValueAsBoolean(value);
                break;
            case PreferenceKey.PicsAlwaysVisible:
                preferences.PicsAlwaysVisible = GetValueAsBoolean(value);
                break;
            case PreferenceKey.DisableStickyNotifications:
                preferences.DisableStickyNotifications = GetValueAsBoolean(value);
                break;
            case PreferenceKey.UseLocalTimezone:
                preferences.UseLocalTimezone = GetValueAsBoolean(value);
                // The clock this request names outranks whatever is stored, so choosing local takes UTC
                // off. Turning local OFF says nothing about the other two and deliberately leaves them
                // alone: the three keys travel as separate requests, and reshaping on this one would
                // overrule a 12-hour choice that arrived first.
                if (preferences.UseLocalTimezone)
                {
                    preferences.UseUtcTimezone = false;
                }
                break;
            case PreferenceKey.UseUtcTimezone:
                preferences.UseUtcTimezone = GetValueAsBoolean(value);
                NormalizeClockPreferences(preferences);
                break;
            case PreferenceKey.Use24HourFormat:
                preferences.Use24HourFormat = GetValueAsBoolean(value);
                break;
            case PreferenceKey.ShowDatasourceLabels:
                preferences.ShowDatasourceLabels = GetValueAsBoolean(value);
                break;
            case PreferenceKey.RefreshRate:
                preferences.RefreshRate = RefreshRateExtensions.TryParseWire(GetValueAsString(value));
                break;
            case PreferenceKey.RefreshRateLocked:
                preferences.RefreshRateLocked = GetNullableBoolean(value);
                break;
            case PreferenceKey.AllowedTimeFormats:
                preferences.AllowedTimeFormats = SerializeAllowedTimeFormats(GetValueAsStringArray(value));
                break;
            case PreferenceKey.SteamMaxThreadCount:
                preferences.SteamMaxThreadCount = GetNullableInt(value);
                break;
            case PreferenceKey.EpicMaxThreadCount:
                preferences.EpicMaxThreadCount = GetNullableInt(value);
                break;
            default:
                _logger.LogWarning("Unknown preference key: {Key}", preferenceKey);
                return false;
        }

        return true;
    }

    /// <summary>
    /// Keeps the three clock columns telling one story. UTC outranks local when a caller sends both, which
    /// is the precedence every reader already applies, and UTC always reads on the 24-hour clock because it
    /// has no 12-hour face worth offering. Three independent booleans hold a three-way choice, so without
    /// this the columns can say two things at once. It does nothing unless UTC is on, which is what keeps
    /// it safe to call from a per-key write: a request that turns UTC off must not reshape a 12-hour
    /// choice that arrived ahead of it.
    /// </summary>
    private static void NormalizeClockPreferences(UserPreferences preferences)
    {
        if (!preferences.UseUtcTimezone)
        {
            return;
        }

        preferences.UseLocalTimezone = false;
        preferences.Use24HourFormat = true;
    }

    /// <summary>
    /// The same rule for a clock that is being chosen rather than stored, so the guest defaults an admin
    /// picks and the per-session clock cannot disagree about what UTC means.
    /// </summary>
    public static void NormalizeClockPreferences(ClockPreferences clock)
    {
        if (!clock.UseUtcTimezone)
        {
            return;
        }

        clock.UseLocalTimezone = false;
        clock.Use24HourFormat = true;
    }

    /// <summary>
    /// Delete user preferences
    /// </summary>
    /// <returns>
    /// True when a row was deleted. False both when there was no row to delete and when the
    /// delete failed, so false does not promise the preferences are gone.
    /// </returns>
    public async Task<bool> DeletePreferencesAsync(Guid sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var preferences = await context.UserPreferences
                .FirstOrDefaultAsync(p => p.SessionId == sessionId);

            if (preferences != null)
            {
                context.UserPreferences.Remove(preferences);
                await context.SaveChangesAsync();
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
    /// Returns true if the given preference key is admin-only.
    /// Guests are not permitted to read or write these keys.
    /// </summary>
    public static bool IsAdminOnlyKey(PreferenceKey key) => key switch
    {
        PreferenceKey.RefreshRateLocked => true,
        PreferenceKey.AllowedTimeFormats => true,
        PreferenceKey.SteamMaxThreadCount => true,
        PreferenceKey.EpicMaxThreadCount => true,
        _ => false
    };

    /// <summary>
    /// Strips admin-only fields from a DTO so they cannot be persisted by non-admin callers.
    /// </summary>
    public static void RedactAdminFields(UserPreferencesDto dto)
    {
        dto.RefreshRateLocked = null;
        dto.AllowedTimeFormats = null;
        dto.SteamMaxThreadCount = null;
        dto.EpicMaxThreadCount = null;
    }

    /// <summary>
    /// Reads the row back after a save so the caller is handed what is stored rather than what this request
    /// happened to be holding. A write sends only the columns it changed, so the entity it wrote through
    /// still carries whatever this request's own read returned for every other column. Anything that arrived
    /// in between is missing from it, and a caller passing those values on would hand them to readers that
    /// have the newer ones already. The extra read costs one round trip per save.
    ///
    /// Returns null when the row is gone, which means the session was removed after the save landed. Saying
    /// nothing is better than naming values that no longer exist.
    /// </summary>
    private static async Task<UserPreferencesDto?> ReadStoredPreferencesAsync(AppDbContext context, Guid sessionId)
    {
        var stored = await context.UserPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.SessionId == sessionId);

        return stored != null ? ToDto(stored) : null;
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
        UseUtcTimezone = prefs.UseUtcTimezone,
        Use24HourFormat = prefs.Use24HourFormat,
        ShowDatasourceLabels = prefs.ShowDatasourceLabels,
        RefreshRate = prefs.RefreshRate?.ToWireString(),
        RefreshRateLocked = prefs.RefreshRateLocked,
        AllowedTimeFormats = ParseAllowedTimeFormats(prefs.AllowedTimeFormats),
        SteamMaxThreadCount = prefs.SteamMaxThreadCount,
        EpicMaxThreadCount = prefs.EpicMaxThreadCount
    };
}
