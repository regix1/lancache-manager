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
    private readonly IConfiguration _configuration;

    private const string CookieName = "LancacheManager.Session";
    // Admin sessions effectively never expire - a far-future ExpiresAtUtc keeps the
    // session valid for the life of the installation and lets the UI render "Never"
    // for any timestamp >= AdminNeverExpiresYear.
    private static readonly DateTime _adminNeverExpiresUtc = new(2099, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    // Process-wide cache of the single admin session minted while authentication is disabled.
    // SessionService is registered scoped, so these MUST be static to survive across requests.
    // Reusing one session (instead of minting per anonymous request) prevents unbounded
    // UserSessions growth from cookie-less callers repeatedly hitting the anonymous
    // /api/auth/status endpoint (OWASP: do not create needless sessions for anonymous users).
    private static (Guid SessionId, string RawToken)? _authDisabledAdminSession;
    private static readonly SemaphoreSlim _authDisabledAdminLock = new(1, 1);

    public SessionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        ILogger<SessionService> logger,
        StateService stateService,
        ISignalRNotificationService signalR,
        IConfiguration configuration)
    {
        _dbContextFactory = dbContextFactory;
        _apiKeyService = apiKeyService;
        _logger = logger;
        _stateService = stateService;
        _signalR = signalR;
        _configuration = configuration;
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

    /// <summary>
    /// True when authentication is enabled (the default). When false (Security:EnableAuthentication=false),
    /// every endpoint allows anonymous access and the frontend is treated as an admin.
    /// </summary>
    public bool IsAuthenticationEnabled()
        => _configuration.GetValue<bool>("Security:EnableAuthentication", true);

    /// <summary>
    /// Returns the shared admin session used when authentication is disabled
    /// (Security:EnableAuthentication=false), creating it once on first use. No API key is required
    /// because authentication is turned off entirely. This exists so that session-scoped surfaces
    /// (SignalR download + prefill-daemon hubs, user preferences, prefill access) have a real session
    /// and cookie to work with under disabled auth, instead of silently failing because the frontend
    /// is told it is an admin while holding no actual credential.
    ///
    /// One session is reused for all anonymous callers (rather than minting per request) so a
    /// cookie-less client cannot flood the UserSessions table. The cached session is revalidated
    /// against the database on every call, so an admin revoking it simply triggers a fresh one.
    /// </summary>
    public async Task<(string RawToken, UserSession Session)> GetOrCreateAuthDisabledAdminSessionAsync(HttpContext httpContext)
    {
        // Defense in depth: never hand out an admin session while authentication is enabled,
        // regardless of caller. The only caller already gates on this, but this guarantees a future
        // caller cannot accidentally turn an auth-disabled helper into an authentication bypass.
        if (IsAuthenticationEnabled())
        {
            throw new InvalidOperationException(
                "GetOrCreateAuthDisabledAdminSessionAsync called while authentication is enabled.");
        }

        // Fast path: reuse the cached session if its row is still valid (no lock needed).
        var reused = await TryReuseAuthDisabledAdminSessionAsync();
        if (reused != null)
        {
            return reused.Value;
        }

        await _authDisabledAdminLock.WaitAsync();
        try
        {
            // Re-check under the lock in case another request created it while we waited.
            reused = await TryReuseAuthDisabledAdminSessionAsync();
            if (reused != null)
            {
                return reused.Value;
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

            _authDisabledAdminSession = (session.Id, rawToken);
            _logger.LogInformation(
                "Created shared auth-disabled admin session {SessionId} (Security:EnableAuthentication=false)",
                session.Id);
            return (rawToken, session);
        }
        finally
        {
            _authDisabledAdminLock.Release();
        }
    }

    /// <summary>
    /// Returns the cached auth-disabled admin session if it still exists and is valid in the database,
    /// else null (signalling the caller to create a fresh one). Keeps a revoked/deleted shared session
    /// from being handed back out as a live credential.
    /// </summary>
    private async Task<(string RawToken, UserSession Session)?> TryReuseAuthDisabledAdminSessionAsync()
    {
        var cached = _authDisabledAdminSession;
        if (cached == null)
        {
            return null;
        }

        var existing = await GetSessionByIdAsync(cached.Value.SessionId);
        if (existing == null || existing.IsRevoked || existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        return (cached.Value.RawToken, existing);
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
    public async Task<List<UserSession>> GetSessionHistoryAsync()
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

    public static string? TokenFromCookie(HttpContext httpContext)
    {
        return httpContext.Request.Cookies[CookieName];
    }

    /// <summary>
    /// Gets session token from cookie first, then falls back to query string access_token.
    /// The query string fallback supports mobile browsers where cookies aren't sent with WebSocket upgrades.
    /// </summary>
    public static string? TokenFromRequest(HttpContext httpContext)
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
        => _stateService.GetGuestSessionDurationHours()
           ?? _configuration.GetValue<int?>("Security:GuestSessionDurationHours")
           ?? 6;

    // True when the runtime value comes from a UI override (state.json), not env/appsettings.
    public bool HasDurationOverride()
        => _stateService.GetGuestSessionDurationHours().HasValue;

    // The env/appsettings-resolved value (ignores any UI override). Used to render
    // "Source: config" labels and the "Reset to default" preview.
    public int GetGuestDurationDefault()
        => _configuration.GetValue<int?>("Security:GuestSessionDurationHours") ?? 6;

    public bool IsGuestModeLocked()
    {
        return _stateService.GetGuestModeLocked();
    }

    public void SetGuestDurationHours(int hours)
    {
        _stateService.SetGuestSessionDurationHours(hours);
        _logger.LogInformation("Guest duration updated to {Hours} hours", hours);
    }

    public void ClearDurationOverride()
    {
        _stateService.SetGuestSessionDurationHours(null);
        _logger.LogInformation("Guest duration UI override cleared; reverting to env/appsettings default");
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

    public bool IsBattleNetPrefillEnabled()
    {
        return _stateService.GetBattleNetGuestPrefillEnabledByDefault();
    }

    public bool IsRiotPrefillEnabled()
    {
        return _stateService.GetRiotGuestPrefillEnabledByDefault();
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

    public async Task GrantBattleNetPrefillAccessAsync(Guid sessionId, int durationHours)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.BattleNetPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await context.SaveChangesAsync();
            _logger.LogInformation("Granted Battle.net prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.BattleNetPrefillExpiresAtUtc);
        }
    }

    public async Task RevokeBattleNetPrefillAccessAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.BattleNetPrefillExpiresAtUtc = null;
            await context.SaveChangesAsync();
            _logger.LogInformation("Revoked Battle.net prefill access for session {SessionId}", sessionId);
        }
    }

    public async Task GrantRiotPrefillAccessAsync(Guid sessionId, int durationHours)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.RiotPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
            await context.SaveChangesAsync();
            _logger.LogInformation("Granted Riot prefill access to session {SessionId}, expires at {ExpiresAt}", sessionId, session.RiotPrefillExpiresAtUtc);
        }
    }

    public async Task RevokeRiotPrefillAccessAsync(Guid sessionId)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session != null)
        {
            session.RiotPrefillExpiresAtUtc = null;
            await context.SaveChangesAsync();
            _logger.LogInformation("Revoked Riot prefill access for session {SessionId}", sessionId);
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
