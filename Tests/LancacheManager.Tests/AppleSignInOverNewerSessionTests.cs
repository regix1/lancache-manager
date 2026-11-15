using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace LancacheManager.Tests;

/// <summary>
/// Apple returns the person with a cross-site form post, and a browser sends no SameSite=Lax cookie with
/// a cross-site post, so that post does not say which session the browser holds. A session another tab
/// of the browser made while the person was on Apple's page is the one the Apple sign-in replaces, and
/// it has to end, the same as for GitHub and OIDC.
///
/// The whole app runs here, with the real Apple OpenID Connect handler. Only Apple's side is faked: its
/// signing key and token endpoint answer on the handler's backchannel. The browser is a cookie jar that
/// sends only SameSite=None cookies on Apple's cross-site post, and follows every redirect the app
/// answers with that stays on this site and inside /api, sending its SameSite=Lax cookies too, since
/// a browser sends those on a top-level GET.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AppleSignInOverNewerSessionTests
{
    private const string SessionCookieName = "LancacheManager.Session";
    private const string AppleIssuer = "https://appleid.apple.com";
    private const string AppleClientId = "com.example.web";
    private const string AppleTokenEndpoint = "https://appleid.apple.com/auth/token";
    private const string OwnerSubject = "owner";
    private static readonly Uri Site = new("https://lancache.example.test");

    // Every session the browser held when the Apple sign-in started (an account, a guest, or none),
    // every session another tab makes, and both orders of that other sign-in and the start of this one.
    public static TheoryData<SessionType?, SessionType, bool> SignInsMadeWhileAtApple()
    {
        var cases = new TheoryData<SessionType?, SessionType, bool>();
        foreach (var heldAtStart in new SessionType?[] { SessionType.Admin, SessionType.Guest, null })
            foreach (var madeMeanwhile in new[] { SessionType.Admin, SessionType.User, SessionType.Guest })
                foreach (var whileAtApple in new[] { true, false })
                    cases.Add(heldAtStart, madeMeanwhile, whileAtApple);
        return cases;
    }

    // One tab starts an Apple sign-in. Before Apple sends the person back, another tab of the same browser
    // signs in, which ends the session the browser held and sets a new cookie. The Apple sign-in then
    // replaces that newer cookie, so no browser holds the newer session any more and the sign-in has to end
    // it. whileAtApple false is the other tab signing in before the Apple sign-in started.
    [Theory]
    [MemberData(nameof(SignInsMadeWhileAtApple))]
    public async Task AppleSignIn_AfterAnotherTabSignedIn_EndsTheSessionTheBrowserHeldWhenAppleSentItBack(
        SessionType? heldAtStart, SessionType madeMeanwhile, bool whileAtApple)
    {
        using var appleKey = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(appleKey) { KeyId = "apple-test-key" };
        var apple = new AppleTokenAnswer(signingKey);
        var appleConfiguration = new OpenIdConnectConfiguration
        {
            Issuer = AppleIssuer,
            AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize",
            TokenEndpoint = AppleTokenEndpoint,
            JwksUri = "https://appleid.apple.com/auth/keys"
        };
        appleConfiguration.SigningKeys.Add(signingKey);

        using var host = new EndpointAuthorizationHost();
        using var application = host.Application.WithWebHostBuilder(webHost => webHost.ConfigureTestServices(
            services => services.PostConfigure<OpenIdConnectOptions>(AccessService.AppleScheme, options =>
            {
                options.ConfigurationManager =
                    new StaticConfigurationManager<OpenIdConnectConfiguration>(appleConfiguration);
                options.Backchannel = new HttpClient(apple);
            })));
        using var scope = application.Services.CreateScope();
        var app = scope.ServiceProvider;
        var sessions = app.GetRequiredService<SessionService>();
        var contexts = app.GetRequiredService<IDbContextFactory<AppDbContext>>();

        using var appleSigningKeyPem = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var access = app.GetRequiredService<AccessService>();
        access.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = LoginKind.Apple,
                    ClientId = AppleClientId,
                    ClientSecret = string.Empty,
                    TeamId = "TEAM123456",
                    KeyId = "KEY1234567",
                    PrivateKey = appleSigningKeyPem.ExportPkcs8PrivateKeyPem()
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("apple", setup: true));
        Assert.True(access.PromotePendingLogin("apple", pending.Revision, AppleIssuer, OwnerSubject));
        app.GetRequiredService<StateService>().UpdateState(state =>
            state.Access.SetupVersion = AccessSettings.RequiredSetupVersion);

        UserAccount NewAccount(string name, SessionType role) => new()
        {
            Id = Guid.NewGuid(),
            Username = $"{name}-{Guid.NewGuid():N}",
            Role = role,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var deputy = NewAccount("deputy", SessionType.Admin);
        var colleague = NewAccount("colleague", SessionType.Admin);
        var member = NewAccount("member", SessionType.User);
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(deputy, colleague, member);
            await context.SaveChangesAsync();
        }

        // An account session for the account given, or a guest session when there is none.
        async Task<string> NewSessionAsync(UserAccount? account) => account is null
            ? Assert.NotNull(await sessions.CreateGuestSessionAsync(new DefaultHttpContext())).RawToken
            : (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account)).RawToken;

        var browser = new BrowserCookies();
        UserSession? held = null;
        if (heldAtStart is { } startKind)
        {
            var token = await NewSessionAsync(startKind == SessionType.Guest ? null : deputy);
            held = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(token));
            Assert.Equal(startKind, held.SessionType);
            browser.Set(SessionCookieName, token, SameSiteMode.Lax);
        }

        // The other tab signs in: it gets a new session and its cookie, and the session the browser held
        // ends, as the password sign-in and the guest start do.
        UserSession? meanwhile = null;
        async Task OtherTabSignsInAsync()
        {
            var token = await NewSessionAsync(madeMeanwhile switch
            {
                SessionType.Admin => colleague,
                SessionType.User => member,
                _ => null
            });
            meanwhile = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(token));
            Assert.Equal(madeMeanwhile, meanwhile.SessionType);
            if (held is { SessionType: SessionType.Guest })
                await sessions.EndGuestSessionAsync(held.Id, app);
            else
                await sessions.EndReplacedSessionAsync(held, app);
            browser.Set(SessionCookieName, token, SameSiteMode.Lax);
            held = meanwhile;
        }

        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = Site
        });

        if (!whileAtApple)
            await OtherTabSignsInAsync();

        using var start = browser.Request(HttpMethod.Post, new Uri(Site, "/api/auth/login/start"), crossSite: false);
        start.Content = JsonContent.Create(new LoginStartRequest { LoginId = "apple" });
        using var started = await browser.SendAsync(client, start);
        Assert.True(
            started.StatusCode == HttpStatusCode.OK,
            $"the Apple sign-in did not start: {(int)started.StatusCode} {await started.Content.ReadAsStringAsync()}");
        var startUrl = Assert.IsType<OidcStartResponse>(await started.Content.ReadFromJsonAsync<OidcStartResponse>()).Url;

        using var challenge = browser.Request(HttpMethod.Get, new Uri(Site, startUrl), crossSite: false);
        using var challenged = await browser.SendAsync(client, challenge);
        Assert.Equal(HttpStatusCode.Redirect, challenged.StatusCode);
        var authorize = Assert.IsType<Uri>(challenged.Headers.Location);
        Assert.Equal("appleid.apple.com", authorize.Host);
        var authorizeQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        apple.Nonce = authorizeQuery["nonce"].ToString();
        var state = authorizeQuery["state"].ToString();
        Assert.False(string.IsNullOrEmpty(apple.Nonce));
        Assert.False(string.IsNullOrEmpty(state));

        // The person is on Apple's page now.
        if (whileAtApple)
            await OtherTabSignsInAsync();
        var replaced = Assert.IsType<UserSession>(meanwhile);

        // Apple posts the person back from its own site.
        using var callback = browser.Request(
            HttpMethod.Post,
            new Uri(Site, AccessService.GetCallbackPath(LoginKind.Apple, setup: false)),
            crossSite: true);
        callback.Headers.TryAddWithoutValidation("Origin", AppleIssuer);
        callback.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "apple-authorization-code",
            ["state"] = state
        });
        var answer = await browser.SendAsync(client, callback);

        // The browser follows the app's redirects that stay on this site and inside /api.
        var landing = (string?)null;
        for (var hop = 0; hop < 5; hop++)
        {
            var next = answer.Headers.Location is { } location
                && (int)answer.StatusCode is >= 300 and < 400
                    ? new Uri(Site, location)
                    : null;
            if (next is null || next.Host != Site.Host || !next.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
            {
                landing = next?.PathAndQuery;
                break;
            }

            answer.Dispose();
            using var follow = browser.Request(HttpMethod.Get, next, crossSite: false);
            answer = await browser.SendAsync(client, follow);
        }
        var landingStatus = answer.StatusCode;
        var landingBody = await answer.Content.ReadAsStringAsync();
        answer.Dispose();

        Assert.True(
            landing == "/",
            $"the Apple sign-in did not land on the app: {(int)landingStatus} to {landing ?? "nowhere"} {landingBody}");
        var signedIn = await sessions.ValidateSessionAsync(Assert.IsType<string>(browser.Value(SessionCookieName)));
        Assert.True(
            signedIn is not null && signedIn.Id != replaced.Id,
            "after the Apple sign-in the browser does not hold a live session of its own");

        var when = whileAtApple ? "while the person was on Apple's page" : "before the Apple sign-in started";
        var startHeld = heldAtStart switch
        {
            SessionType.Admin => "an Admin account session",
            SessionType.Guest => "a guest session",
            _ => "no session"
        };
        var made = madeMeanwhile == SessionType.Guest ? "guest" : $"{madeMeanwhile} account";
        Assert.True(
            (await sessions.GetSessionByIdAsync(replaced.Id))!.IsRevoked,
            $"the {made} session another tab made {when} (the browser held {startHeld} at the start) is still live after the Apple sign-in replaced the cookie that named it, so no browser holds it and nothing ends it");
    }

    /// <summary>
    /// The cookies one browser holds for this site, with the SameSite rule the browser applies to each.
    /// A cookie set with no SameSite attribute is treated as Lax, as browsers do.
    /// </summary>
    private sealed class BrowserCookies
    {
        private readonly Dictionary<string, (string Value, SameSiteMode SameSite)> _cookies = new(StringComparer.Ordinal);

        public void Set(string name, string value, SameSiteMode sameSite) => _cookies[name] = (value, sameSite);

        public string? Value(string name) => _cookies.TryGetValue(name, out var cookie) ? cookie.Value : null;

        // A request from another site sends only the SameSite=None cookies. A request from this site, or a
        // top-level GET a redirect from this site starts, sends the others as well.
        public HttpRequestMessage Request(HttpMethod method, Uri url, bool crossSite)
        {
            var request = new HttpRequestMessage(method, url);
            var sent = _cookies
                .Where(cookie => !crossSite || cookie.Value.SameSite == SameSiteMode.None)
                .Select(cookie => $"{cookie.Key}={cookie.Value.Value}")
                .ToArray();
            if (sent.Length > 0)
                request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", sent));
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", crossSite ? "cross-site" : "same-origin");
            return request;
        }

        public async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
        {
            var response = await client.SendAsync(request);
            if (response.Headers.TryGetValues("Set-Cookie", out var headers)
                && SetCookieHeaderValue.TryParseList(headers.ToList(), out var cookies))
            {
                foreach (var cookie in cookies)
                {
                    var name = cookie.Name.ToString();
                    if ((cookie.MaxAge is { } maxAge && maxAge <= TimeSpan.Zero)
                        || (cookie.Expires is { } expires && expires <= DateTimeOffset.UtcNow))
                    {
                        _cookies.Remove(name);
                        continue;
                    }

                    _cookies[name] = (
                        cookie.Value.ToString(),
                        cookie.SameSite switch
                        {
                            Microsoft.Net.Http.Headers.SameSiteMode.None => SameSiteMode.None,
                            Microsoft.Net.Http.Headers.SameSiteMode.Strict => SameSiteMode.Strict,
                            _ => SameSiteMode.Lax
                        });
                }
            }
            return response;
        }
    }

    /// <summary>
    /// Apple's token endpoint: it trades the authorization code for an ID token that names the owner,
    /// carries the nonce of the sign-in it answers, and is signed with the key Apple's metadata lists.
    /// </summary>
    private sealed class AppleTokenAnswer(SecurityKey signingKey) : HttpMessageHandler
    {
        public string? Nonce { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.ToString() != AppleTokenEndpoint)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            const string accessToken = "apple-access-token";
            var hash = SHA256.HashData(Encoding.ASCII.GetBytes(accessToken));
            var now = DateTime.UtcNow;
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = AppleIssuer,
                Audience = AppleClientId,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddMinutes(5),
                Claims = new Dictionary<string, object>
                {
                    ["sub"] = OwnerSubject,
                    ["nonce"] = Nonce ?? string.Empty,
                    ["at_hash"] = Base64UrlEncoder.Encode(hash[..(hash.Length / 2)])
                },
                SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)
            });
            var body = $"{{\"access_token\":\"{accessToken}\",\"token_type\":\"Bearer\",\"expires_in\":3600,\"id_token\":\"{idToken}\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
