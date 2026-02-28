using System.Security.Cryptography;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

public class SessionService
{
    private readonly AppDbContext _dbContext;
    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<SessionService> _logger;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _signalR;

    private const string CookieName = "LancacheManager.Session";
    private const int AdminSessionDurationHours = 720; // 30 days

    public SessionService(
        AppDbContext dbContext,
        ApiKeyService apiKeyService,
        ILogger<SessionService> logger,
        StateService stateService,
        ISignalRNotificationService signalR)
    {
        _dbContext = dbContext;
        _apiKeyService = apiKeyService;
        _logger = logger;
        _stateService = stateService;
        _signalR = signalR;
    }

    public async Task<(string RawToken, UserSession Session)?> CreateAdminSessionAsync(string apiKey, HttpContext httpContext)
    {
        if (!_apiKeyService.ValidateApiKey(apiKey))
        {
            return null;
        }

        var (rawToken, tokenHash) = GenerateSessionToken();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = tokenHash,
            SessionType = "admin",
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(AdminSessionDurationHours),
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _dbContext.UserSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created admin session {SessionId} for IP {IP}", session.Id, session.IpAddress);
        return (rawToken, session);
    }

    public async Task<(string RawToken, UserSession Session)?> CreateGuestSessionAsync(HttpContext httpContext)
    {
        if (!IsGuestAccessEnabled())
        {
            return null;
        }

        var (rawToken, tokenHash) = GenerateSessionToken();
        var durationHours = GetGuestDurationHours();

        var session = new UserSession
        {
            Id = Guid.NewGuid(),
            SessionTokenHash = tokenHash,
            SessionType = "guest",
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours),
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        _dbContext.UserSessions.Add(session);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created guest session {SessionId} for IP {IP}, expires in {Hours}h",
            session.Id, session.IpAddress, durationHours);
        return (rawToken, session);
    }

    public async Task<UserSession?> ValidateSessionAsync(string rawToken)
    {
        var tokenHash = HashToken(rawToken);
        var now = DateTime.UtcNow;

        var session = await _dbContext.UserSessions
            .FirstOrDefaultAsync(s => s.SessionTokenHash == tokenHash ||
                (s.PreviousSessionTokenHash == tokenHash && s.PreviousTokenValidUntilUtc > now));

        if (session == null)
            return null;

        if (session.IsRevoked)
            return null;

        if (session.ExpiresAtUtc <= now)
            return null;

        return session;
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session == null)
            return false;

        session.IsRevoked = true;
        session.RevokedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Revoked session {SessionId}", sessionId);
        return true;
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session == null)
            return false;

        _dbContext.UserSessions.Remove(session);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Permanently deleted session {SessionId}", sessionId);
        return true;
    }

    public async Task<int> RevokeAllGuestSessionsAsync()
    {
        var now = DateTime.UtcNow;
        var count = await _dbContext.UserSessions
            .Where(s => s.SessionType == "guest" && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsRevoked, true)
                .SetProperty(x => x.RevokedAtUtc, now));

        _logger.LogInformation("Revoked {Count} guest sessions", count);
        return count;
    }

    public async Task<List<UserSession>> GetActiveSessionsAsync()
    {
        var now = DateTime.UtcNow;
        return await _dbContext.UserSessions
            .Where(s => !s.IsRevoked && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Get all sessions with pagination (includes revoked/expired for admin view).
    /// </summary>
    public async Task<(List<UserSession> Sessions, int TotalCount)> GetAllSessionsPagedAsync(int page, int pageSize)
    {
        var query = _dbContext.UserSessions
            .OrderByDescending(s => s.LastSeenAtUtc);

        var totalCount = await query.CountAsync();
        var sessions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (sessions, totalCount);
    }

    public async Task UpdateLastSeenAsync(UserSession session)
    {
        if ((DateTime.UtcNow - session.LastSeenAtUtc).TotalSeconds < 60)
            return;

        session.LastSeenAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        // Broadcast SessionLastSeenUpdated (already throttled to 60s)
        _signalR.NotifyAllFireAndForget(SignalREvents.SessionLastSeenUpdated, new
        {
            sessionId = session.Id.ToString(),
            lastSeenAt = session.LastSeenAtUtc
        });
    }

    /// <summary>
    /// Rotates the session token, returning the new raw token.
    /// The previous token remains valid for 30 seconds (grace period for concurrent requests/tabs).
    /// Rate-limited: skips rotation if already rotated within the last 30 seconds, returning null.
    /// </summary>
    public async Task<string?> RotateSessionTokenAsync(UserSession session, HttpContext httpContext)
    {
        // Rate-limit: skip if we already rotated recently (previous token still in grace period)
        if (session.PreviousTokenValidUntilUtc > DateTime.UtcNow)
            return null;

        var (newRawToken, newTokenHash) = GenerateSessionToken();

        // Preserve old token hash for grace period
        session.PreviousSessionTokenHash = session.SessionTokenHash;
        session.PreviousTokenValidUntilUtc = DateTime.UtcNow.AddSeconds(30);

        // Set new primary token
        session.SessionTokenHash = newTokenHash;

        await _dbContext.SaveChangesAsync();

        // Update the cookie with the new token
        SetSessionCookie(httpContext, newRawToken, session.ExpiresAtUtc);

        return newRawToken;
    }

    public async Task<int> CleanupExpiredSessionsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var count = await _dbContext.UserSessions
            .Where(s => s.ExpiresAtUtc < cutoff || (s.IsRevoked && s.RevokedAtUtc < cutoff))
            .ExecuteDeleteAsync();

        if (count > 0)
        {
            _logger.LogInformation("Cleaned up {Count} expired/revoked sessions", count);
        }
        return count;
    }

    public void SetSessionCookie(HttpContext httpContext, string rawToken, DateTime expiresAtUtc)
    {
        httpContext.Response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = new DateTimeOffset(expiresAtUtc)
        });
    }

    public void ClearSessionCookie(HttpContext httpContext)
    {
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }

    public static string? GetSessionTokenFromCookie(HttpContext httpContext)
    {
        return httpContext.Request.Cookies[CookieName];
    }

    /// <summary>
    /// Gets session token from cookie first, then falls back to query string access_token.
    /// The query string fallback supports mobile browsers where cookies aren't sent with WebSocket upgrades.
    /// </summary>
    public static string? GetSessionTokenFromRequest(HttpContext httpContext)
    {
        var token = httpContext.Request.Cookies[CookieName];
        if (!string.IsNullOrEmpty(token))
            return token;

        return httpContext.Request.Query["access_token"].FirstOrDefault();
    }

    // --- Guest Configuration (persisted via StateService) ---

    public bool IsGuestAccessEnabled()
    {
        return !_stateService.GetGuestModeLocked();
    }

    public int GetGuestDurationHours()
    {
        return _stateService.GetGuestSessionDurationHours();
    }

    public bool IsGuestModeLocked()
    {
        return _stateService.GetGuestModeLocked();
    }

    public void SetGuestDurationHours(int hours)
    {
        _stateService.SetGuestSessionDurationHours(hours);
        _logger.LogInformation("Guest duration updated to {Hours} hours", hours);
    }

    public void SetGuestModeLocked(bool locked)
    {
        _stateService.SetGuestModeLocked(locked);
        _logger.LogInformation("Guest mode lock set to {Locked}", locked);
    }

    // --- Guest Prefill Configuration (persisted via StateService) ---

    public bool IsSteamPrefillEnabled()
    {
        return _stateService.GetGuestPrefillEnabledByDefault();
    }

    public bool IsEpicPrefillEnabled()
    {
        return _stateService.GetEpicGuestPrefillEnabledByDefault();
    }

    public void SetSteamGuestPrefillEnabled(bool enabled)
    {
        _stateService.SetGuestPrefillEnabledByDefault(enabled);
        _logger.LogInformation("Guest Steam prefill set to {Enabled}", enabled);
    }

    public int GetGuestPrefillDurationHours()
    {
        return _stateService.GetGuestPrefillDurationHours();
    }

    public void SetGuestPrefillDurationHours(int hours)
    {
        _stateService.SetGuestPrefillDurationHours(hours);
        _logger.LogInformation("Guest prefill duration updated to {Hours} hours", hours);
    }

    // --- Per-Session Prefill Grants ---

    public async Task GrantSteamPrefillAccessAsync(Guid sessionId, int durationHours)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.SteamPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Granted Steam prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.SteamPrefillExpiresAtUtc);
        }
    }

    public async Task GrantEpicPrefillAccessAsync(Guid sessionId, int durationHours)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.EpicPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Granted Epic prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.EpicPrefillExpiresAtUtc);
        }
    }

    public async Task RevokeSteamPrefillAccessAsync(Guid sessionId)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.SteamPrefillExpiresAtUtc = null;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Revoked Steam prefill access for session {SessionId}", sessionId);
        }
    }

    public async Task RevokeEpicPrefillAccessAsync(Guid sessionId)
    {
        var session = await _dbContext.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.EpicPrefillExpiresAtUtc = null;
            await _dbContext.SaveChangesAsync();
            _logger.LogInformation("Revoked Epic prefill access for session {SessionId}", sessionId);
        }
    }

    private static (string RawToken, string TokenHash) GenerateSessionToken()
    {
        var tokenBytes = new byte[64];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(tokenBytes);

        var rawToken = Convert.ToBase64String(tokenBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        var tokenHash = HashToken(rawToken);
        return (rawToken, tokenHash);
    }

    private static string HashToken(string rawToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
