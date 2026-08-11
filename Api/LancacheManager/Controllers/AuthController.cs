using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Validators;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
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
    private readonly ApiKeyService _apiKeyService;
    private readonly IPasswordHasher<UserAccount> _passwordHasher;
    private readonly AccountLockout _accountLockout;
    private readonly IdentityAuditService _identityAuditService;

    public AuthController(
        SessionService sessionService,
        ILogger<AuthController> logger,
        IDbContextFactory<AppDbContext> dbContextFactory,
        StateService stateService,
        ISignalRNotificationService signalR,
        ApiKeyService apiKeyService,
        IPasswordHasher<UserAccount> passwordHasher,
        AccountLockout accountLockout,
        IdentityAuditService identityAuditService)
    {
        _sessionService = sessionService;
        _logger = logger;
        _dbContextFactory = dbContextFactory;
        _stateService = stateService;
        _signalR = signalR;
        _apiKeyService = apiKeyService;
        _passwordHasher = passwordHasher;
        _accountLockout = accountLockout;
        _identityAuditService = identityAuditService;
    }

    /// <summary>
    /// A hash of a password nobody holds, verified against when the username names no account.
    /// Static so it is produced once and then reused: the point is to spend the same time on the
    /// compare as a real account does, not to spend it building something to compare with.
    /// </summary>
    private static string? _absentAccountHash;

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

        // Nothing copies IsMainAdmin onto the session row, so the account row is what answers it. Only an
        // account-backed session reaches the query: a guest, an API-key caller and the
        // disabled-authentication session all leave AccountId null (UserSession.cs:29). [58]
        var isMainAdmin = false;
        if (session?.AccountId is { } accountId)
        {
            try
            {
                using var context = _dbContextFactory.CreateDbContext();
                isMainAdmin = await context.UserAccounts
                    .Where(a => a.Id == accountId)
                    .Select(a => a.IsMainAdmin)
                    .FirstOrDefaultAsync();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Failed to check if the session's account is the main admin"); }
        }

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

        // Token rotation: the browser leaves here holding a fresh cookie, so a token that leaked from an
        // earlier response is short-lived rather than good for the life of the session.
        // Not for a caller carrying the key: SessionAuthenticationHandler resolves a session for it only
        // while authentication is enabled and the header is present (SessionAuthenticationHandler.cs:65-67),
        // that is one session every key caller shares (SessionService.cs:50), and no copy of its token is
        // kept (SessionService.cs:342-345). Rotating it writes a cookie for that shared session
        // (SessionService.cs:759) and retires the token the previous caller was handed, so a key caller
        // would leave here holding a credential belonging to all of them that the next status call breaks.
        // The key authenticates each of its requests on its own. With authentication disabled the header is
        // never read and the session is the shared one whose cookie the browser runs on, so that path keeps
        // rotating. [11]
        var authenticatedWithApiKey = authenticationEnabled && Request.Headers.ContainsKey("X-Api-Key");
        if (session != null && !authenticatedWithApiKey)
        {
            await _sessionService.RotateSessionTokenAsync(session, HttpContext);
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
            AccountId = session?.AccountId,
            // With authentication disabled the session is the shared minted one, which has no account
            // row, and that caller may create admins anyway, so it is told it holds the rights the
            // server will actually grant it rather than being shown a screen it cannot use. [58]
            IsMainAdmin = !authenticationEnabled || isMainAdmin,
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
            XboxPrefillExpiresAt = xboxPrefillExpiresAt
        });
    }

    /// <summary>
    /// Signs an account in with the installation's API key, a username and a password.
    /// </summary>
    /// <remarks>
    /// All three are required and all three are checked before anything is answered. The key alone
    /// used to be a sign-in; it is now one of three things a caller has to hold. [38][39]
    ///
    /// Every way this can fail is answered identically - the same status and the same body - because a
    /// caller who can tell them apart learns which usernames exist, which passwords are close, and
    /// whether their guessing is having an effect. That includes an account locked by too many recent
    /// failures, which is the one that would otherwise confirm a username outright. [44]
    ///
    /// If the browser already held a guest session, that session is revoked first so the upgrade
    /// does not leave two live sessions for the same browser.
    /// </remarks>
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [HttpPost("login")]
    [ProducesResponseType(typeof(LoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LoginResponse>> LoginAsync([FromBody] LoginRequest request)
    {
        var account = await FindAccountAsync(request.Username);

        // The password is verified before anything else is decided, and against a throwaway hash when
        // there is no account, so an unknown username costs the same PBKDF2 work as a real one. Skipping
        // it would answer in a few milliseconds instead of a few hundred, which is a username oracle
        // that no shared error message hides. This mirrors what ApiKeyService.ValidateApiKey:107-112
        // does for a key of the wrong length. [44]
        var password = VerifyPassword(account, request.Password);
        var passwordMatched = password is PasswordVerificationResult.Success
            or PasswordVerificationResult.SuccessRehashNeeded;
        var keyMatched = _apiKeyService.ValidateApiKey(request.ApiKey);

        // Checked after the password rather than instead of it, for the same reason: a locked account
        // that answered without hashing would be recognisable by how fast it refused.
        var locked = account != null && _accountLockout.IsLocked(account.Id);

        if (account == null || account.IsDisabled || !passwordMatched || !keyMatched || locked)
        {
            if (account != null && !account.IsDisabled && !locked && keyMatched && !passwordMatched)
            {
                // Only a wrong password against a real, usable account is counted, and only from a
                // caller who held the installation's key. An unknown username names nobody to count
                // against, and a caller without the key is not making a credible attempt on this
                // account - counting those would let anyone lock any account they can name, without
                // ever holding the key. Both are the per-IP limiter's to bound instead. [43]
                _accountLockout.RecordFailure(account.Id);
            }

            _logger.LogWarning("Failed login attempt from {IP}", HttpContext.Connection.RemoteIpAddress);

            // No session and, when the username was unknown, no account either. [29c]
            await _identityAuditService.RecordAsync(
                IdentityAuditEvent.LoginFailed,
                performedByAccountId: account?.Id,
                performedBySessionId: null,
                targetAccountId: account?.Id);

            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.InvalidCredentials,
                    Error = "Invalid API key, username or password"
                });
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

        var result = await _sessionService.CreateAdminSessionAsync(request.ApiKey, HttpContext, account);
        if (result == null)
        {
            // The key checked above is the key this rechecks, so the two disagree only when the key was
            // rotated in between - ApiKeyService.RegenerateApiKey replaces it for the whole process.
            // The caller is told what everyone else is told, having genuinely arrived with a key that no
            // longer works.
            _logger.LogWarning("Login lost a race with an API key rotation");
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.InvalidCredentials,
                    Error = "Invalid API key, username or password"
                });
        }

        var (rawToken, session) = result.Value;

        _accountLockout.Clear(account.Id);
        await StampSignInAsync(
            account.Id,
            request.Password,
            rewriteHash: password == PasswordVerificationResult.SuccessRehashNeeded);

        // The account is the actor and the target: it is the identity the event is about. [49h]
        await _identityAuditService.RecordAsync(
            IdentityAuditEvent.LoginSucceeded,
            performedByAccountId: account.Id,
            performedBySessionId: session.Id,
            targetAccountId: account.Id);

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
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc)
        });
    }

    /// <summary>
    /// Changes the password of the account the caller is signed in as.
    /// </summary>
    /// <remarks>
    /// Only your own, and only with the current password: a session that has been left open on a shared
    /// machine is not permission to change what the password is.
    ///
    /// A wrong current password counts against the same per-account limit a failed sign-in does. Without
    /// that, an attacker holding any live session guesses here instead of at the sign-in screen and the
    /// lockout never fires. [43b]
    ///
    /// The session's token is replaced on success and the one the caller arrived with stops working,
    /// so a stolen cookie does not outlive the password it was obtained under. [46]
    /// </remarks>
    [EnableRateLimiting("auth")]
    [HttpPost("password")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MessageResponse>> ChangePasswordAsync([FromBody] ChangePasswordRequest request)
    {
        // [Authorize] answers a caller with no session at all, which is a 401 and no body. What reaches
        // here without an account is a session that has one of the two shapes that never had one: a
        // caller carrying only the API key, and the shared session used while authentication is
        // disabled. Neither has a password to change. [47]
        var session = HttpContext.GetUserSession();
        if (session?.AccountId is not { } accountId)
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.AccountRequired,
                    Error = "This session is not signed in as an account"
                });
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var account = await context.UserAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null)
        {
            // The account was deleted while this session was open. SessionService.ValidateSessionAsync
            // rejects such a session on its next request, so this is the same answer arriving here
            // first rather than a state of its own. [29]
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.AccountRequired,
                    Error = "This session is not signed in as an account"
                });
        }

        if (_accountLockout.IsLocked(accountId))
        {
            return StatusCode(
                StatusCodes.Status403Forbidden,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.AccountLocked,
                    Error = "Too many failed password attempts. Try again later."
                });
        }

        var current = _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, request.CurrentPassword);
        if (current == PasswordVerificationResult.Failed)
        {
            _accountLockout.RecordFailure(accountId);
            return StatusCode(
                StatusCodes.Status401Unauthorized,
                new CredentialRefusalResponse
                {
                    StageKey = CredentialRefusalResponse.PasswordIncorrect,
                    Error = "The current password is incorrect"
                });
        }

        // The rules the account was created under, run against the replacement, so a password that
        // could not have been chosen at sign-up cannot be arrived at by changing to it. The single
        // validator is the one place those rules live, which is why this builds one rather than
        // restating them. [26]
        var proposed = new AccountCredentialsRequestValidator().Validate(
            new AccountCredentialsRequest { Username = account.Username, Password = request.NewPassword });
        if (!proposed.IsValid)
        {
            return BadRequest(new CredentialRefusalResponse
            {
                StageKey = CredentialRefusalResponse.PasswordRejected,
                Error = proposed.Errors[0].ErrorMessage
            });
        }

        account.PasswordHash = _passwordHasher.HashPassword(account, request.NewPassword);
        await context.SaveChangesAsync();

        _accountLockout.Clear(accountId);

        // The session survives the change and gets a new token, rather than being revoked: signing the
        // person out of the screen they just used is not what changing a password is for. What must not
        // survive is the token they arrived with, which was obtained under the old password. [46]
        //
        // Rotation on its own does neither reliably. It declines while a previous rotation is inside
        // its 30-second grace period - and /api/auth/status rotates on a schedule, so that window is
        // open often - which would leave the token untouched. And it then puts the arriving token into
        // that same grace window, which keeps a stolen copy working for another 30 seconds. Closing the
        // window on both sides of the call is what makes the turnover certain and immediate.
        await EndPreviousTokenGraceAsync(context, session.Id);
        var rotated = await _sessionService.RotateSessionTokenAsync(session, HttpContext);
        await EndPreviousTokenGraceAsync(context, session.Id);

        if (rotated == null)
        {
            // Reached when the session row is gone, which is what regenerating the API key does to
            // every session (SessionService.ClearAllSessionsAsync). The password is already changed and
            // the caller has to sign in again, which is what the missing row means anyway.
            _logger.LogWarning("Session {SessionId} was gone before its token could be replaced", session.Id);
        }

        _logger.LogInformation("Password changed for account {AccountId}", accountId);

        return Ok(MessageResponse.Ok("Password changed"));
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
            ExpiresAt = DateTime.SpecifyKind(session.ExpiresAtUtc, DateTimeKind.Utc)
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
    /// Requires a session. The login screen no longer reads it, so it advertises the guest-mode
    /// settings to callers who already hold a session instead of to anyone at all. [55]
    /// </remarks>
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
    /// Any session may read it, a guest's included, so the guest onboarding screen shows these
    /// settings once the visitor has signed in rather than to anyone who can reach the port. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    [Authorize(Policy = "AccountHolder")]
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
    [Authorize(Policy = "AccountHolder")]
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
    /// their own guest-prefill config endpoints below. Any session may read it, a guest's
    /// included, once the visitor has signed in. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    /// Any session may read it, a guest's included, so the guest onboarding screen shows these
    /// defaults once the visitor has signed in rather than to anyone who can reach the port. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    /// Any session may read it, a guest's included, so the guest onboarding screen shows these
    /// defaults once the visitor has signed in rather than to anyone who can reach the port. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    /// Any session may read it, a guest's included, so the guest onboarding screen shows these
    /// defaults once the visitor has signed in rather than to anyone who can reach the port. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    /// Any session may read it, a guest's included, so the guest onboarding screen shows these
    /// defaults once the visitor has signed in rather than to anyone who can reach the port. [55]
    /// </remarks>
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
    [Authorize(Policy = "AccountHolder")]
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
    [Authorize(Policy = "AccountHolder")]
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

    /// <summary>
    /// Finds the account a username names. The column is citext, so the comparison is
    /// case-insensitive at the database and "Admin" and "admin" are the same person - which is the
    /// same rule the unique index enforces (AppDbContext.cs:213-217).
    /// </summary>
    private async Task<UserAccount?> FindAccountAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        var trimmed = username.Trim();
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.UserAccounts.FirstOrDefaultAsync(a => a.Username == trimmed);
    }

    /// <summary>
    /// Verifies a password, spending the same work whether or not the username named anybody.
    /// </summary>
    /// <remarks>
    /// An unknown username that returned without hashing would answer in a few milliseconds where a
    /// known one takes a few hundred, which tells a caller which usernames exist no matter how
    /// carefully the two are given the same message. Verifying against a hash nobody holds is what
    /// closes that, and it is the same move ApiKeyService.ValidateApiKey:107-112 makes for a key of
    /// the wrong length. [44]
    ///
    /// The stand-in hash is produced by the injected hasher so it carries the configured iteration
    /// count: the count is read out of the hash itself, so a cheaper one would be a cheaper compare
    /// and would leak the same thing. Two requests arriving before it exists each build one and one
    /// wins, which costs a hash rather than a fault.
    /// </remarks>
    private PasswordVerificationResult VerifyPassword(UserAccount? account, string password)
    {
        if (account != null)
        {
            return _passwordHasher.VerifyHashedPassword(account, account.PasswordHash, password);
        }

        _absentAccountHash ??= _passwordHasher.HashPassword(new UserAccount(), Guid.NewGuid().ToString());
        _passwordHasher.VerifyHashedPassword(new UserAccount(), _absentAccountHash, password);
        return PasswordVerificationResult.Failed;
    }

    /// <summary>
    /// Records the sign-in on the account row: the time it happened, and a replacement hash when the
    /// stored one was written at a lower iteration count than the application now configures.
    /// </summary>
    /// <remarks>
    /// One <c>SaveChangesAsync</c> covers both, so an installation whose hasher was stepped up cannot
    /// end up with a rewritten hash and no record of the sign-in, or the reverse. The rehash is the
    /// only moment the raw password is in hand, which is why it happens here rather than on a sweep.
    /// [42]
    /// </remarks>
    private async Task StampSignInAsync(Guid accountId, string password, bool rewriteHash)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var account = await context.UserAccounts.FirstOrDefaultAsync(a => a.Id == accountId);
        if (account == null)
        {
            // The account was deleted between the sign-in and this write. The session it just minted is
            // rejected on its next request by SessionService.ValidateSessionAsync, so there is nothing
            // to stamp and nothing left to sign in as. [29]
            return;
        }

        account.LastLoginAtUtc = DateTime.UtcNow;
        if (rewriteHash)
        {
            account.PasswordHash = _passwordHasher.HashPassword(account, password);
        }

        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Ends the grace period a token rotation leaves behind, so the previous token stops working now
    /// rather than in thirty seconds. Only the password change needs this: everywhere else the grace
    /// period is what keeps a caller's in-flight requests and open tabs alive. [46]
    /// </summary>
    private static async Task EndPreviousTokenGraceAsync(AppDbContext context, Guid sessionId)
    {
        await context.UserSessions
            .Where(s => s.Id == sessionId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.PreviousSessionTokenHash, (string?)null)
                .SetProperty(x => x.PreviousTokenValidUntilUtc, (DateTime?)null));
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
public class ChangePasswordRequest
{
    /// <summary>
    /// The password the account has now. Required, so a session left open on a shared machine is not
    /// on its own permission to change what the password is.
    /// </summary>
    public string CurrentPassword { get; set; } = string.Empty;

    /// <summary>
    /// The replacement, held to the same rules an account is created under.
    /// </summary>
    public string NewPassword { get; set; } = string.Empty;
}

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
