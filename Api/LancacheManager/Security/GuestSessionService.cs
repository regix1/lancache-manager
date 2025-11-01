using System.Text.Json;
using LancacheManager.Infrastructure.Services.Interfaces;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Security;

public class GuestSessionService
{
    private readonly ILogger<GuestSessionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly string _sessionsDirectory;
    private readonly Dictionary<string, GuestSession> _sessionCache = new();
    private readonly object _cacheLock = new();

    public GuestSessionService(ILogger<GuestSessionService> logger, IPathResolver pathResolver)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _sessionsDirectory = Path.Combine(_pathResolver.GetDevicesDirectory(), "guest_sessions");

        // Create directory if it doesn't exist
        if (!Directory.Exists(_sessionsDirectory))
        {
            Directory.CreateDirectory(_sessionsDirectory);
            _logger.LogInformation("Created guest sessions directory: {Directory}", _sessionsDirectory);
        }

        LoadGuestSessions();
    }

    public class GuestSession
    {
        public string SessionId { get; set; } = string.Empty;
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
            var session = new GuestSession
            {
                SessionId = request.SessionId,
                DeviceName = request.DeviceName,
                IpAddress = ipAddress,
                OperatingSystem = request.OperatingSystem,
                Browser = request.Browser,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddHours(6),
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
                    DeviceName = session.DeviceName,
                    IpAddress = session.IpAddress,
                    OperatingSystem = session.OperatingSystem,
                    Browser = session.Browser,
                    CreatedAt = session.CreatedAt,
                    LastSeenAt = session.LastSeenAt,
                    ExpiresAt = session.ExpiresAt,
                    IsExpired = session.ExpiresAt <= DateTime.UtcNow,
                    IsRevoked = session.IsRevoked,
                    RevokedAt = session.RevokedAt,
                    RevokedBy = session.RevokedBy
                });
            }
        }

        return sessions.OrderByDescending(s => s.LastSeenAt ?? s.CreatedAt).ToList();
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
            lock (_cacheLock)
            {
                // Remove from cache
                _sessionCache.Remove(sessionId);

                // Delete file from disk
                var filePath = GetSessionFilePath(sessionId);
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogInformation("Deleted guest session: {SessionId}", sessionId);
                    return true;
                }
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
            lock (_cacheLock)
            {
                var expiredSessions = _sessionCache.Values
                    .Where(s => s.ExpiresAt < cutoffDate)
                    .Select(s => s.SessionId)
                    .ToList();

                foreach (var sessionId in expiredSessions)
                {
                    _sessionCache.Remove(sessionId);
                    var filePath = GetSessionFilePath(sessionId);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        count++;
                    }
                }
            }

            if (count > 0)
            {
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
            var filePath = GetSessionFilePath(session.SessionId);
            var json = JsonSerializer.Serialize(session, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
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
            if (!Directory.Exists(_sessionsDirectory))
            {
                return;
            }

            var files = Directory.GetFiles(_sessionsDirectory, "*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var session = JsonSerializer.Deserialize<GuestSession>(json);

                    if (session != null)
                    {
                        lock (_cacheLock)
                        {
                            _sessionCache[session.SessionId] = session;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load guest session file: {File}", file);
                }
            }

            _logger.LogInformation("Loaded {Count} guest sessions from disk", _sessionCache.Count);

            // Clean up old sessions on startup
            CleanupExpiredSessions();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading guest sessions");
        }
    }

    private string GetSessionFilePath(string sessionId)
    {
        var safeFileName = string.Concat(sessionId.Where(c => char.IsLetterOrDigit(c) || c == '-'));
        return Path.Combine(_sessionsDirectory, $"{safeFileName}.json");
    }
}
