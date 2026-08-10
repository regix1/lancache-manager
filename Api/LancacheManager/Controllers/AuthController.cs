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

    /// <summary>
    /// Returns session and setup status for the current request.
    /// </summary>
    /// <remarks>
    /// Anonymous so the frontend can call it before login to decide whether to show the login
    /// screen, the setup wizard, or the app. When authentication is disabled,
    /// <see cref="SessionAuthenticationHandler"/> has already minted the shared admin session for
    /// this request, so this reports it exactly like a real login rather than special-casing the
    /// disabled-auth state here too.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("status")]
    [ProducesResponseType(typeof(AuthStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<AuthStatusResponse>> GetStatusAsync()
    {
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate, max-age=0";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";

        // Under disabled auth the shared admin session is resolved and its cookie set by
        // SessionAuthenticationHandler, which runs for every request rather than only this one, so
        // the session is already here.
        var session = HttpContext.GetUserSession();
        var authenticationEnabled = _sessionService.IsAuthenticationEnabled();

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
            if (session.SessionType.IsAccountHolder())
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

        // Token rotation: provide a fresh token for SignalR accessTokenFactory (mobile support).
        // Not for a caller carrying the key: SessionAuthenticationHandler resolves a session for it only
        // while authentication is enabled and the header is present (SessionAuthenticationHandler.cs:65-67),
        // that is one session every key caller shares (SessionService.cs:50), and no copy of its token is
        // kept (SessionService.cs:342-345). Rotating it writes a cookie for that shared session
        // (SessionService.cs:759) and retires the token the previous caller was handed, so a key caller
        // would leave here holding a credential belonging to all of them that the next status call breaks.
        // The key authenticates each of its requests on its own. With authentication disabled the header is
        // never read and the session is the shared one whose cookie the browser runs on, so that path keeps
        // rotating. [11]
        string? token = null;
        if (session != null)
        {
            var authenticatedWithApiKey = authenticationEnabled && Request.Headers.ContainsKey("X-Api-Key");
            var rotatedToken = authenticatedWithApiKey
                ? null
                : await _sessionService.RotateSessionTokenAsync(session, HttpContext);
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

    /// <summary>
    /// Exchanges an API key for an admin session cookie.
    /// </summary>
    /// <remarks>
    /// If the browser already held a guest session, that session is revoked first so the upgrade
    /// does not leave two live sessions for the same browser.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> LoginAsync([FromBody] LoginRequest request)
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

    /// <summary>
    /// Starts a time-limited guest session.
    /// </summary>
    /// <remarks>
    /// For each prefill service enabled by default, grants that service's guest-prefill access
    /// immediately so the guest does not have to ask an admin to turn it on. Rejected when guest
    /// access is turned off, and while setup is unfinished.
    ///
    /// Rate limited because it is the one endpoint that writes rows for a caller who presented
    /// nothing: each call inserts a session and up to five prefill grants.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("guest")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> StartGuestAsync()
    {
        if (!_sessionService.IsGuestAccessEnabled())
        {
            throw new ForbiddenException("Guest access is disabled");
        }

        // An unfinished installation serves the setup wizard and nothing else, and a guest cannot
        // finish it: the wizard's saves are admin-gated and it offers no way out. A guest session
        // handed out in that state leaves the caller looking at a screen that refuses everything, so
        // the session is refused instead. [7]
        if (!_stateService.GetSetupCompleted())
        {
            throw new ForbiddenException("Setup is not complete");
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
    /// Lightweight presence heartbeat for the current session.
    /// </summary>
    /// <remarks>
    /// The authenticated request itself triggers
    /// SessionAuthenticationHandler.UpdateLastSeenAsync (throttled to 60s server-side), which
    /// broadcasts SessionLastSeenUpdated on SignalR. Called by useActivityTracker while the tab is
    /// active so the user's presence dot stays "active" instead of flipping to "away" when no
    /// other API calls happen to be in-flight.
    /// </remarks>
    [HttpPost("heartbeat")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Heartbeat()
    {
        return Ok();
    }

    /// <summary>
    /// Revokes the caller's session and clears the session cookie.
    /// </summary>
    /// <remarks>
    /// Always succeeds, including when the caller had no session to begin with.
    /// </remarks>
    [AllowAnonymous]
    [HttpPost("logout")]
    [ProducesResponseType(typeof(LogoutResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LogoutResponse>> LogoutAsync()
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

        return Ok(new LogoutResponse { Success = true, Message = "Logged out successfully" });
    }

    /// <summary>
    /// Returns whether guest mode is locked and the duration a new guest session would get.
    /// </summary>
    /// <remarks>
    /// Anonymous so the login screen can show this before the visitor has any session at all.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/status")]
    [ProducesResponseType(typeof(GuestStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<GuestStatusResponse> GetGuestStatus()
    {
        return Ok(new GuestStatusResponse
        {
            IsLocked = _sessionService.IsGuestModeLocked(),
            DurationHours = _sessionService.GetGuestDurationHours()
        });
    }

    // --- Guest Configuration Endpoints ---

    /// <summary>
    /// Returns the guest-mode duration and lock state, for the guest onboarding screen.
    /// </summary>
    /// <remarks>
    /// Anonymous so it can be read before the visitor has any session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/config")]
    [ProducesResponseType(typeof(GuestConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<GuestConfigResponse> GetGuestConfig()
    {
        return Ok(new GuestConfigResponse
        {
            DurationHours = _sessionService.GetGuestDurationHours(),
            IsLocked = _sessionService.IsGuestModeLocked()
        });
    }

    /// <summary>
    /// Returns the current default guest-session duration, for the admin settings screen.
    /// </summary>
    /// <remarks>
    /// Also reports whether the value came from a UI override or the environment/appsettings
    /// default.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpGet("guest/config/duration")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ProducesResponseType(typeof(GuestDurationResponse), StatusCodes.Status200OK)]
    public ActionResult<GuestDurationResponse> GetGuestDuration()
    {
        return Ok(new GuestDurationResponse
        {
            DurationHours = _sessionService.GetGuestDurationHours(),
            Source = _sessionService.HasDurationOverride() ? "ui" : "config",
            CanEdit = true,
            EnvVarValue = _sessionService.GetGuestDurationDefault()
        });
    }

    /// <summary>
    /// Sets or clears the UI override for the default guest-session duration.
    /// </summary>
    /// <remarks>
    /// A null <c>DurationHours</c> clears the override and reverts to the environment/appsettings
    /// default; existing guest sessions keep whatever duration they were granted with.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/config/duration")]
    [ProducesResponseType(typeof(GuestDurationResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GuestDurationResponse>> SetGuestDurationAsync([FromBody] GuestDurationRequest request)
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

    /// <summary>
    /// Locks or unlocks guest mode.
    /// </summary>
    /// <remarks>
    /// While locked, no new guest sessions can be started; existing guest sessions are unaffected.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/config/lock")]
    [ProducesResponseType(typeof(GuestLockResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GuestLockResponse>> SetGuestLockAsync([FromBody] GuestLockRequest request)
    {
        _sessionService.SetGuestModeLocked(request.IsLocked);

        await _signalR.NotifyAllAsync(SignalREvents.GuestModeLockChanged, new
        {
            isLocked = request.IsLocked
        });

        return Ok(new GuestLockResponse
        {
            Success = true,
            IsLocked = request.IsLocked,
            Message = request.IsLocked ? "Guest mode locked" : "Guest mode unlocked"
        });
    }

    // --- Guest Prefill Endpoints ---

    /// <summary>
    /// Returns the Steam guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Also includes the Epic and Battle.net enabled flags that predate those services getting
    /// their own guest-prefill config endpoints below. Anonymous so the guest onboarding screen
    /// can read it before the visitor has a session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/prefill/config")]
    [ProducesResponseType(typeof(GuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<GuestPrefillConfigResponse> GetGuestPrefillConfig()
    {
        return Ok(new GuestPrefillConfigResponse
        {
            EnabledByDefault = _sessionService.IsSteamPrefillEnabled(),
            DurationHours = _sessionService.GetGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetDefaultGuestMaxThreadCount(),
            EpicEnabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            EpicDurationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            EpicMaxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount(),
            BattlenetEnabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            BattlenetDurationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    /// <summary>
    /// Saves the Steam guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Optionally also saves the Battle.net and Riot defaults alongside them, but only when the
    /// caller supplies values for those since they are separate anonymous services. Turning a
    /// service on that was off grants it immediately to every eligible active guest session
    /// instead of waiting for their next login.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/prefill/config")]
    [ProducesResponseType(typeof(SetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetGuestPrefillConfigResponse>> SetGuestPrefillConfigAsync([FromBody] GuestPrefillConfigRequest request)
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

        return Ok(new SetGuestPrefillConfigResponse
        {
            Success = true,
            EnabledByDefault = _sessionService.IsSteamPrefillEnabled(),
            DurationHours = _sessionService.GetGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetDefaultGuestMaxThreadCount()
        });
    }

    // --- Epic Guest Prefill Endpoints ---

    /// <summary>
    /// Returns the Epic guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Anonymous so the guest onboarding screen can read it before the visitor has a session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/epic-prefill/config")]
    [ProducesResponseType(typeof(EpicGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<EpicGuestPrefillConfigResponse> GetEpicPrefillConfig()
    {
        return Ok(new EpicGuestPrefillConfigResponse
        {
            EnabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount()
        });
    }

    /// <summary>
    /// Saves the Epic guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Turning it on when it was off grants Epic prefill immediately to every eligible active
    /// guest session instead of waiting for their next login.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/epic-prefill/config")]
    [ProducesResponseType(typeof(SetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetGuestPrefillConfigResponse>> SetEpicPrefillConfigAsync([FromBody] EpicGuestPrefillConfigRequest request)
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

        return Ok(new SetGuestPrefillConfigResponse
        {
            Success = true,
            EnabledByDefault = _stateService.GetEpicGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetEpicGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetEpicDefaultGuestMaxThreadCount()
        });
    }

    // --- Battle.net Guest Prefill Endpoints (anonymous - no account login, no thread limit) ---

    /// <summary>
    /// Returns the Battle.net guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Anonymous so the guest onboarding screen can read it before the visitor has a session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/battlenet-prefill/config")]
    [ProducesResponseType(typeof(BattleNetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<BattleNetGuestPrefillConfigResponse> GetBattleNetPrefillConfig()
    {
        return Ok(new BattleNetGuestPrefillConfigResponse
        {
            EnabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    /// <summary>
    /// Saves the Battle.net guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Turning it on when it was off grants Battle.net prefill immediately to every eligible
    /// active guest session instead of waiting for their next login.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/battlenet-prefill/config")]
    [ProducesResponseType(typeof(SetBattleNetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetBattleNetGuestPrefillConfigResponse>> SetBattleNetPrefillConfigAsync([FromBody] BattleNetGuestPrefillConfigRequest request)
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

        return Ok(new SetBattleNetGuestPrefillConfigResponse
        {
            Success = true,
            EnabledByDefault = _stateService.GetBattleNetGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetBattleNetGuestPrefillDurationHours()
        });
    }

    // --- Riot Guest Prefill Endpoints (anonymous - no account login, no thread limit) ---

    /// <summary>
    /// Returns the Riot guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Anonymous so the guest onboarding screen can read it before the visitor has a session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/riot-prefill/config")]
    [ProducesResponseType(typeof(BattleNetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<BattleNetGuestPrefillConfigResponse> GetRiotPrefillConfig()
    {
        return Ok(new BattleNetGuestPrefillConfigResponse
        {
            EnabledByDefault = _stateService.GetRiotGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetRiotGuestPrefillDurationHours()
        });
    }

    /// <summary>
    /// Saves the Riot guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Turning it on when it was off grants Riot prefill immediately to every eligible active
    /// guest session instead of waiting for their next login.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/riot-prefill/config")]
    [ProducesResponseType(typeof(SetBattleNetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetBattleNetGuestPrefillConfigResponse>> SetRiotPrefillConfigAsync([FromBody] RiotGuestPrefillConfigRequest request)
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

        return Ok(new SetBattleNetGuestPrefillConfigResponse
        {
            Success = true,
            EnabledByDefault = _stateService.GetRiotGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetRiotGuestPrefillDurationHours()
        });
    }

    // --- Xbox Guest Prefill Endpoints (login-required - mirrors Epic, has a thread limit) ---

    /// <summary>
    /// Returns the Xbox guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Anonymous so the guest onboarding screen can read it before the visitor has a session.
    /// </remarks>
    [AllowAnonymous]
    [HttpGet("guest/xbox-prefill/config")]
    [ProducesResponseType(typeof(EpicGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public ActionResult<EpicGuestPrefillConfigResponse> GetXboxPrefillConfig()
    {
        return Ok(new EpicGuestPrefillConfigResponse
        {
            EnabledByDefault = _stateService.GetXboxGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetXboxGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetXboxDefaultGuestMaxThreadCount()
        });
    }

    /// <summary>
    /// Saves the Xbox guest-prefill defaults.
    /// </summary>
    /// <remarks>
    /// Turning it on when it was off grants Xbox prefill immediately to every eligible active
    /// guest session instead of waiting for their next login.
    /// </remarks>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/xbox-prefill/config")]
    [ProducesResponseType(typeof(SetGuestPrefillConfigResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SetGuestPrefillConfigResponse>> SetXboxPrefillConfigAsync([FromBody] XboxGuestPrefillConfigRequest request)
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

        return Ok(new SetGuestPrefillConfigResponse
        {
            Success = true,
            EnabledByDefault = _stateService.GetXboxGuestPrefillEnabledByDefault(),
            DurationHours = _stateService.GetXboxGuestPrefillDurationHours(),
            MaxThreadCount = _stateService.GetXboxDefaultGuestMaxThreadCount()
        });
    }

    /// <summary>
    /// Grants or revokes one guest session's prefill access for one service.
    /// </summary>
    /// <remarks>
    /// Used by the admin session list to flip an individual guest's access without waiting for
    /// their grant to expire or changing the site-wide default.
    /// </remarks>
    /// <param name="service">"steam" (default) | "epic" | "battlenet" | "riot" | "xbox".</param>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("guest/prefill/toggle/{sessionId:guid}")]
    [ProducesResponseType(typeof(GuestPrefillToggleResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<GuestPrefillToggleResponse>> ToggleGuestPrefillAsync(Guid sessionId, [FromBody] GuestPrefillToggleRequest request, [FromQuery] string service = "steam")
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

        return Ok(new GuestPrefillToggleResponse
        {
            Success = true,
            SessionId = sessionId.ToString(),
            Service = normalizedService,
            Enabled = request.Enabled,
            PrefillExpiresAt = prefillExpiresAt
        });
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
