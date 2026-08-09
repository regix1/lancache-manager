using System.Net;
using System.Security.Cryptography;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.StatusCheck;
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

    // Optional (like ServiceScheduleRegistry's _tracker) so unit tests that construct the service
    // directly keep compiling; at runtime DI always supplies the singleton. Each session create/revoke/
    // delete mirrors the session's presence into the unified activity registry so the Active Sessions
    // status dots read the one ActivityUpdated event.
    private readonly IActivityRegistry? _activityRegistry;

    // Optional for the same reason as _activityRegistry: unit tests construct this service directly and
    // do not all care about the guest defaults. At runtime DI always supplies the singleton.
    private readonly UserPreferencesService? _userPreferences;

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

    // While the database cannot be reached, every request that arrives without a usable cookie lands on
    // the shared session and repeats the same failing read/create and the same error line. One outage then
    // costs a database round trip per request and fills the log with copies of a single fault. The first
    // failure is still reported in full; the ones inside the next few seconds are refused before the
    // database is touched. The gate is cleared by the first attempt after it rather than by a timer, so
    // the service comes back on its own once the database does. [14]
    private static readonly TimeSpan _authDisabledRetryDelay = TimeSpan.FromSeconds(5);
    private static DateTime _authDisabledRetryAfterUtc = DateTime.MinValue;

    public SessionService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ApiKeyService apiKeyService,
        ILogger<SessionService> logger,
        StateService stateService,
        ISignalRNotificationService signalR,
        IConfiguration configuration,
        IActivityRegistry? activityRegistry = null,
        UserPreferencesService? userPreferences = null)
    {
        _dbContextFactory = dbContextFactory;
        _apiKeyService = apiKeyService;
        _logger = logger;
        _stateService = stateService;
        _signalR = signalR;
        _configuration = configuration;
        _activityRegistry = activityRegistry;
        _userPreferences = userPreferences;
    }

    // Marks a session present or absent in the unified activity registry. Best-effort: the registry
    // swallows its own broadcast failures, so a presence update never disrupts the session operation.
    private Task ReportSessionPresenceAsync(Guid sessionId, bool present)
    {
        if (_activityRegistry is null)
        {
            return Task.CompletedTask;
        }

        return _activityRegistry.ReportAsync(
            ActivityDomains.UserSession, sessionId.ToString(), ActivityAspects.Present, present);
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
        await ReportSessionPresenceAsync(session.Id, true);
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
    ///
    /// Returns null when the database could not answer, so the caller carries on unauthenticated
    /// instead of failing outright. Repeated attempts inside <see cref="_authDisabledRetryDelay"/> of a
    /// failure are refused without touching the database. [14]
    /// </summary>
    public async Task<(string RawToken, UserSession Session)?> GetOrCreateAuthDisabledAdminSessionAsync(HttpContext httpContext)
    {
        // Defense in depth: never hand out an admin session while authentication is enabled,
        // regardless of caller. The only caller already gates on this, but this guarantees a future
        // caller cannot accidentally turn an auth-disabled helper into an authentication bypass.
        if (IsAuthenticationEnabled())
        {
            throw new InvalidOperationException(
                "GetOrCreateAuthDisabledAdminSessionAsync called while authentication is enabled.");
        }

        // Every database attempt is serialized. In particular, a request that passed the hold-off before
        // waiting must check it again after the request ahead of it records a failure. Keeping the failure
        // write inside this lock makes that handoff deterministic: the next waiter observes the hold-off
        // before it can repeat the same database call. [14]
        await _authDisabledAdminLock.WaitAsync();
        try
        {
            var heldOffUntil = _authDisabledRetryAfterUtc;
            if (DateTime.UtcNow < heldOffUntil)
            {
                _logger.LogDebug(
                    "Skipped the shared auth-disabled session: the last attempt failed and the next one is not due until {RetryAt}",
                    heldOffUntil);
                return null;
            }

            try
            {
                var reused = await TryReuseAuthDisabledAdminSessionAsync();
                if (reused != null)
                {
                    _authDisabledRetryAfterUtc = DateTime.MinValue;
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
                _authDisabledRetryAfterUtc = DateTime.MinValue;
                _logger.LogInformation(
                    "Created shared auth-disabled admin session {SessionId} (Security:EnableAuthentication=false)",
                    session.Id);
                await ReportSessionPresenceAsync(session.Id, true);
                return (rawToken, session);
            }
            catch (Exception ex)
            {
                // Deliberately catching everything: nothing in the stack tells a server that cannot be
                // reached apart from any other provider fault, and every one of them leaves this request
                // with no session either way. The first report carries the exception in full so the cause
                // is not lost; the ones held off behind it say only that they were skipped. [14]
                var retryAt = DateTime.UtcNow + _authDisabledRetryDelay;
                _authDisabledRetryAfterUtc = retryAt;
                _logger.LogError(
                    ex,
                    "Could not resolve the shared session used while authentication is disabled; the next attempt is held off until {RetryAt}",
                    retryAt);
                return null;
            }
        }
        finally
        {
            _authDisabledAdminLock.Release();
        }
    }

    /// <summary>
    /// Keeps the cached shared auth-disabled token equal to the one now stored. Callers holding no cookie
    /// (the hubs reading access_token, and any request that arrives without one) are handed this cached
    /// token, so leaving the rotated-away token here means they authenticate only until the previous
    /// token's 30-second grace runs out and are rejected from then on. [1]
    /// </summary>
    private static async Task ReplaceAuthDisabledAdminTokenAsync(Guid sessionId, string rawToken)
    {
        if (_authDisabledAdminSession?.SessionId != sessionId)
        {
            return;
        }

        await _authDisabledAdminLock.WaitAsync();
        try
        {
            // Re-check under the lock: the cached session can be replaced wholesale while this waits,
            // and that newer session's token must not be overwritten with this rotation's.
            if (_authDisabledAdminSession?.SessionId == sessionId)
            {
                _authDisabledAdminSession = (sessionId, rawToken);
            }
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
        await SeedGuestDefaultsAsync(session.Id);
        await ReportSessionPresenceAsync(session.Id, true);
        return (rawToken, session);
    }

    /// <summary>
    /// Gives a brand-new guest session the defaults an admin chose, so someone logging in after the change
    /// sees it instead of the built-in values. The write is the preferences service's own and swallows its
    /// failures there, so this can never stop a session being created or a login completing. [7]
    /// </summary>
    private async Task SeedGuestDefaultsAsync(Guid sessionId)
    {
        if (_userPreferences is null)
        {
            return;
        }

        try
        {
            var state = _stateService.GetState();
            await _userPreferences.SeedGuestDefaultsAsync(sessionId, new UserPreferencesService.UserPreferencesDto
            {
                UseLocalTimezone = state.DefaultGuestUseLocalTimezone,
                UseUtcTimezone = state.DefaultGuestUseUtcTimezone,
                Use24HourFormat = state.DefaultGuestUse24HourFormat,
                SharpCorners = state.DefaultGuestSharpCorners,
                DisableTooltips = state.DefaultGuestDisableTooltips,
                ShowDatasourceLabels = state.DefaultGuestShowDatasourceLabels
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not apply guest defaults to session {SessionId}", sessionId);
        }
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
        await ReportSessionPresenceAsync(sessionId, false);
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
        await ReportSessionPresenceAsync(sessionId, false);
        return true;
    }

    public async Task<int> RevokeAllGuestSessionsAsync()
    {
        var now = DateTime.UtcNow;
        using var context = _dbContextFactory.CreateDbContext();

        // Capture the ids being revoked up front so their presence can be cleared individually — the
        // bulk ExecuteUpdateAsync returns only a row count, not the affected keys.
        var revokedIds = await context.UserSessions
            .Where(s => s.SessionType == SessionType.Guest && !s.IsRevoked)
            .Select(s => s.Id)
            .ToListAsync();

        var count = await context.UserSessions
            .Where(s => s.SessionType == SessionType.Guest && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IsRevoked, true)
                .SetProperty(x => x.RevokedAtUtc, now));

        _logger.LogInformation("Revoked {Count} guest sessions", count);
        foreach (var id in revokedIds)
        {
            await ReportSessionPresenceAsync(id, false);
        }
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

        // Every session is gone, so clear the entire user-session presence set in one snapshot.
        if (_activityRegistry is not null)
        {
            await _activityRegistry.ReplaceAsync(
                ActivityDomains.UserSession,
                ActivityAspects.Present,
                new Dictionary<string, int>(StringComparer.Ordinal));
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
    /// Persist the session's public IP, the browser-reported locale/screen fields and the
    /// GeoIP-resolved country/city/ISP. The public IP is resolved server-side (the request's
    /// remote address, falling back to PublicIpLookupService) rather than reported by the
    /// browser. Silently no-ops if the session no longer exists or is revoked.
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

        // A lookup that comes back empty keeps the stored address only while that address is
        // still a public one. Anything else is cleared, so a private value written before the
        // classifier rejected mapped LAN ranges cannot survive on a box where the outbound
        // lookup is always blocked.
        persisted.PublicIpAddress = publicIpAddress ??
            (IPAddress.TryParse(persisted.PublicIpAddress, out var storedAddress) && PublicAddressSafety.IsPublic(storedAddress)
                ? persisted.PublicIpAddress
                : null);
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

        await ReplaceAuthDisabledAdminTokenAsync(session.Id, newRawToken);

        return newRawToken;
    }

    public void SetSessionCookie(HttpContext httpContext, string rawToken, DateTime expiresAtUtc)
    {
        // Default-false opt-in: when true, force the Secure flag even on plain-HTTP requests
        // (e.g. behind a TLS-terminating reverse proxy). Defaults to false so plain-HTTP LAN
        // deployments keep working — the cookie is only marked Secure on real HTTPS requests.
        var forceSecure = _configuration.GetValue<bool>("Security:ForceSecureCookies");
        httpContext.Response.Cookies.Append(CookieName, rawToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = forceSecure || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = new DateTimeOffset(expiresAtUtc)
        });
    }

    public void ClearSessionCookie(HttpContext httpContext)
    {
        var forceSecure = _configuration.GetValue<bool>("Security:ForceSecureCookies");
        httpContext.Response.Cookies.Delete(CookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = forceSecure || httpContext.Request.IsHttps,
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

    /// <summary>
    /// Grant-only: active guest sessions with null or expired prefill for the given service.
    /// Never revokes and never modifies an unexpired grant.
    /// </summary>
    public async Task<IReadOnlyList<GuestPrefillGrantResult>> GrantDefaultPrefillToEligibleGuestSessionsAsync(
        PrefillPlatform service,
        int durationHours)
    {
        var now = DateTime.UtcNow;
        var expiresAt = now.AddHours(durationHours);

        using var context = _dbContextFactory.CreateDbContext();
        var guests = await context.UserSessions
            .Where(s => s.SessionType == SessionType.Guest
                     && !s.IsRevoked
                     && s.ExpiresAtUtc > now)
            .ToListAsync();

        var grants = new List<GuestPrefillGrantResult>();
        foreach (var session in guests)
        {
            var current = GetPrefillExpiresAt(session, service);
            if (current != null && current > now)
                continue;

            SetPrefillExpiresAt(session, service, expiresAt);
            grants.Add(new GuestPrefillGrantResult(session.Id, expiresAt));
        }

        if (grants.Count > 0)
            await context.SaveChangesAsync();

        _logger.LogInformation(
            "Default {Service} prefill granted to {Count} eligible guest session(s), duration={Hours}h",
            service, grants.Count, durationHours);

        return grants;
    }

    internal static DateTime? GetPrefillExpiresAt(UserSession session, PrefillPlatform service) =>
        service switch
        {
            PrefillPlatform.Steam => session.SteamPrefillExpiresAtUtc,
            PrefillPlatform.Epic => session.EpicPrefillExpiresAtUtc,
            PrefillPlatform.BattleNet => session.BattleNetPrefillExpiresAtUtc,
            PrefillPlatform.Riot => session.RiotPrefillExpiresAtUtc,
            PrefillPlatform.Xbox => session.XboxPrefillExpiresAtUtc,
            _ => null
        };

    private static void SetPrefillExpiresAt(UserSession session, PrefillPlatform service, DateTime? expiresAtUtc)
    {
        switch (service)
        {
            case PrefillPlatform.Steam:
                session.SteamPrefillExpiresAtUtc = expiresAtUtc;
                break;
            case PrefillPlatform.Epic:
                session.EpicPrefillExpiresAtUtc = expiresAtUtc;
                break;
            case PrefillPlatform.BattleNet:
                session.BattleNetPrefillExpiresAtUtc = expiresAtUtc;
                break;
            case PrefillPlatform.Riot:
                session.RiotPrefillExpiresAtUtc = expiresAtUtc;
                break;
            case PrefillPlatform.Xbox:
                session.XboxPrefillExpiresAtUtc = expiresAtUtc;
                break;
        }
    }

    public Task GrantSteamPrefillAccessAsync(Guid sessionId, int durationHours) =>
        GrantPrefillAccessAsync(sessionId, durationHours, PrefillPlatform.Steam, "Steam");

    public Task GrantEpicPrefillAccessAsync(Guid sessionId, int durationHours) =>
        GrantPrefillAccessAsync(sessionId, durationHours, PrefillPlatform.Epic, "Epic");

    public Task RevokeSteamPrefillAccessAsync(Guid sessionId) =>
        RevokePrefillAccessAsync(sessionId, PrefillPlatform.Steam, "Steam");

    public Task RevokeEpicPrefillAccessAsync(Guid sessionId) =>
        RevokePrefillAccessAsync(sessionId, PrefillPlatform.Epic, "Epic");

    public Task GrantBattleNetPrefillAccessAsync(Guid sessionId, int durationHours) =>
        GrantPrefillAccessAsync(sessionId, durationHours, PrefillPlatform.BattleNet, "Battle.net");

    public Task RevokeBattleNetPrefillAccessAsync(Guid sessionId) =>
        RevokePrefillAccessAsync(sessionId, PrefillPlatform.BattleNet, "Battle.net");

    public Task GrantRiotPrefillAccessAsync(Guid sessionId, int durationHours) =>
        GrantPrefillAccessAsync(sessionId, durationHours, PrefillPlatform.Riot, "Riot");

    public Task RevokeRiotPrefillAccessAsync(Guid sessionId) =>
        RevokePrefillAccessAsync(sessionId, PrefillPlatform.Riot, "Riot");

    public Task GrantXboxPrefillAccessAsync(Guid sessionId, int durationHours) =>
        GrantPrefillAccessAsync(sessionId, durationHours, PrefillPlatform.Xbox, "Xbox");

    public Task RevokeXboxPrefillAccessAsync(Guid sessionId) =>
        RevokePrefillAccessAsync(sessionId, PrefillPlatform.Xbox, "Xbox");

    private async Task GrantPrefillAccessAsync(
        Guid sessionId,
        int durationHours,
        PrefillPlatform platform,
        string platformName)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session == null)
            return;

        var expiresAtUtc = DateTime.UtcNow.AddHours(durationHours);
        SetPrefillExpiresAt(session, platform, expiresAtUtc);
        await context.SaveChangesAsync();
        _logger.LogInformation(
            "Granted {Platform} prefill access to session {SessionId}, expires at {ExpiresAt}",
            platformName,
            sessionId,
            expiresAtUtc);
    }

    private async Task RevokePrefillAccessAsync(
        Guid sessionId,
        PrefillPlatform platform,
        string platformName)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FindAsync(sessionId);
        if (session == null)
            return;

        SetPrefillExpiresAt(session, platform, null);
        await context.SaveChangesAsync();
        _logger.LogInformation(
            "Revoked {Platform} prefill access for session {SessionId}",
            platformName,
            sessionId);
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
