using System.Security.Claims;
using System.Text.Encodings.Web;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace LancacheManager.Security;

public class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Session";

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Extract session token from cookie
        var rawToken = SessionService.TokenFromCookie(Context);
        if (string.IsNullOrEmpty(rawToken))
            return AuthenticateResult.NoResult();

        // 2. Validate session
        var sessionService = Context.RequestServices.GetRequiredService<SessionService>();
        UserSession? session;
        try
        {
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

        if (session == null)
            return AuthenticateResult.Fail("Invalid session");

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
        // NOTE: Claim values are lowercase strings ("admin"/"guest") to match existing
        // AuthorizationPolicy.RequireClaim("SessionType", "admin") in Program.cs and legacy cookies.
        var sessionTypeClaim = session.SessionType.ToString().ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.Id.ToString()),
            new(ClaimTypes.Role, sessionTypeClaim),
            new("SessionType", sessionTypeClaim),
        };

        // Add prefill access claims: admins always have access, guests need valid expiry
        if (session.SessionType == SessionType.Admin
            || (session.SteamPrefillExpiresAtUtc != null && session.SteamPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("SteamPrefillActive", "true"));

        if (session.SessionType == SessionType.Admin
            || (session.EpicPrefillExpiresAtUtc != null && session.EpicPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("EpicPrefillActive", "true"));

        if (session.SessionType == SessionType.Admin
            || (session.BattleNetPrefillExpiresAtUtc != null && session.BattleNetPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("BattleNetPrefillActive", "true"));

        if (session.SessionType == SessionType.Admin
            || (session.RiotPrefillExpiresAtUtc != null && session.RiotPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("RiotPrefillActive", "true"));

        if (session.SessionType == SessionType.Admin
            || (session.XboxPrefillExpiresAtUtc != null && session.XboxPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("XboxPrefillActive", "true"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
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
