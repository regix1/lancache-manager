using Microsoft.AspNetCore.Antiforgery;

namespace LancacheManager.Security;

/// <summary>
/// Hands the browser the antiforgery token it has to send back on every request that changes
/// something. The framework stores its own half of the pair in a cookie of its own; this writes the
/// half the page reads.
/// </summary>
public class AntiforgeryToken
{
    /// <summary>
    /// Header the browser sends the token back in, and the name the validation reads it from.
    /// </summary>
    public const string HeaderName = "X-Antiforgery-Token";

    /// <summary>
    /// Cookie the token is handed over in. The page reads it by this name
    /// (Web/src/utils/antiforgery.ts), so it is part of the wire contract rather than an internal
    /// detail.
    /// </summary>
    public const string CookieName = "LancacheManager.Antiforgery";

    /// <summary>
    /// What a caller is told when the token is absent or does not match. Two places answer that
    /// refusal: the filter every controller runs behind, and the setup endpoints, which opt out of
    /// that filter and check the token themselves for the caller that presented no API key.
    /// </summary>
    public const string MissingTokenMessage =
        "The antiforgery token is missing or does not match this session";

    private readonly IAntiforgery _antiforgery;
    private readonly IConfiguration _configuration;

    public AntiforgeryToken(IAntiforgery antiforgery, IConfiguration configuration)
    {
        _antiforgery = antiforgery;
        _configuration = configuration;
    }

    /// <summary>
    /// Writes the token for the caller this request resolved to. The token is tied to that caller, so
    /// it has to be re-issued whenever the caller changes - which is why this runs on the status call
    /// the client already makes after signing in, after starting a guest session and after signing out.
    /// </summary>
    public void Issue(HttpContext httpContext)
    {
        var tokens = _antiforgery.GetAndStoreTokens(httpContext);

        var forceSecure = _configuration.GetValue<bool>("Security:ForceSecureCookies");
        httpContext.Response.Cookies.Append(CookieName, tokens.RequestToken!, new CookieOptions
        {
            // Deliberately readable by script, and that is the mechanism rather than a hole in it.
            // The page has to read this value to put it in a header, and a page served by another
            // origin can do neither: it cannot read this origin's cookies, so it cannot produce the
            // header, so the request it forges is refused. The session cookie beside it stays
            // HttpOnly (SessionService.cs:806) because that one IS the credential. [77n]
            HttpOnly = false,
            // Same rule as the session cookie: marked Secure on a real HTTPS request, or whenever the
            // deployment opts in behind a TLS-terminating proxy, and left unmarked otherwise so plain
            // HTTP LAN installations keep working.
            Secure = forceSecure || httpContext.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Path = "/"
        });
    }
}
