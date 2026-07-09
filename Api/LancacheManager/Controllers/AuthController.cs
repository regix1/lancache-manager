using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/auth")]
[Authorize]
public class AuthController : ControllerBase
{
    private readonly SessionService _sessionService;
    private readonly ILogger<AuthController> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly StateService _stateService;
    private readonly ISignalRNotificationService _signalR;

    public AuthController(
        SessionService sessionService,
        ILogger<AuthController> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        StateService stateService,
        ISignalRNotificationService signalR)
    {
        _sessionService = sessionService;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _stateService = stateService;
        _signalR = signalR;
    }

    [AllowAnonymous]
    [HttpGet("status")]
    public async Task<IActionResult> GetStatusAsync()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        var session = HttpContext.GetUserSession();

        // When authentication is disabled via config, the frontend is told it is an admin, but no
        // real session/cookie is ever created. Every session-scoped surface (SignalR download +
        // prefill-daemon hubs, user preferences, prefill access) then silently fails: the hubs reject
        // the connection "without session" and preferences return 400. Mint a real admin session (and
        // set its cookie) on first contact so those features work under disabled auth. Subsequent
        // requests carry the cookie, so GetUserSession() resolves it and we do not mint again.
        var authenticationEnabled = _sessionService.IsAuthenticationEnabled();
        if (!authenticationEnabled && session == null)
        {
            var (rawToken, adminSession) = await _sessionService.GetOrCreateAuthDisabledAdminSessionAsync(HttpContext);
            _sessionService.SetSessionCookie(HttpContext, rawToken, adminSession.ExpiresAtUtc);
            session = adminSession;
        }

        bool hasData = false;
        bool hasBeenInitialized = false;
        bool hasDataLoaded = false;

        try
        {
            using var context = _dbContextFactory.CreateDbContext();
            hasData = await context.Downloads.AnyAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if database has data"); }

        try { hasBeenInitialized = _stateService.GetSetupCompleted(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if setup has been completed"); }

        try { hasDataLoaded = _stateService.HasDataLoaded(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if data has been loaded"); }

        // Determine per-service prefill access
        var steamPrefillEnabled = false;
        DateTime? steamPrefillExpiresAt = null;
        var epicPrefillEnabled = false;
        DateTime? epicPrefillExpiresAt = null;
        var battlenetPrefillEnabled = false;
        DateTime? battlenetPrefillExpiresAt = null;
        var riotPrefillEnabled = false;
        DateTime? riotPrefillExpiresAt = null;
        var xboxPrefillEnabled = false;
        DateTime? xboxPrefillExpiresAt = null;

        if (session != null)
        {
            if (session.SessionType == SessionType.Admin)
            {
                steamPrefillEnabled = true;
                epicPrefillEnabled = true;
                battlenetPrefillEnabled = true;
                riotPrefillEnabled = true;
                xboxPrefillEnabled = true;
            }
            else if (session.SessionType == SessionType.Guest)
            {
                steamPrefillEnabled = session.SteamPrefillExpiresAtUtc != null && session.SteamPrefillExpiresAtUtc > DateTime.UtcNow;
                steamPrefillExpiresAt = steamPrefillEnabled
                    ? DateTime.SpecifyKind(session.SteamPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null;

                epicPrefillEnabled = session.EpicPrefillExpiresAtUtc != null && session.EpicPrefillExpiresAtUtc > DateTime.UtcNow;
                epicPrefillExpiresAt = epicPrefillEnabled
                    ? DateTime.SpecifyKind(session.EpicPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null;

                battlenetPrefillEnabled = session.BattleNetPrefillExpiresAtUtc != null && session.BattleNetPrefillExpiresAtUtc > DateTime.UtcNow;
                battlenetPrefillExpiresAt = battlenetPrefillEnabled
                    ? DateTime.SpecifyKind(session.BattleNetPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null;

                riotPrefillEnabled = session.RiotPrefillExpiresAtUtc != null && session.RiotPrefillExpiresAtUtc > DateTime.UtcNow;
                riotPrefillExpiresAt = riotPrefillEnabled
                    ? DateTime.SpecifyKind(session.RiotPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null;

                xboxPrefillEnabled = session.XboxPrefillExpiresAtUtc != null && session.XboxPrefillExpiresAtUtc > DateTime.UtcNow;
                xboxPrefillExpiresAt = xboxPrefillEnabled
                    ? DateTime.SpecifyKind(session.XboxPrefillExpiresAtUtc!.Value, DateTimeKind.Utc) : null;
            }
        }

        // Backward-compat: prefillEnabled is true if any service is active
        var prefillEnabled = steamPrefillEnabled || epicPrefillEnabled || battlenetPrefillEnabled || riotPrefillEnabled || xboxPrefillEnabled;

        // Token rotation: provide a fresh token for SignalR accessTokenFactory (mobile support)
        string? token = null;
        if (session != null)
        {
            var rotatedToken = await _sessionService.RotateSessionTokenAsync(session, HttpContext);
            token = rotatedToken ?? SessionService.TokenFromCookie(HttpContext);
        }

        // authenticationEnabled was resolved above (it gates the admin-session mint). When disabled,
        // the minted session makes IsAuthenticated/SessionType below resolve to a real admin session.
        return Ok(new AuthStatusResponse
        {
            AuthenticationEnabled = authenticationEnabled,
            IsAuthenticated = !authenticationEnabled || session != null,
            SessionType = !authenticationEnabled ? Models.SessionType.Admin : session?.SessionType,
            SessionId = session?.Id,
            ExpiresAt = session != null ? DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc) : (DateTime?)null,
            HasData = hasData,
            HasBeenInitialized = hasBeenInitialized,
            HasDataLoaded = hasDataLoaded,
            GuestAccessEnabled = _sessionService.IsGuestAccessEnabled(),
            GuestDurationHours = _sessionService.GetGuestDurationHours(),
            PrefillEnabled = prefillEnabled,
            SteamPrefillEnabled = steamPrefillEnabled,
            SteamPrefillExpiresAt = steamPrefillExpiresAt,
            EpicPrefillEnabled = epicPrefillEnabled,
            EpicPrefillExpiresAt = epicPrefillExpiresAt,
            BattlenetPrefillEnabled = battlenetPrefillEnabled,
            BattlenetPrefillExpiresAt = battlenetPrefillExpiresAt,
            RiotPrefillEnabled = riotPrefillEnabled,
            RiotPrefillExpiresAt = riotPrefillExpiresAt,
            XboxPrefillEnabled = xboxPrefillEnabled,
            XboxPrefillExpiresAt = xboxPrefillExpiresAt,
            Token = token
        });
    }

    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    public async Task<IActionResult> LoginAsync([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return BadRequest(ApiResponse.Required("API key"));
        }

        // If this browser has an existing guest session, revoke it before upgrading
        var existingToken = SessionService.TokenFromCookie(HttpContext);
        if (!string.IsNullOrEmpty(existingToken))
        {
            var existingSession = await _sessionService.ValidateSessionAsync(existingToken);
            if (existingSession is { SessionType: SessionType.Guest })
            {
                await _sessionService.RevokeSessionAsync(existingSession.Id);
                _logger.LogInformation("Revoked guest session {SessionId} during upgrade to admin", existingSession.Id);
            }
        }

        var result = await _sessionService.CreateAdminSessionAsync(request.ApiKey, HttpContext);
        if (result == null)
        {
            _logger.LogWarning("Failed login attempt from {IP}", HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(ApiResponse.Error("Invalid API key"));
        }

        var (rawToken, session) = result.Value;
        _sessionService.SetSessionCookie(HttpContext, rawToken, session.ExpiresAtUtc);

        // Broadcast session created
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionCreated, new
        {
            sessionId = session.Id.ToString(),
            sessionType = session.SessionType
        });

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc),
            Token = rawToken
        });
    }

    [AllowAnonymous]
    [HttpPost("guest")]
    public async Task<IActionResult> StartGuestAsync()
    {
        if (!_sessionService.IsGuestAccessEnabled())
        {
            throw new ForbiddenException("Guest access is disabled");
        }

        var result = await _sessionService.CreateGuestSessionAsync(HttpContext);
        if (result == null)
        {
            return StatusCode(500, ApiResponse.Error("Failed to create guest session"));
        }

        var (rawToken, session) = result.Value;
        _sessionService.SetSessionCookie(HttpContext, rawToken, session.ExpiresAtUtc);

        // Auto-grant per-service prefill access if enabled by default
        if (_sessionService.IsSteamPrefillEnabled())
        {
            await _sessionService.GrantSteamPrefillAccessAsync(session.Id, _sessionService.GetGuestPrefillDurationHours());
        }

        if (_sessionService.IsEpicPrefillEnabled())
        {
            await _sessionService.GrantEpicPrefillAccessAsync(session.Id, _stateService.GetEpicGuestPrefillDurationHours());
        }

        if (_sessionService.IsBattleNetPrefillEnabled())
        {
            await _sessionService.GrantBattleNetPrefillAccessAsync(session.Id, _stateService.GetBattleNetGuestPrefillDurationHours());
        }

        if (_sessionService.IsRiotPrefillEnabled())
        {
            await _sessionService.GrantRiotPrefillAccessAsync(session.Id, _stateService.GetRiotGuestPrefillDurationHours());
        }

        if (_stateService.GetXboxGuestPrefillEnabledByDefault())
        {
            await _sessionService.GrantXboxPrefillAccessAsync(session.Id, _stateService.GetXboxGuestPrefillDurationHours());
        }

        // Broadcast session created
        await _signalR.NotifyAllAsync(SignalREvents.UserSessionCreated, new
        {
            sessionId = session.Id.ToString(),
            sessionType = session.SessionType
        });

        return Ok(new LoginResponse
        {
            Success = true,
            SessionType = session.SessionType,
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc),
            Token = rawToken
        });
    }

    /// <summary>
    /// Lightweight presence heartbeat. The authenticated request itself triggers
    /// SessionAuthenticationHandler.UpdateLastSeenAsync (throttled to 60s server-side),
    /// which broadcasts SessionLastSeenUpdated on SignalR. Called by useActivityTracker
    /// while the tab is active so the user's presence dot stays "active" instead of
    /// flipping to "away" when no other API calls happen to be in-flight.
    /// </summary>
    [HttpPost("heartbeat")]
    public IActionResult Heartbeat()
    {
        return Ok();
    }

    [AllowAnonymous]
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        var rawToken = SessionService.TokenFromCookie(HttpContext);
        if (!string.IsNullOrEmpty(rawToken))
        {
            var session = await _sessionService.ValidateSessionAsync(rawToken);
            if (session != null)
            {
                await _sessionService.RevokeSessionAsync(session.Id);

                // Broadcast session revoked
                await _signalR.NotifyAllAsync(SignalREvents.UserSessionRevoked, new
                {
                    sessionId = session.Id.ToString(),
                    sessionType = session.SessionType
                });
            }
        }

        _sessionService.ClearSessionCookie(HttpContext);

        return Ok(new { success = true, message = "Logged out successfully" });
    }

    [AllowAnonymous]
    [HttpGet("guest/status")]
    public IActionResult GetGuestStatus()
    {
        return Ok(new
        {
            isLocked = _sessionService.IsGuestModeLocked(),
            durationHours = _sessionService.GetGuestDurationHours()
        });
    }

    // --- Guest Configuration Endpoints ---

    [AllowAnonymous]
    [HttpGet("guest/config")]
    public IActionResult GetGuestConfig()
    {
        return Ok(new GuestConfigResponse
        {
            DurationHours = _sessionService.GetGuestDurationHours(),
            IsLocked = _sessionService.IsGuestModeLocked()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpGet("guest/config/duration")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetGuestDuration()
    {
        return Ok(new GuestDurationResponse
        {
            DurationHours = _sessionService.GetGuestDurationHours(),
            Source = _sessionService.HasDurationOverride() ? "ui" : "config",
            CanEdit = true,
            EnvVarValue = _sessionService.GetGuestDurationDefault()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/config/duration")]
    public async Task<IActionResult> SetGuestDurationAsync([FromBody] GuestDurationRequest request)
    {
        if (request.DurationHours.HasValue && (request.DurationHours.Value < 1 || request.DurationHours.Value > 720))
        {
            return BadRequest(ApiResponse.Invalid("Duration must be between 1 and 720 hours"));
        }

        try
        {
            if (request.DurationHours is null)
            {
                _sessionService.ClearDurationOverride();
                _logger.LogInformation("Guest duration UI override cleared (will revert to env/appsettings default)");
            }
            else
            {
                _sessionService.SetGuestDurationHours(request.DurationHours.Value);
                _logger.LogInformation("Default guest duration updated to {Hours}h (existing sessions unchanged)", request.DurationHours.Value);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist guest duration setting");
            return StatusCode(503, ApiResponse.Error("state_persistence_disabled"));
        }

        // Broadcast the effective (post-merge) value, not the raw request value, so clients
        // see the env/appsettings fallback when the override is cleared.
        var effectiveHours = _sessionService.GetGuestDurationHours();
        await _signalR.NotifyAllAsync(SignalREvents.GuestDurationUpdated, new
        {
            durationHours = effectiveHours
        });

        return Ok(new GuestDurationResponse
        {
            DurationHours = effectiveHours,
            Source = _sessionService.HasDurationOverride() ? "ui" : "config",
            CanEdit = true,
            EnvVarValue = _sessionService.GetGuestDurationDefault()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/config/lock")]
    public async Task<IActionResult> SetGuestLockAsync([FromBody] GuestLockRequest request)
    {
        _sessionService.SetGuestModeLocked(request.IsLocked);

        await _signalR.NotifyAllAsync(SignalREvents.GuestModeLockChanged, new
        {
            isLocked = request.IsLocked
        });

        return Ok(new { success = true, isLocked = request.IsLocked, message = request.IsLocked ? "Guest mode locked" : "Guest mode unlocked" });
    }

    // --- Guest Prefill Endpoints ---

    [AllowAnonymous]
    [HttpGet("guest/prefill/config")]
    public IActionResult GetGuestPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _sessionService.IsSteamPrefillEnabled(),
            durationHours = _sessionService.GetGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount(),
            epicEnabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            epicDurationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            epicMaxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount(),
            battlenetEnabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            battlenetDurationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/prefill/config")]
    public async Task<IActionResult> SetGuestPrefillConfigAsync([FromBody] GuestPrefillConfigRequest request)
    {
        if (!GuestPrefillValidation.TryValidateDurationHours(request.DurationHours, out var durationError))
        {
            return BadRequest(ApiResponse.Invalid(durationError));
        }

        var wasSteamEnabled = _sessionService.IsSteamPrefillEnabled();
        var wasBattleNetEnabled = _stateService.GetBattleNetGuestPrefillEnabledByDefault();
        var wasRiotEnabled = _stateService.GetRiotGuestPrefillEnabledByDefault();

        _sessionService.SetSteamGuestPrefillEnabled(request.EnabledByDefault);
        _sessionService.SetGuestPrefillDurationHours(request.DurationHours);
        _stateService.SetDefaultGuestMaxThreadCount(request.MaxThreadCount);

        // Battle.net is an optional, anonymous service; only update when the caller supplies values.
        if (request.BattleNetEnabledByDefault.HasValue)
            _stateService.SetBattleNetGuestPrefillEnabledByDefault(request.BattleNetEnabledByDefault.Value);
        if (request.BattleNetDurationHours.HasValue)
            _stateService.SetBattleNetGuestPrefillDurationHours(request.BattleNetDurationHours.Value);

        // Riot is an optional, anonymous service; only update when the caller supplies values.
        if (request.RiotEnabledByDefault.HasValue)
            _stateService.SetRiotGuestPrefillEnabledByDefault(request.RiotEnabledByDefault.Value);
        if (request.RiotDurationHours.HasValue)
            _stateService.SetRiotGuestPrefillDurationHours(request.RiotDurationHours.Value);

        var steamGrantCount = 0;
        if (request.EnabledByDefault && !wasSteamEnabled)
        {
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.Steam, request.DurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "steam");
            steamGrantCount = grants.Count;
        }

        var battleNetGrantCount = 0;
        if (request.BattleNetEnabledByDefault is true && !wasBattleNetEnabled)
        {
            var battleNetDurationHours = request.BattleNetDurationHours
                ?? _stateService.GetBattleNetGuestPrefillDurationHours();
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.BattleNet, battleNetDurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "battlenet");
            battleNetGrantCount = grants.Count;
        }

        var riotGrantCount = 0;
        if (request.RiotEnabledByDefault is true && !wasRiotEnabled)
        {
            var riotDurationHours = request.RiotDurationHours
                ?? _stateService.GetRiotGuestPrefillDurationHours();
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.Riot, riotDurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "riot");
            riotGrantCount = grants.Count;
        }

        if (steamGrantCount > 0 || battleNetGrantCount > 0 || riotGrantCount > 0)
        {
            _logger.LogInformation(
                "Default guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads}; granted to {SteamCount} Steam, {BattleNetCount} Battle.net, {RiotCount} Riot guest session(s)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount,
                steamGrantCount, battleNetGrantCount, riotGrantCount);
        }
        else
        {
            _logger.LogInformation(
                "Default guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads} (existing sessions unchanged)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount);
        }

        await _signalR.NotifyAllAsync(SignalREvents.GuestPrefillConfigChanged, new
        {
            enabledByDefault = request.EnabledByDefault,
            durationHours = request.DurationHours,
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _sessionService.IsSteamPrefillEnabled(),
            durationHours = _sessionService.GetGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });
    }

    // --- Epic Guest Prefill Endpoints ---

    [AllowAnonymous]
    [HttpGet("guest/epic-prefill/config")]
    public IActionResult GetEpicPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/epic-prefill/config")]
    public async Task<IActionResult> SetEpicPrefillConfigAsync([FromBody] EpicGuestPrefillConfigRequest request)
    {
        if (!GuestPrefillValidation.TryValidateDurationHours(request.DurationHours, out var durationError))
        {
            return BadRequest(ApiResponse.Invalid(durationError));
        }

        var wasEnabled = _stateService.GetEpicGuestPrefillEnabledByDefault();

        _stateService.SetEpicGuestPrefillEnabledByDefault(request.EnabledByDefault);
        _stateService.SetEpicGuestPrefillDurationHours(request.DurationHours);
        _stateService.SetEpicDefaultGuestMaxThreadCount(request.MaxThreadCount);

        var grantCount = 0;
        if (request.EnabledByDefault && !wasEnabled)
        {
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.Epic, request.DurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "epic");
            grantCount = grants.Count;
        }

        if (grantCount > 0)
        {
            _logger.LogInformation(
                "Default Epic guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads}; granted to {GrantCount} eligible guest session(s)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount, grantCount);
        }
        else
        {
            _logger.LogInformation(
                "Default Epic guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads} (existing sessions unchanged)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount);
        }

        await _signalR.NotifyAllAsync(SignalREvents.EpicGuestPrefillConfigChanged, new
        {
            enabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            epicMaxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount()
        });
    }

    // --- Battle.net Guest Prefill Endpoints (anonymous - no account login, no thread limit) ---

    [AllowAnonymous]
    [HttpGet("guest/battlenet-prefill/config")]
    public IActionResult GetBattleNetPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/battlenet-prefill/config")]
    public async Task<IActionResult> SetBattleNetPrefillConfigAsync([FromBody] BattleNetGuestPrefillConfigRequest request)
    {
        if (!GuestPrefillValidation.TryValidateDurationHours(request.DurationHours, out var durationError))
        {
            return BadRequest(ApiResponse.Invalid(durationError));
        }

        var wasEnabled = _stateService.GetBattleNetGuestPrefillEnabledByDefault();

        _stateService.SetBattleNetGuestPrefillEnabledByDefault(request.EnabledByDefault);
        _stateService.SetBattleNetGuestPrefillDurationHours(request.DurationHours);

        var grantCount = 0;
        if (request.EnabledByDefault && !wasEnabled)
        {
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.BattleNet, request.DurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "battlenet");
            grantCount = grants.Count;
        }

        if (grantCount > 0)
        {
            _logger.LogInformation(
                "Default Battle.net guest prefill config updated: enabled={Enabled}, duration={Hours}h; granted to {GrantCount} eligible guest session(s)",
                request.EnabledByDefault, request.DurationHours, grantCount);
        }
        else
        {
            _logger.LogInformation(
                "Default Battle.net guest prefill config updated: enabled={Enabled}, duration={Hours}h (existing sessions unchanged)",
                request.EnabledByDefault, request.DurationHours);
        }

        await _signalR.NotifyAllAsync(SignalREvents.BattleNetGuestPrefillConfigChanged, new
        {
            enabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    // --- Riot Guest Prefill Endpoints (anonymous - no account login, no thread limit) ---

    [AllowAnonymous]
    [HttpGet("guest/riot-prefill/config")]
    public IActionResult GetRiotPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _stateService.GetRiotGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetRiotGuestPrefillDurationHours()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/riot-prefill/config")]
    public async Task<IActionResult> SetRiotPrefillConfigAsync([FromBody] RiotGuestPrefillConfigRequest request)
    {
        if (!GuestPrefillValidation.TryValidateDurationHours(request.DurationHours, out var durationError))
        {
            return BadRequest(ApiResponse.Invalid(durationError));
        }

        var wasEnabled = _stateService.GetRiotGuestPrefillEnabledByDefault();

        _stateService.SetRiotGuestPrefillEnabledByDefault(request.EnabledByDefault);
        _stateService.SetRiotGuestPrefillDurationHours(request.DurationHours);

        var grantCount = 0;
        if (request.EnabledByDefault && !wasEnabled)
        {
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.Riot, request.DurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "riot");
            grantCount = grants.Count;
        }

        if (grantCount > 0)
        {
            _logger.LogInformation(
                "Default Riot guest prefill config updated: enabled={Enabled}, duration={Hours}h; granted to {GrantCount} eligible guest session(s)",
                request.EnabledByDefault, request.DurationHours, grantCount);
        }
        else
        {
            _logger.LogInformation(
                "Default Riot guest prefill config updated: enabled={Enabled}, duration={Hours}h (existing sessions unchanged)",
                request.EnabledByDefault, request.DurationHours);
        }

        await _signalR.NotifyAllAsync(SignalREvents.RiotGuestPrefillConfigChanged, new
        {
            enabledByDefault = _stateService.GetRiotGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetRiotGuestPrefillDurationHours()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _stateService.GetRiotGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetRiotGuestPrefillDurationHours()
        });
    }

    // --- Xbox Guest Prefill Endpoints (login-required - mirrors Epic, has a thread limit) ---

    [AllowAnonymous]
    [HttpGet("guest/xbox-prefill/config")]
    public IActionResult GetXboxPrefillConfig()
    {
        return Ok(new
        {
            enabledByDefault = _stateService.GetXboxGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetXboxGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetXboxDefaultGuestMaxThreadCount()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/xbox-prefill/config")]
    public async Task<IActionResult> SetXboxPrefillConfigAsync([FromBody] XboxGuestPrefillConfigRequest request)
    {
        if (!GuestPrefillValidation.TryValidateDurationHours(request.DurationHours, out var durationError))
        {
            return BadRequest(ApiResponse.Invalid(durationError));
        }

        var wasEnabled = _stateService.GetXboxGuestPrefillEnabledByDefault();

        _stateService.SetXboxGuestPrefillEnabledByDefault(request.EnabledByDefault);
        _stateService.SetXboxGuestPrefillDurationHours(request.DurationHours);
        _stateService.SetXboxDefaultGuestMaxThreadCount(request.MaxThreadCount);

        var grantCount = 0;
        if (request.EnabledByDefault && !wasEnabled)
        {
            var grants = await _sessionService.GrantDefaultPrefillToEligibleGuestSessionsAsync(
                PrefillPlatform.Xbox, request.DurationHours);
            await EmitDefaultPrefillGrantsAsync(grants, "xbox");
            grantCount = grants.Count;
        }

        if (grantCount > 0)
        {
            _logger.LogInformation(
                "Default Xbox guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads}; granted to {GrantCount} eligible guest session(s)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount, grantCount);
        }
        else
        {
            _logger.LogInformation(
                "Default Xbox guest prefill config updated: enabled={Enabled}, duration={Hours}h, maxThreads={MaxThreads} (existing sessions unchanged)",
                request.EnabledByDefault, request.DurationHours, request.MaxThreadCount);
        }

        await _signalR.NotifyAllAsync(SignalREvents.XboxGuestPrefillConfigChanged, new
        {
            enabledByDefault = _stateService.GetXboxGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetXboxGuestPrefillDurationHours(),
            xboxMaxThreadCount = _stateService.GetXboxDefaultGuestMaxThreadCount()
        });

        return Ok(new {
            success = true,
            enabledByDefault = _stateService.GetXboxGuestPrefillEnabledByDefault(),
            durationHours = _stateService.GetXboxGuestPrefillDurationHours(),
            maxThreadCount = _stateService.GetXboxDefaultGuestMaxThreadCount()
        });
    }

    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/prefill/toggle/{sessionId:guid}")]
    public async Task<IActionResult> ToggleGuestPrefillAsync(Guid sessionId, [FromBody] GuestPrefillToggleRequest request, [FromQuery] string service = "steam")
    {
        var normalizedService = service.Trim().ToLowerInvariant();

        if (normalizedService == "epic")
        {
            if (request.Enabled)
                await _sessionService.GrantEpicPrefillAccessAsync(sessionId, _stateService.GetEpicGuestPrefillDurationHours());
            else
                await _sessionService.RevokeEpicPrefillAccessAsync(sessionId);
        }
        else if (normalizedService == "battlenet")
        {
            // Battle.net is anonymous; this grant only gates feature access (not an account login)
            if (request.Enabled)
                await _sessionService.GrantBattleNetPrefillAccessAsync(sessionId, _stateService.GetBattleNetGuestPrefillDurationHours());
            else
                await _sessionService.RevokeBattleNetPrefillAccessAsync(sessionId);
        }
        else if (normalizedService == "riot")
        {
            // Riot is anonymous; this grant only gates feature access (not an account login)
            if (request.Enabled)
                await _sessionService.GrantRiotPrefillAccessAsync(sessionId, _stateService.GetRiotGuestPrefillDurationHours());
            else
                await _sessionService.RevokeRiotPrefillAccessAsync(sessionId);
        }
        else if (normalizedService == "xbox")
        {
            // Xbox is login-required (Microsoft device-code); this grant gates feature access
            if (request.Enabled)
                await _sessionService.GrantXboxPrefillAccessAsync(sessionId, _stateService.GetXboxGuestPrefillDurationHours());
            else
                await _sessionService.RevokeXboxPrefillAccessAsync(sessionId);
        }
        else
        {
            // Default to steam for backward compatibility
            if (request.Enabled)
                await _sessionService.GrantSteamPrefillAccessAsync(sessionId, _sessionService.GetGuestPrefillDurationHours());
            else
                await _sessionService.RevokeSteamPrefillAccessAsync(sessionId);
        }

        var updatedSession = await _sessionService.GetSessionByIdAsync(sessionId);
        DateTime? prefillExpiresAt = null;

        if (normalizedService == "epic")
        {
            prefillExpiresAt = updatedSession?.EpicPrefillExpiresAtUtc != null
                ? DateTime.SpecifyKind(updatedSession.EpicPrefillExpiresAtUtc.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }
        else if (normalizedService == "battlenet")
        {
            prefillExpiresAt = updatedSession?.BattleNetPrefillExpiresAtUtc != null
                ? DateTime.SpecifyKind(updatedSession.BattleNetPrefillExpiresAtUtc.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }
        else if (normalizedService == "riot")
        {
            prefillExpiresAt = updatedSession?.RiotPrefillExpiresAtUtc != null
                ? DateTime.SpecifyKind(updatedSession.RiotPrefillExpiresAtUtc.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }
        else if (normalizedService == "xbox")
        {
            prefillExpiresAt = updatedSession?.XboxPrefillExpiresAtUtc != null
                ? DateTime.SpecifyKind(updatedSession.XboxPrefillExpiresAtUtc.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }
        else
        {
            prefillExpiresAt = updatedSession?.SteamPrefillExpiresAtUtc != null
                ? DateTime.SpecifyKind(updatedSession.SteamPrefillExpiresAtUtc.Value, DateTimeKind.Utc)
                : (DateTime?)null;
        }

        await _signalR.NotifyAllAsync(SignalREvents.GuestPrefillPermissionChanged, new
        {
            sessionId = sessionId.ToString(),
            service = normalizedService,
            enabled = request.Enabled,
            prefillExpiresAt
        });

        return Ok(new { success = true, sessionId = sessionId.ToString(), service = normalizedService, enabled = request.Enabled, prefillExpiresAt });
    }

    private async Task EmitDefaultPrefillGrantsAsync(
        IReadOnlyList<GuestPrefillGrantResult> grants,
        string service)
    {
        foreach (var grant in grants)
        {
            await _signalR.NotifyAllAsync(SignalREvents.GuestPrefillPermissionChanged, new
            {
                sessionId = grant.SessionId.ToString(),
                service,
                enabled = true,
                prefillExpiresAt = DateTime.SpecifyKind(grant.PrefillExpiresAtUtc, DateTimeKind.Utc)
            });
        }
    }
}

// Request models
public class GuestDurationRequest
{
    // null = clear UI override (revert to env/appsettings default).
    public int? DurationHours { get; set; }
}

public class GuestLockRequest
{
    public bool IsLocked { get; set; }
}

public class GuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
    // Optional Battle.net defaults (anonymous service); omitted by Steam-only callers.
    public bool? BattleNetEnabledByDefault { get; set; }
    public int? BattleNetDurationHours { get; set; }
    // Optional Riot defaults (anonymous service); omitted by Steam-only callers.
    public bool? RiotEnabledByDefault { get; set; }
    public int? RiotDurationHours { get; set; }
}

public class GuestPrefillToggleRequest
{
    public bool Enabled { get; set; }
}

public class EpicGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
}

public class BattleNetGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
}

public class RiotGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
}

public class XboxGuestPrefillConfigRequest
{
    public bool EnabledByDefault { get; set; }
    public int DurationHours { get; set; } = 2;
    public int? MaxThreadCount { get; set; }
}
