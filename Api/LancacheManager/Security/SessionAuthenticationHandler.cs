using System.Security.Claims;
using System.Text.Encodings.Web;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LancacheManager.Security;

public class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Session";

    private static readonly (PrefillPlatform Platform, string ClaimType)[] _prefillClaims =
    [
        (PrefillPlatform.Steam, "SteamPrefillActive"),
        (PrefillPlatform.Epic, "EpicPrefillActive"),
        (PrefillPlatform.BattleNet, "BattleNetPrefillActive"),
        (PrefillPlatform.Riot, "RiotPrefillActive"),
        (PrefillPlatform.Xbox, "XboxPrefillActive")
    ];

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var sessionService = Context.RequestServices.GetRequiredService<SessionService>();
        var authenticationEnabled = sessionService.IsAuthenticationEnabled();

        // 1. Extract session token from cookie
        var rawToken = SessionService.TokenFromCookie(Context);

        // 2. Validate session
        UserSession? session = null;
        try
        {
            if (authenticationEnabled && !string.IsNullOrEmpty(rawToken))
                session = await sessionService.ValidateSessionAsync(rawToken);
        }
        catch (Exception ex)
        {
            // Authentication runs before authorization, so a database that cannot be reached would
            // otherwise fail every request carrying a session cookie, including the ones to endpoints
            // that ask for no authorization at all. Those are the endpoints the setup and error
            // screens are built from, so losing them is what turns a database outage into an
            // installation with no way back in. The request continues unauthenticated instead: no
            // session is published to Items and no principal is built, so nothing here can stand in
            // for the session the database could not confirm.
            //
            // Deliberately no retry. The connection is configured with EnableRetryOnFailure(3, 5s),
            // so a transient fault has already been retried by the time it surfaces here and trying
            // again would only hold the request open longer. Deliberately catching everything, too:
            // nothing in the stack distinguishes "server unreachable" from any other provider fault,
            // and ValidateSessionAsync takes no cancellation token, so nothing that arrives here is a
            // cancelled request being misreported as a failure. [26]
            Logger.LogError(ex, "Could not reach the database to validate a session cookie; the request continues unauthenticated");
            return AuthenticateResult.NoResult();
        }

        if (session == null
            && authenticationEnabled
            && Context.Request.Headers.ContainsKey("X-Api-Key"))
        {
            var authenticationHelper = Context.RequestServices.GetRequiredService<AuthenticationHelper>();
            if (!authenticationHelper.ValidateApiKey(Context).IsAuthenticated)
            {
                return AuthenticateResult.Fail("Invalid API key");
            }

            return AuthenticateResult.Success(CreateTicket(Guid.Empty, SessionType.Admin));
        }

        // With authentication turned off the frontend is told it is an admin and starts writing
        // preferences, joining the hubs and asking for prefill access straight away. All of those need
        // a real session, and the only thing that created one was GET /api/auth/status. Anything that
        // went out before that call landed, and everything sent while the browser still held a cookie
        // for a session that no longer exists, came back 400 "No session found" instead. Resolving the
        // shared session here means every request has one, whatever order they arrive in.
        if (session == null && !authenticationEnabled)
        {
            try
            {
                // A database that cannot be reached comes back as null rather than an exception, and the
                // service reports the first such failure itself and holds the next attempts off for a
                // few seconds. That keeps one outage from costing a database round trip and an error
                // line on every anonymous request, while the request still carries on unauthenticated
                // so the endpoints the error screens are built from stay reachable. [14]
                var shared = await sessionService.GetOrCreateAuthDisabledAdminSessionAsync(Context);
                if (shared is { } resolved)
                {
                    sessionService.SetSessionCookie(Context, resolved.RawToken, resolved.Session.ExpiresAtUtc);
                    session = resolved.Session;
                }
            }
            catch (Exception ex)
            {
                // Same reasoning as the validation failure above: the request carries on
                // unauthenticated rather than failing outright, so anything the shared-session path
                // could still throw does not take out those endpoints either.
                Logger.LogError(ex, "Could not create the shared session used while authentication is disabled");
            }
        }

        if (session == null)
            return !authenticationEnabled || string.IsNullOrEmpty(rawToken)
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Fail("Invalid session");

        // 3. Store session in HttpContext.Items for backward compatibility
        Context.Items["Session"] = session;

        // 4. Fire-and-forget last seen update - skip only when the browser explicitly reports no
        // genuine recent interaction (X-User-Active: false), so a tab sitting open and VISIBLE but
        // untouched on an unattended screen doesn't keep resetting LastSeenAtUtc from its own ambient
        // background refetches as if a human were present. Mere tab visibility is not enough here -
        // see Web/src/utils/userInteractionTracker.ts for why.
        //
        // SignalR hub requests (negotiate + the WebSocket upgrade itself) are excluded entirely,
        // header or not: the browser does not apply custom headers to a WebSocket handshake (only to
        // plain HTTP requests - see @microsoft/signalr's IHttpConnectionOptions.headers doc comment),
        // so X-User-Active can never actually reach these regardless of what the client sends. More
        // importantly, withAutomaticReconnect means a reconnect can fire from nothing but a network
        // blip, an idle NAT/router timeout, or a laptop waking from sleep - none of which is evidence
        // a human is present. Regular API traffic (dashboard polling, the heartbeat) is the real
        // presence signal; a hub reconnect must never independently refresh LastSeenAtUtc.
        var isHubRequest = Context.Request.Path.StartsWithSegments("/hubs");
        var userActive = !isHubRequest && !string.Equals(
            Context.Request.Headers["X-User-Active"].ToString(), "false", StringComparison.OrdinalIgnoreCase);
        if (userActive)
        {
            // Nothing awaits this, so the failure has to be reported from the task itself. The write
            // is the other database call on this path: an outage that begins after the validation
            // above lands here instead, and a last-seen write that has quietly stopped working shows
            // up only as stale times in the sessions list, with nothing in the log to explain them.
            var sessionId = session.Id;
            _ = sessionService.UpdateLastSeenAsync(session).ContinueWith(
                finished => Logger.LogWarning(
                    finished.Exception, "Could not record last-seen time for session {SessionId}", sessionId),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        // 5. Build ClaimsPrincipal
        return AuthenticateResult.Success(CreateTicket(session.Id, session.SessionType, session));
    }

    private AuthenticationTicket CreateTicket(Guid sessionId, SessionType sessionType, UserSession? session = null)
    {
        // NOTE: Claim values are lowercase strings ("admin"/"guest") to match existing
        // AuthorizationPolicy.RequireClaim("SessionType", "admin") in Program.cs and legacy cookies.
        var sessionTypeClaim = sessionType.ToString().ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sessionId.ToString()),
            new(ClaimTypes.Role, sessionTypeClaim),
            new("SessionType", sessionTypeClaim),
        };

        // Add prefill access claims: admins always have access, guests need valid expiry
        if (session != null)
        {
            foreach (var (platform, claimType) in _prefillClaims)
            {
                if (sessionType == SessionType.Admin
                    || (SessionService.GetPrefillExpiresAt(session, platform) is { } expiresAtUtc
                        && expiresAtUtc > DateTime.UtcNow))
                {
                    claims.Add(new Claim(claimType, "true"));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        return new AuthenticationTicket(principal, SchemeName);
    }

    /// <summary>
    /// Override the default challenge behaviour to log at Debug instead of the framework
    /// default (Information). This suppresses the "AuthenticationScheme: Session was
    /// challenged" message that floods production logs on every unauthenticated request
    /// (polling endpoints, guests viewing the login screen, etc.).
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Logger.LogDebug("AuthenticationScheme: {Scheme} was challenged.", Scheme.Name);
        Context.Response.StatusCode = 401;
        return Task.CompletedTask;
    }
}
