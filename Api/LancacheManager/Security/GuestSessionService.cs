using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Security;

public class GuestSessionService
{
    private readonly ILogger<GuestSessionService> _logger;
    private readonly IDbContextFactory<AppDbContext> _contextFactory;
    private readonly StateRepository _stateRepository;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<string, GuestSession> _sessionCache = new();
    private readonly object _cacheLock = new();

    public GuestSessionService(ILogger<GuestSessionService> logger, IDbContextFactory<AppDbContext> contextFactory, StateRepository stateRepository, IConfiguration configuration)
    {
        _logger = logger;
        _contextFactory = contextFactory;
        _stateRepository = stateRepository;
        _configuration = configuration;

        // Initialize guest session duration from appsettings.json if not already set
        InitializeGuestSessionDuration();

        LoadGuestSessions();
    }

    private void InitializeGuestSessionDuration()
    {
        // Check if duration is at default value (6 hours), if so, load from appsettings
        var currentDuration = _stateRepository.GetGuestSessionDurationHours();
        if (currentDuration == 6)
        {
            var configuredDuration = _configuration.GetValue<int>("Security:GuestSessionDurationHours", 6);
            if (configuredDuration != 6)
            {
                _stateRepository.SetGuestSessionDurationHours(configuredDuration);
                _logger.LogInformation("Initialized guest session duration from appsettings: {Hours} hours", configuredDuration);
            }
        }
    }

    public class GuestSession
    {
        public string DeviceId { get; set; } = string.Empty; // Browser fingerprint - primary key
        public string? SessionId { get; set; } // ASP.NET Core session ID (temporary)
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public bool IsRevoked { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedBy { get; set; }
        // Prefill permissions
        public bool PrefillEnabled { get; set; }
        public DateTime? PrefillExpiresAt { get; set; }
    }

    public class CreateGuestSessionRequest
    {
        public string DeviceId { get; set; } = string.Empty; // Browser fingerprint
        public string? DeviceName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
    }

    public class GuestSessionInfo
    {
        public string DeviceId { get; set; } = string.Empty; // Browser fingerprint - primary key
        public string? SessionId { get; set; } // ASP.NET Core session ID (temporary)
        public string? DeviceName { get; set; }
        public string? IpAddress { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? LastSeenAt { get; set; }
        public DateTime ExpiresAt { get; set; }
        public bool IsExpired { get; set; }
        public bool IsRevoked { get; set; }
        public DateTime? RevokedAt { get; set; }
        public string? RevokedBy { get; set; }
        // Prefill permissions
        public bool PrefillEnabled { get; set; }
        public DateTime? PrefillExpiresAt { get; set; }
        public bool IsPrefillExpired => PrefillExpiresAt.HasValue && PrefillExpiresAt.Value <= DateTime.UtcNow;
    }

    /// <summary>
    /// Create a new guest session
    /// </summary>
    public GuestSession CreateSession(CreateGuestSessionRequest request, string? ipAddress = null)
    {
        try
        {
            // Get default prefill settings
            var prefillEnabledByDefault = _stateRepository.GetGuestPrefillEnabledByDefault();
            var prefillDurationHours = _stateRepository.GetGuestPrefillDurationHours();

            var session = new GuestSession
            {
                DeviceId = request.DeviceId,
                SessionId = null, // Will be populated from HttpContext when needed
                DeviceName = request.DeviceName,
                IpAddress = ipAddress,
                OperatingSystem = request.OperatingSystem,
                Browser = request.Browser,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(GetGuestSessionDurationHours()),
                LastSeenAt = DateTime.UtcNow,
                IsRevoked = false,
                // Initialize prefill permissions from defaults
                PrefillEnabled = prefillEnabledByDefault,
                PrefillExpiresAt = prefillEnabledByDefault ? DateTime.UtcNow.AddHours(prefillDurationHours) : null
            };

            SaveGuestSession(session);

            lock (_cacheLock)
            {
                _sessionCache[session.DeviceId] = session;
            }

            _logger.LogInformation("Guest session created: {DeviceId}, Device: {DeviceName}",
                session.DeviceId, session.DeviceName ?? "Unknown");

            return session;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating guest session");
            throw;
        }
    }

    /// <summary>
    /// Validate a guest session (check if expired or revoked)
    /// Returns: (isValid, reason) - reason is only set when session exists but is invalid
    /// </summary>
    public (bool isValid, string? reason) ValidateSessionWithReason(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return (false, null);
        }

        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(deviceId, out var session))
            {
                // NOTE: Do NOT update LastSeenAt here - that's the heartbeat's job
                // The heartbeat endpoint (/api/sessions/current/last-seen) respects page visibility
                // and only updates LastSeenAt when the user is actively viewing the page.
                // Updating it here on every API request would make guests always appear "Active"
                // even when their browser tab is minimized.

                // Check if revoked or expired
                if (session.IsRevoked)
                {
                    return (false, "revoked");
                }

                if (session.ExpiresAt <= DateTime.UtcNow)
                {
                    return (false, "expired");
                }

                return (true, null);
            }
        }

        // Session not found - return false but no reason (could be deleted or never existed)
        return (false, null);
    }

    /// <summary>
    /// Validate a guest session (check if expired or revoked)
    /// </summary>
    public bool ValidateSession(string deviceId)
    {
        return ValidateSessionWithReason(deviceId).isValid;
    }

    /// <summary>
    /// Update the LastSeenAt timestamp for a guest session
    /// </summary>
    public void UpdateLastSeen(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        try
        {
            using var context = _contextFactory.CreateDbContext();
            var session = context.UserSessions
                .FirstOrDefault(s => s.DeviceId == deviceId && s.IsGuest && !s.IsRevoked);

            if (session != null)
            {
                session.LastSeenAtUtc = DateTime.UtcNow;
                context.SaveChanges();

                // Update cache
                lock (_cacheLock)
                {
                    if (_sessionCache.TryGetValue(deviceId, out var cachedSession))
                    {
                        cachedSession.LastSeenAt = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GuestSession] Failed to update LastSeen for device {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Get all guest sessions
    /// </summary>
    public List<GuestSessionInfo> GetAllSessions()
    {
        var sessions = new List<GuestSessionInfo>();

        lock (_cacheLock)
        {
            foreach (var session in _sessionCache.Values)
            {
                sessions.Add(new GuestSessionInfo
                {
                    DeviceId = session.DeviceId,
                    SessionId = session.SessionId, // ASP.NET Core session ID (may be null)
                    DeviceName = session.DeviceName,
                    IpAddress = session.IpAddress,
                    OperatingSystem = session.OperatingSystem,
                    Browser = session.Browser,
                    CreatedAt = session.CreatedAt.AsUtc(),
                    LastSeenAt = session.LastSeenAt.AsUtc(),
                    ExpiresAt = session.ExpiresAt.AsUtc(),
                    IsExpired = session.ExpiresAt <= DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAt = session.RevokedAt.AsUtc(),
                    RevokedBy = session.RevokedBy,
                    // Prefill permissions
                    PrefillEnabled = session.PrefillEnabled,
                    PrefillExpiresAt = session.PrefillExpiresAt.AsUtc()
                });
            }
        }

        return sessions.OrderByDescending(s => s.LastSeenAt ?? s.CreatedAt).ToList();
    }

    /// <summary>
    /// Get a specific guest session by device ID
    /// </summary>
    public GuestSessionInfo? GetSessionByDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return null;
        }

        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(deviceId, out var session))
            {
                return new GuestSessionInfo
                {
                    DeviceId = session.DeviceId,
                    SessionId = session.SessionId,
                    DeviceName = session.DeviceName,
                    IpAddress = session.IpAddress,
                    OperatingSystem = session.OperatingSystem,
                    Browser = session.Browser,
                    CreatedAt = session.CreatedAt.AsUtc(),
                    LastSeenAt = session.LastSeenAt.AsUtc(),
                    ExpiresAt = session.ExpiresAt.AsUtc(),
                    IsExpired = session.ExpiresAt <= DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAt = session.RevokedAt.AsUtc(),
                    RevokedBy = session.RevokedBy,
                    // Prefill permissions
                    PrefillEnabled = session.PrefillEnabled,
                    PrefillExpiresAt = session.PrefillExpiresAt.AsUtc()
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Set prefill permission for a guest session
    /// </summary>
    public bool SetPrefillPermission(string deviceId, bool enabled, int? durationHours = null)
    {
        try
        {
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(deviceId, out var session))
                {
                    session.PrefillEnabled = enabled;
                    if (enabled)
                    {
                        var hours = durationHours ?? _stateRepository.GetGuestPrefillDurationHours();
                        session.PrefillExpiresAt = DateTime.UtcNow.AddHours(hours);
                    }
                    else
                    {
                        session.PrefillExpiresAt = null;
                    }
                    SaveGuestSession(session);
                    _logger.LogInformation("Prefill permission {Action} for guest session: {DeviceId}, expires: {Expires}",
                        enabled ? "enabled" : "disabled", deviceId, session.PrefillExpiresAt);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting prefill permission for guest session: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Revoke a guest session
    /// </summary>
    public bool RevokeSession(string deviceId, string? revokedBy = null)
    {
        try
        {
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(deviceId, out var session))
                {
                    session.IsRevoked = true;
                    session.RevokedAt = DateTime.UtcNow;
                    session.RevokedBy = revokedBy;
                    SaveGuestSession(session);
                    _logger.LogWarning("Revoked guest session: {DeviceId} by {RevokedBy}", deviceId, revokedBy ?? "Unknown");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking guest session: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Remove guest session from cache only (used during upgrade to authenticated)
    /// Does NOT delete from database - just removes from in-memory cache
    /// </summary>
    public void RemoveFromCache(string deviceId)
    {
        lock (_cacheLock)
        {
            _sessionCache.Remove(deviceId);
        }
        _logger.LogInformation("Removed guest session from cache (upgrade): {DeviceId}", deviceId);
    }

    /// <summary>
    /// Permanently delete a guest session
    /// </summary>
    public bool DeleteSession(string deviceId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var userSession = context.UserSessions.FirstOrDefault(s => s.DeviceId == deviceId && s.IsGuest);

            if (userSession != null)
            {
                context.UserSessions.Remove(userSession);
                context.SaveChanges();

                lock (_cacheLock)
                {
                    _sessionCache.Remove(deviceId);
                }

                _logger.LogInformation("Deleted guest session: {DeviceId}", deviceId);
                return true;
            }

            _logger.LogWarning("Guest session not found for deletion: {DeviceId}", deviceId);
            // Still remove from cache even if not in database
            lock (_cacheLock)
            {
                _sessionCache.Remove(deviceId);
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting guest session: {DeviceId}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// Clean up expired sessions (older than 24 hours)
    /// </summary>
    public int CleanupExpiredSessions()
    {
        var count = 0;
        var cutoffDate = DateTime.UtcNow.AddHours(-24);

        try
        {
            using var context = _contextFactory.CreateDbContext();
            var expiredSessions = context.UserSessions
                .Where(s => s.IsGuest && s.ExpiresAtUtc < cutoffDate)
                .ToList();

            foreach (var session in expiredSessions)
            {
                context.UserSessions.Remove(session);
                lock (_cacheLock)
                {
                    _sessionCache.Remove(session.DeviceId);
                }
                count++;
            }

            if (count > 0)
            {
                context.SaveChanges();
                _logger.LogInformation("Cleaned up {Count} expired guest sessions", count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up expired sessions");
        }

        return count;
    }

    private void SaveGuestSession(GuestSession session)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();

            // Check if session exists
            var existingSession = context.UserSessions.FirstOrDefault(s => s.DeviceId == session.DeviceId);

            if (existingSession != null)
            {
                // Update existing session
                existingSession.DeviceName = session.DeviceName ?? string.Empty;
                existingSession.IpAddress = session.IpAddress ?? string.Empty;
                existingSession.OperatingSystem = session.OperatingSystem ?? string.Empty;
                existingSession.Browser = session.Browser ?? string.Empty;
                existingSession.LastSeenAtUtc = session.LastSeenAt ?? DateTime.UtcNow;
                existingSession.ExpiresAtUtc = session.ExpiresAt;
                existingSession.IsRevoked = session.IsRevoked;
                existingSession.RevokedAtUtc = session.RevokedAt;
                existingSession.RevokedBy = session.RevokedBy;
                existingSession.IsGuest = true; // Ensure session is marked as guest (handles downgrade from authenticated)
                // Prefill permissions
                existingSession.PrefillEnabled = session.PrefillEnabled;
                existingSession.PrefillExpiresAtUtc = session.PrefillExpiresAt;
            }
            else
            {
                // Create new session
                var userSession = new UserSession
                {
                    DeviceId = session.DeviceId,
                    DeviceName = session.DeviceName ?? string.Empty,
                    IpAddress = session.IpAddress ?? string.Empty,
                    OperatingSystem = session.OperatingSystem ?? string.Empty,
                    Browser = session.Browser ?? string.Empty,
                    IsGuest = true,
                    CreatedAtUtc = session.CreatedAt,
                    ExpiresAtUtc = session.ExpiresAt,
                    LastSeenAtUtc = session.LastSeenAt ?? DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAtUtc = session.RevokedAt,
                    RevokedBy = session.RevokedBy,
                    // Prefill permissions
                    PrefillEnabled = session.PrefillEnabled,
                    PrefillExpiresAtUtc = session.PrefillExpiresAt
                };
                context.UserSessions.Add(userSession);
            }

            context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving guest session: {DeviceId}", session.DeviceId);
        }
    }

    private void LoadGuestSessions()
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var sessions = context.UserSessions
                .Where(s => s.IsGuest)
                .ToList();

            lock (_cacheLock)
            {
                _sessionCache.Clear();

                foreach (var userSession in sessions)
                {
                    // Ensure DateTime values from EF Core are marked as UTC
                    var session = new GuestSession
                    {
                        DeviceId = userSession.DeviceId,
                        DeviceName = userSession.DeviceName,
                        IpAddress = userSession.IpAddress,
                        OperatingSystem = userSession.OperatingSystem,
                        Browser = userSession.Browser,
                        CreatedAt = userSession.CreatedAtUtc.AsUtc(),
                        ExpiresAt = (userSession.ExpiresAtUtc ?? DateTime.UtcNow.AddHours(GetGuestSessionDurationHours())).AsUtc(),
                        LastSeenAt = userSession.LastSeenAtUtc.AsUtc(),
                        IsRevoked = userSession.IsRevoked,
                        RevokedAt = userSession.RevokedAtUtc.AsUtc(),
                        RevokedBy = userSession.RevokedBy,
                        // Prefill permissions
                        PrefillEnabled = userSession.PrefillEnabled,
                        PrefillExpiresAt = userSession.PrefillExpiresAtUtc.AsUtc()
                    };

                    _sessionCache[session.DeviceId] = session;
                }
            }

            _logger.LogInformation("Loaded {Count} guest sessions from database", _sessionCache.Count);

            // Clean up old sessions on startup
            CleanupExpiredSessions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading guest sessions from database");
        }
    }

    /// <summary>
    /// Get the configured guest session duration in hours
    /// </summary>
    public int GetGuestSessionDurationHours()
    {
        return _stateRepository.GetGuestSessionDurationHours();
    }

    /// <summary>
    /// Update the guest session duration configuration
    /// Persists to state.json file
    /// </summary>
    public void SetGuestSessionDurationHours(int hours)
    {
        if (hours < 1 || hours > 168) // Between 1 hour and 1 week
        {
            throw new ArgumentException("Guest session duration must be between 1 and 168 hours");
        }

        _stateRepository.SetGuestSessionDurationHours(hours);
        _logger.LogInformation("Guest session duration updated to {Hours} hours (persisted to state.json)", hours);
    }
}
