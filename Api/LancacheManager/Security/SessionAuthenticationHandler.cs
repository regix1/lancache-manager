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
        var session = await sessionService.ValidateSessionAsync(rawToken);
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
            _ = sessionService.UpdateLastSeenAsync(session);
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
