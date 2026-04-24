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
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ApiKeyService _apiKeyService;
    private readonly ILogger<SessionService> _logger;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _signalR;

    private const string CookieName = "LancacheManager.Session";
    // Admin sessions effectively never expire — a far-future ExpiresAtUtc keeps the
    // session valid for the life of the installation and lets the UI render "Never"
    // for any timestamp >= AdminNeverExpiresYear.
    private static readonly DateTime _adminNeverExpiresUtc = new(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public SessionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        ILogger<SessionService> logger,
        StateService stateService,
        ISignalRNotificationService signalR)
    {
        _dbContextFactory = dbContextFactory;
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
            SessionType = SessionType.Admin,
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = _adminNeverExpiresUtc,
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        using var context = _dbContextFactory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();

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
            SessionType = SessionType.Guest,
            IpAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent = httpContext.Request.Headers.UserAgent.ToString(),
            CreatedAtUtc = DateTime.UtcNow,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours),
            LastSeenAtUtc = DateTime.UtcNow,
            IsRevoked = false
        };

        using var context = _dbContextFactory.CreateDbContext();
        context.UserSessions.Add(session);
        await context.SaveChangesAsync();

        _logger.LogInformation("Created guest session {SessionId} for IP {IP}, expires in {Hours}h",
            session.Id, session.IpAddress, durationHours);
        return (rawToken, session);
    }

    public async Task<UserSession?> ValidateSessionAsync(string rawToken)
    {
        var tokenHash = HashToken(rawToken);
        var now = DateTime.UtcNow;

        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SessionTokenHash == tokenHash ||
                (s.PreviousSessionTokenHash == tokenHash && s.PreviousTokenValidUntilUtc > now));

        if (session == null)
            return null;

        if (session.IsRevoked)
            return null;

        if (session.ExpiresAtUtc <= now)
            return null;

        // Backfill: pre-existing admin sessions created before the never-expires change
        // still have a 30-day expiry. Bump them on the first validated request so the
        // UI no longer shows a countdown for them.
        if (session.SessionType == SessionType.Admin && session.ExpiresAtUtc < _adminNeverExpiresUtc)
        {
            await using var updateContext = _dbContextFactory.CreateDbContext();
            var tracked = await updateContext.UserSessions.FindAsync(session.Id);
            if (tracked != null && tracked.SessionType == SessionType.Admin && tracked.ExpiresAtUtc < _adminNeverExpiresUtc)
            {
                tracked.ExpiresAtUtc = _adminNeverExpiresUtc;
                await updateContext.SaveChangesAsync();
                session.ExpiresAtUtc = _adminNeverExpiresUtc;
            }
        }

        return session;
    }

    public async Task<bool> RevokeSessionAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session == null)
            return false;

        session.IsRevoked = true;
        session.RevokedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();

        _logger.LogInformation("Revoked session {SessionId}", sessionId);
        return true;
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session == null)
            return false;

        context.UserSessions.Remove(session);
        await context.SaveChangesAsync();

        _logger.LogInformation("Permanently deleted session {SessionId}", sessionId);
        return true;
    }

    public async Task<int> RevokeAllGuestSessionsAsync()
    {
        var now = DateTime.UtcNow;
        using var context = _dbContextFactory.CreateDbContext();
        var count = await context.UserSessions
            .Where(s => s.SessionType == SessionType.Guest && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsRevoked, true)
                .SetProperty(x => x.RevokedAtUtc, now));

        _logger.LogInformation("Revoked {Count} guest sessions", count);
        return count;
    }

    /// <summary>
    /// Deletes all sessions from PostgreSQL. Called when a new API key is generated on startup
    /// (e.g. after data folder deletion) so old browser cookies can no longer authenticate.
    /// </summary>
    public async Task<int> ClearAllSessionsAsync()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var count = await context.UserSessions.ExecuteDeleteAsync();

        if (count > 0)
        {
            _logger.LogWarning("Cleared all {Count} sessions from the database because a new API key was generated. All clients must log in again.", count);
        }
        return count;
    }

    public async Task<List<UserSession>> GetActiveSessionsAsync()
    {
        var now = DateTime.UtcNow;
        using var context = _dbContextFactory.CreateDbContext();
        return await context.UserSessions
            .AsNoTracking()
            .Where(s => !s.IsRevoked && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc)
            .ToListAsync();
    }

    /// <summary>
    /// Get active sessions with pagination (excludes revoked/expired).
    /// </summary>
    public async Task<(List<UserSession> Sessions, int TotalCount)> GetActiveSessionsPagedAsync(int page, int pageSize)
    {
        var now = DateTime.UtcNow;
        using var context = _dbContextFactory.CreateDbContext();
        var query = context.UserSessions
            .AsNoTracking()
            .Where(s => !s.IsRevoked && s.ExpiresAtUtc > now)
            .OrderByDescending(s => s.LastSeenAtUtc);

        var totalCount = await query.CountAsync();
        var sessions = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (sessions, totalCount);
    }

    /// <summary>
    /// Get all revoked or expired sessions (for session history display).
    /// </summary>
    public async Task<List<UserSession>> GetHistorySessionsAsync()
    {
        var now = DateTime.UtcNow;
        using var context = _dbContextFactory.CreateDbContext();
        return await context.UserSessions
            .AsNoTracking()
            .Where(s => s.IsRevoked || s.ExpiresAtUtc <= now)
            .OrderByDescending(s => s.RevokedAtUtc ?? s.ExpiresAtUtc)
            .ToListAsync();
    }

    public async Task<UserSession?> GetSessionByIdAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        return await context.UserSessions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId);
    }

    public async Task UpdateLastSeenAsync(UserSession session)
    {
        var now = DateTime.UtcNow;
        if ((now - session.LastSeenAtUtc).TotalSeconds < 60)
            return;

        using var context = _dbContextFactory.CreateDbContext();
        var persistedSession = await context.UserSessions.FindAsync(session.Id);
        if (persistedSession == null || persistedSession.IsRevoked || persistedSession.ExpiresAtUtc <= now)
            return;

        if ((now - persistedSession.LastSeenAtUtc).TotalSeconds < 60)
        {
            session.LastSeenAtUtc = persistedSession.LastSeenAtUtc;
            return;
        }

        persistedSession.LastSeenAtUtc = now;
        await context.SaveChangesAsync();
        session.LastSeenAtUtc = now;

        // Broadcast SessionLastSeenUpdated (already throttled to 60s)
        _signalR.NotifyAllFireAndForget(SignalREvents.SessionLastSeenUpdated, new
        {
            sessionId = session.Id.ToString(),
            lastSeenAt = now
        });
    }

    /// <summary>
    /// Persist browser-reported client info (public IP, locale, screen) plus
    /// GeoIP-resolved country/city/ISP for the given session. Silently no-ops
    /// if the session no longer exists or is revoked.
    /// </summary>
    public async Task UpdateClientInfoAsync(
        Guid sessionId,
        string? publicIpAddress,
        string? countryCode,
        string? countryName,
        string? regionName,
        string? city,
        string? timezone,
        string? ispName,
        string? screenResolution,
        string? browserLanguage)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var persisted = await context.UserSessions.FindAsync(sessionId);
        if (persisted == null || persisted.IsRevoked) return;

        persisted.PublicIpAddress = publicIpAddress;
        persisted.CountryCode = countryCode;
        persisted.CountryName = countryName;
        persisted.RegionName = regionName;
        persisted.City = city;
        persisted.Timezone = timezone;
        persisted.IspName = ispName;
        persisted.ScreenResolution = screenResolution;
        persisted.BrowserLanguage = browserLanguage;
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Rotates the session token, returning the new raw token.
    /// The previous token remains valid for 30 seconds (grace period for concurrent requests/tabs).
    /// Rate-limited: skips rotation if already rotated within the last 30 seconds, returning null.
    /// </summary>
    public async Task<string?> RotateSessionTokenAsync(UserSession session, HttpContext httpContext)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var persistedSession = await context.UserSessions.FindAsync(session.Id);
        if (persistedSession == null)
            return null;

        var now = DateTime.UtcNow;

        // Rate-limit: skip if we already rotated recently (previous token still in grace period)
        if (persistedSession.PreviousTokenValidUntilUtc > now)
            return null;

        var (newRawToken, newTokenHash) = GenerateSessionToken();

        // Preserve old token hash for grace period
        persistedSession.PreviousSessionTokenHash = persistedSession.SessionTokenHash;
        persistedSession.PreviousTokenValidUntilUtc = now.AddSeconds(30);

        // Set new primary token
        persistedSession.SessionTokenHash = newTokenHash;

        await context.SaveChangesAsync();

        session.PreviousSessionTokenHash = persistedSession.PreviousSessionTokenHash;
        session.PreviousTokenValidUntilUtc = persistedSession.PreviousTokenValidUntilUtc;
        session.SessionTokenHash = persistedSession.SessionTokenHash;

        // Update the cookie with the new token
        SetSessionCookie(httpContext, newRawToken, session.ExpiresAtUtc);

        return newRawToken;
    }

    public async Task<int> CleanupExpiredSessionsAsync()
    {
        var cutoff = DateTime.UtcNow.AddDays(-7);
        using var context = _dbContextFactory.CreateDbContext();
        var count = await context.UserSessions
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
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.SteamPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await context.SaveChangesAsync();
            _logger.LogInformation("Granted Steam prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.SteamPrefillExpiresAtUtc);
        }
    }

    public async Task GrantEpicPrefillAccessAsync(Guid sessionId, int durationHours)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.EpicPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await context.SaveChangesAsync();
            _logger.LogInformation("Granted Epic prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.EpicPrefillExpiresAtUtc);
        }
    }

    public async Task RevokeSteamPrefillAccessAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.SteamPrefillExpiresAtUtc = null;
            await context.SaveChangesAsync();
            _logger.LogInformation("Revoked Steam prefill access for session {SessionId}", sessionId);
        }
    }

    public async Task RevokeEpicPrefillAccessAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.EpicPrefillExpiresAtUtc = null;
            await context.SaveChangesAsync();
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
