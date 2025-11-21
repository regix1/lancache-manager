using LancacheManager.Data;
using LancacheManager.Infrastructure.Repositories;
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
        public string SessionId { get; set; } = string.Empty;
        public string? DeviceId { get; set; } // Browser fingerprint device ID
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
    }

    public class CreateGuestSessionRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string? DeviceName { get; set; }
        public string? OperatingSystem { get; set; }
        public string? Browser { get; set; }
    }

    public class GuestSessionInfo
    {
        public string SessionId { get; set; } = string.Empty;
        public string? DeviceId { get; set; } // Browser fingerprint device ID
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
    }

    /// <summary>
    /// Create a new guest session
    /// </summary>
    public GuestSession CreateSession(CreateGuestSessionRequest request, string? ipAddress = null)
    {
        try
        {
            // SessionId IS the device ID now (unified approach)
            var session = new GuestSession
            {
                SessionId = request.SessionId,
                DeviceId = request.SessionId, // Now they're the same
                DeviceName = request.DeviceName,
                IpAddress = ipAddress,
                OperatingSystem = request.OperatingSystem,
                Browser = request.Browser,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(GetGuestSessionDurationHours()),
                LastSeenAt = DateTime.UtcNow,
                IsRevoked = false
            };

            SaveGuestSession(session);

            lock (_cacheLock)
            {
                _sessionCache[session.SessionId] = session;
            }

            _logger.LogInformation("Guest session created: {SessionId}, Device: {DeviceName}",
                session.SessionId, session.DeviceName ?? "Unknown");

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
    public (bool isValid, string? reason) ValidateSessionWithReason(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return (false, null);
        }

        lock (_cacheLock)
        {
            if (_sessionCache.TryGetValue(sessionId, out var session))
            {
                // Update last seen
                session.LastSeenAt = DateTime.UtcNow;
                SaveGuestSession(session);

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
    public bool ValidateSession(string sessionId)
    {
        return ValidateSessionWithReason(sessionId).isValid;
    }

    /// <summary>
    /// Update the LastSeenAt timestamp for a guest session
    /// </summary>
    public void UpdateLastSeen(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        try
        {
            using var context = _contextFactory.CreateDbContext();
            var session = context.UserSessions
                .FirstOrDefault(s => s.SessionId == sessionId && s.IsGuest && !s.IsRevoked);

            if (session != null)
            {
                session.LastSeenAtUtc = DateTime.UtcNow;
                context.SaveChanges();

                // Update cache
                lock (_cacheLock)
                {
                    if (_sessionCache.TryGetValue(sessionId, out var cachedSession))
                    {
                        cachedSession.LastSeenAt = DateTime.UtcNow;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GuestSession] Failed to update LastSeen for session {SessionId}", sessionId);
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
                    SessionId = session.SessionId,
                    DeviceId = session.DeviceId, // Now always set (same as SessionId)
                    DeviceName = session.DeviceName,
                    IpAddress = session.IpAddress,
                    OperatingSystem = session.OperatingSystem,
                    Browser = session.Browser,
                    CreatedAt = DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc),
                    LastSeenAt = session.LastSeenAt.HasValue ? DateTime.SpecifyKind(session.LastSeenAt.Value, DateTimeKind.Utc) : null,
                    ExpiresAt = DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc),
                    IsExpired = session.ExpiresAt <= DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAt = session.RevokedAt.HasValue ? DateTime.SpecifyKind(session.RevokedAt.Value, DateTimeKind.Utc) : null,
                    RevokedBy = session.RevokedBy
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
            // DeviceId equals SessionId for guest sessions
            if (_sessionCache.TryGetValue(deviceId, out var session))
            {
                return new GuestSessionInfo
                {
                    SessionId = session.SessionId,
                    DeviceId = session.DeviceId,
                    DeviceName = session.DeviceName,
                    IpAddress = session.IpAddress,
                    OperatingSystem = session.OperatingSystem,
                    Browser = session.Browser,
                    CreatedAt = DateTime.SpecifyKind(session.CreatedAt, DateTimeKind.Utc),
                    LastSeenAt = session.LastSeenAt.HasValue ? DateTime.SpecifyKind(session.LastSeenAt.Value, DateTimeKind.Utc) : null,
                    ExpiresAt = DateTime.SpecifyKind(session.ExpiresAt, DateTimeKind.Utc),
                    IsExpired = session.ExpiresAt <= DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAt = session.RevokedAt.HasValue ? DateTime.SpecifyKind(session.RevokedAt.Value, DateTimeKind.Utc) : null,
                    RevokedBy = session.RevokedBy
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Revoke a guest session
    /// </summary>
    public bool RevokeSession(string sessionId, string? revokedBy = null)
    {
        try
        {
            lock (_cacheLock)
            {
                if (_sessionCache.TryGetValue(sessionId, out var session))
                {
                    session.IsRevoked = true;
                    session.RevokedAt = DateTime.UtcNow;
                    session.RevokedBy = revokedBy;
                    SaveGuestSession(session);
                    _logger.LogWarning("Revoked guest session: {SessionId} by {RevokedBy}", sessionId, revokedBy ?? "Unknown");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revoking guest session: {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Permanently delete a guest session
    /// </summary>
    public bool DeleteSession(string sessionId)
    {
        try
        {
            using var context = _contextFactory.CreateDbContext();
            var userSession = context.UserSessions.FirstOrDefault(s => s.SessionId == sessionId && s.IsGuest);

            if (userSession != null)
            {
                context.UserSessions.Remove(userSession);
                context.SaveChanges();

                lock (_cacheLock)
                {
                    _sessionCache.Remove(sessionId);
                }

                _logger.LogInformation("Deleted guest session: {SessionId}", sessionId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting guest session: {SessionId}", sessionId);
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
                    _sessionCache.Remove(session.SessionId);
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
            var existingSession = context.UserSessions.FirstOrDefault(s => s.SessionId == session.SessionId);

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
            }
            else
            {
                // Create new session
                var userSession = new UserSession
                {
                    SessionId = session.SessionId,
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
                    RevokedBy = session.RevokedBy
                };
                context.UserSessions.Add(userSession);
            }

            context.SaveChanges();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving guest session: {SessionId}", session.SessionId);
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
                        SessionId = userSession.SessionId,
                        DeviceId = userSession.SessionId, // Now unified
                        DeviceName = userSession.DeviceName,
                        IpAddress = userSession.IpAddress,
                        OperatingSystem = userSession.OperatingSystem,
                        Browser = userSession.Browser,
                        CreatedAt = DateTime.SpecifyKind(userSession.CreatedAtUtc, DateTimeKind.Utc),
                        ExpiresAt = DateTime.SpecifyKind(
                            userSession.ExpiresAtUtc ?? DateTime.UtcNow.AddHours(GetGuestSessionDurationHours()),
                            DateTimeKind.Utc
                        ),
                        LastSeenAt = DateTime.SpecifyKind(userSession.LastSeenAtUtc, DateTimeKind.Utc),
                        IsRevoked = userSession.IsRevoked,
                        RevokedAt = userSession.RevokedAtUtc.HasValue
                            ? DateTime.SpecifyKind(userSession.RevokedAtUtc.Value, DateTimeKind.Utc)
                            : (DateTime?)null,
                        RevokedBy = userSession.RevokedBy
                    };

                    _sessionCache[session.SessionId] = session;
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
