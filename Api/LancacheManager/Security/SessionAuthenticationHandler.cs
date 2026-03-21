using System.Security.Claims;
using System.Text.Encodings.Web;
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
        var rawToken = SessionService.GetSessionTokenFromCookie(Context);
        if (string.IsNullOrEmpty(rawToken))
            return AuthenticateResult.NoResult();

        // 2. Validate session
        var sessionService = Context.RequestServices.GetRequiredService<SessionService>();
        var session = await sessionService.ValidateSessionAsync(rawToken);
        if (session == null)
            return AuthenticateResult.Fail("Invalid session");

        // 3. Store session in HttpContext.Items for backward compatibility
        Context.Items["Session"] = session;

        // 4. Fire-and-forget last seen update
        _ = sessionService.UpdateLastSeenAsync(session);

        // 5. Build ClaimsPrincipal
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, session.Id.ToString()),
            new(ClaimTypes.Role, session.SessionType),
            new("SessionType", session.SessionType),
        };

        // Add prefill access claims: admins always have access, guests need valid expiry
        if (session.SessionType == "admin"
            || (session.SteamPrefillExpiresAtUtc != null && session.SteamPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("SteamPrefillActive", "true"));

        if (session.SessionType == "admin"
            || (session.EpicPrefillExpiresAtUtc != null && session.EpicPrefillExpiresAtUtc > DateTime.UtcNow))
            claims.Add(new Claim("EpicPrefillActive", "true"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
