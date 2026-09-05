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

    private bool _invalidKeysThrottled;

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
            if (!authenticationEnabled)
            {
                // Resolve through the existing shared-session outage gate first. That prevents a
                // stale cookie from bypassing its retry hold-off, while a working database still lets
                // an account-backed owner cookie take precedence over the anonymous shared session.
                var shared = await sessionService.GetOrCreateAuthDisabledAdminSessionAsync(Context);
                if (shared is null)
                {
                    return AuthenticateResult.NoResult();
                }

                if (!string.IsNullOrEmpty(rawToken))
                {
                    session = await sessionService.ValidateSessionAsync(rawToken);
                    if (session is not null
                        && !await Context.RequestServices
                            .GetRequiredService<AccessService>()
                            .IsMainAdminAsync(session))
                    {
                        session = null;
                    }
                }

                if (session is null)
                {
                    sessionService.SetSessionCookie(Context, shared.Value.RawToken, shared.Value.Session.ExpiresAtUtc);
                    session = shared.Value.Session;
                }
            }
            else if (!string.IsNullOrEmpty(rawToken))
            {
                session = await sessionService.ValidateSessionAsync(rawToken);
            }

            if (authenticationEnabled
                && session is { SessionType: SessionType.Admin, AccountId: null }
                && Context.RequestServices.GetRequiredService<AccessService>().RejectAccountlessAdminSession())
            {
                session = null;
            }
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
            // cancelled request being misreported as a failure.
            Logger.LogError(ex, "Could not reach the database to validate a session cookie; the request continues unauthenticated");
            return AuthenticateResult.NoResult();
        }

        // The key stands in for a session on the API reference and the document behind it, and
        // nowhere else. Those two are read by client generators and by an operator who holds the key
        // and has no account yet: neither has a cookie jar, and neither can run the sign-in POST. On
        // every other route a key alone no longer authenticates, so a caller that sends only the
        // header is answered like any other caller with no session.
        if (session == null
            && authenticationEnabled
            && Context.Request.Headers.ContainsKey("X-Api-Key")
            && (Context.Request.Path.StartsWithSegments("/scalar")
                || Context.Request.Path.StartsWithSegments("/openapi")))
        {
            var authenticationHelper = Context.RequestServices.GetRequiredService<AuthenticationHelper>();
            var apiKeyResult = authenticationHelper.ValidateApiKey(Context);
            if (!apiKeyResult.IsAuthenticated)
            {
                // A wrong key is answered 401 by the challenge below, like any other failed
                // authentication. Once the caller's address has spent its budget of wrong keys the
                // helper says so, and that has to reach the response or a flood looks exactly like a
                // single mistyped key.
                _invalidKeysThrottled = apiKeyResult.StatusCode == StatusCodes.Status429TooManyRequests;
                return AuthenticateResult.Fail("Invalid API key");
            }

            // The key path carried no session at all until now, so the caller was an admin to the
            // policies and nobody to GetUserSession(), and GetRequiredSessionId() threw. One session is
            // resolved for the whole process rather than minted per request: the key is checked on
            // every request that carries the header to one of these two routes, so anything polling
            // the document would otherwise add a row per fetch. Read the header the same way
            // AuthenticationHelper.ExtractApiKey does, taking the first value: joining several into
            // one string would have the check above accept a request that sends the header twice and
            // the resolve below refuse it.
            var keySession = await sessionService.GetOrCreateApiKeySessionAsync(
                Context.Request.Headers["X-Api-Key"].FirstOrDefault() ?? string.Empty, Context);
            if (keySession == null)
            {
                // The key is right but no session came back, which means the database did not answer.
                // Same reasoning as the two branches around this one: the request keeps the
                // authentication its key earned rather than losing it to an outage, and it is left
                // exactly as it was before this session existed - no session in Items, so nothing here
                // stands in for a row the database could not confirm.
                return AuthenticateResult.Success(CreateTicket(Guid.Empty, SessionType.Admin));
            }

            Context.Items["Session"] = keySession;
            return AuthenticateResult.Success(CreateTicket(keySession.Id, keySession.SessionType));
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
        // NOTE: Claim values are lowercase strings ("admin"/"user"/"guest") to match existing
        // AuthorizationPolicy.RequireClaim("SessionType", "admin", "user") in Program.cs and legacy
        // cookies.
        var sessionTypeClaim = sessionType.ToString().ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, sessionId.ToString()),
            new(ClaimTypes.Role, sessionTypeClaim),
            new("SessionType", sessionTypeClaim),
        };

        // The account behind the session, for the endpoints that answer questions about the caller's
        // own account rather than about its session. The claim is absent rather than empty for the
        // three kinds of session created without one, which UserSession.AccountId lists, so a reader
        // that finds it never has to check it for a placeholder. The session type above is that
        // account's role: it is copied onto the session row when the account signs in, and
        // SessionService.RevokeAccountSessionsAsync exists so that changing the role ends the
        // sessions carrying the old copy instead of leaving it to go stale.
        if (session?.AccountId is { } accountId)
        {
            claims.Add(new Claim("AccountId", accountId.ToString()));
        }

        // Add prefill access claims: account holders always have access, guests need valid expiry
        if (session != null)
        {
            foreach (var (platform, claimType) in _prefillClaims)
            {
                if (sessionType.IsAccountHolder()
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
        Context.Response.StatusCode = _invalidKeysThrottled
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
