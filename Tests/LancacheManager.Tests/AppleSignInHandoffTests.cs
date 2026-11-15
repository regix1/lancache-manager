using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using SetCookieHeaderValue = Microsoft.Net.Http.Headers.SetCookieHeaderValue;

namespace LancacheManager.Tests;

/// <summary>
/// Two more Apple sign-in paths through the whole app and the real Apple OpenID Connect handler, with
/// only Apple's side faked (its signing key and token endpoint on the handler's backchannel), and the
/// same browser model as <see cref="AppleSignInOverNewerSessionTests"/>: Apple's cross-site form post
/// carries only SameSite=None cookies, and every redirect the app answers with that stays on this site
/// and inside /api is followed with a GET that carries the rest.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AppleSignInHandoffTests
{
    private const string SessionCookieName = "LancacheManager.Session";
    private const string AppleIssuer = "https://appleid.apple.com";
    private const string AppleClientId = "com.example.web";
    private const string AppleTokenEndpoint = "https://appleid.apple.com/auth/token";
    private const string OwnerSubject = "owner";
    private static readonly Uri Site = new("https://lancache.example.test");

    // The person stays on Apple's page longer than five minutes before Apple posts them back. With or
    // without another tab of the browser signing in meanwhile, the sign-in completes and ends the
    // session the browser holds when Apple sends the person back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AppleSignIn_PostedBackAfterMoreThanFiveMinutes_CompletesAndEndsTheSessionTheBrowserHolds(
        bool otherTabSignsInMeanwhile)
    {
        var clock = new MovableClock();
        using var appleKey = RSA.Create(2048);
        var apple = new AppleTokenAnswer(new RsaSecurityKey(appleKey) { KeyId = "apple-test-key" });

        using var host = new EndpointAuthorizationHost();
        using var application = host.Application.WithWebHostBuilder(webHost => webHost.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            apple.Configure(services, AccessService.AppleScheme);
        }));
        // The clock moved below is the one the app's handlers, the Apple cookie included, read.
        Assert.Same(clock, application.Services.GetRequiredService<TimeProvider>());
        Assert.Same(
            clock,
            application.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(AccessService.AppleCookieScheme).TimeProvider);

        using var scope = application.Services.CreateScope();
        var app = scope.ServiceProvider;
        var sessions = app.GetRequiredService<SessionService>();
        var contexts = app.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var access = app.GetRequiredService<AccessService>();
        using var applePrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        StageApple(access, applePrivateKey);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("apple", setup: true));
        Assert.True(access.PromotePendingLogin("apple", pending.Revision, AppleIssuer, OwnerSubject));
        app.GetRequiredService<LancacheManager.Infrastructure.Services.StateService>().UpdateState(state =>
            state.Access.SetupVersion = AccessSettings.RequiredSetupVersion);

        UserAccount NewAccount(string name) => new()
        {
            Id = Guid.NewGuid(),
            Username = $"{name}-{Guid.NewGuid():N}",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var deputy = NewAccount("deputy");
        var colleague = NewAccount("colleague");
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(deputy, colleague);
            await context.SaveChangesAsync();
        }

        var browser = new BrowserCookies(clock);
        var heldToken = (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), deputy)).RawToken;
        var held = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(heldToken));
        browser.Set(SessionCookieName, heldToken, SameSiteMode.Lax);

        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = Site
        });
        var state = await ChallengeAsync(client, browser, apple, new LoginStartRequest { LoginId = "apple" });

        // The person is on Apple's page. Another tab may sign in, and more than five minutes pass.
        if (otherTabSignsInMeanwhile)
        {
            var newerToken = (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), colleague)).RawToken;
            var newer = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(newerToken));
            await sessions.EndReplacedSessionAsync(held, app);
            browser.Set(SessionCookieName, newerToken, SameSiteMode.Lax);
            held = newer;
        }
        clock.Ahead = TimeSpan.FromMinutes(6);

        var (landing, failure) = await AppleSendsThePersonBackAsync(
            client,
            browser,
            AccessService.GetCallbackPath(LoginKind.Apple, setup: false),
            state);

        var meanwhile = otherTabSignsInMeanwhile ? "another tab signed in meanwhile" : "no other tab signed in";
        Assert.True(
            landing == "/",
            $"an Apple sign-in posted back six minutes after it started ({meanwhile}) did not complete: {failure}");
        var signedIn = await sessions.ValidateSessionAsync(Assert.IsType<string>(browser.Value(SessionCookieName)));
        Assert.True(
            signedIn is not null && signedIn.Id != held.Id,
            $"after an Apple sign-in posted back six minutes after it started ({meanwhile}) the browser does not hold a live session of its own");
        Assert.True(
            (await sessions.GetSessionByIdAsync(held.Id))!.IsRevoked,
            $"the session the browser held when Apple posted the person back six minutes after the start ({meanwhile}) is still live after the Apple sign-in replaced its cookie, so no browser holds it and nothing ends it");
    }

    // External-database setup: the account database does not exist yet, so the owner's Apple sign-in
    // waits for it. The browser used the app at this address before and still holds that session's
    // cookie, and the sign-in must finish setup without reading the database for it.
    [Fact]
    public async Task AppleOwnerSignIn_DuringDatabaseSetupWithAnOldSessionCookie_FinishesSetupWithoutTheDatabase()
    {
        using var appleKey = RSA.Create(2048);
        var apple = new AppleTokenAnswer(new RsaSecurityKey(appleKey) { KeyId = "apple-test-key" });

        using var host = new EndpointAuthorizationHost();
        using var application = host.Application.WithWebHostBuilder(webHost => webHost.ConfigureTestServices(services =>
        {
            // The database that does not exist: every read or write of it throws.
            services.AddSingleton<IDbContextFactory<AppDbContext>>(new ThrowingDbContextFactory());
            apple.Configure(services, AccessService.AppleSetupScheme);
        }));
        var app = application.Services;
        Assert.True(app.GetRequiredService<IConfiguration>().GetValue<bool>("Runtime:DatabaseSetupPending"));
        var access = app.GetRequiredService<AccessService>();
        using var applePrivateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        StageApple(access, applePrivateKey);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("apple", setup: true));
        Assert.True(access.CanDeferOidcAccount("apple", pending.Revision));

        var browser = new BrowserCookies(TimeProvider.System);
        browser.Set(SessionCookieName, "session-from-before-the-database-move", SameSiteMode.Lax);
        using var client = application.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = false,
            BaseAddress = Site
        });
        var state = await ChallengeAsync(client, browser, apple, new LoginStartRequest
        {
            LoginId = "apple",
            Setup = true,
            ApiKey = app.GetRequiredService<ApiKeyService>().GetApiKey()
        });

        var (landing, failure) = await AppleSendsThePersonBackAsync(
            client,
            browser,
            AccessService.GetCallbackPath(LoginKind.Apple, setup: true),
            state);

        Assert.True(
            landing == "/?accessSetup=1&loginTest=success&loginId=apple",
            $"the Apple owner sign-in during external-database setup from a browser with an old session cookie did not finish setup: {failure}");
        Assert.Null(access.GetLoginSettings("apple", setup: true));
    }

    private static void StageApple(AccessService access, ECDsa applePrivateKey)
        => access.Apply(
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
                    PrivateKey = applePrivateKey.ExportPkcs8PrivateKeyPem()
                }
            },
            null);

    /// <summary>
    /// Starts the sign-in and opens its challenge, as the sign-in button does, and returns the state
    /// Apple will post back. The nonce of the authorize request goes to the fake token endpoint.
    /// </summary>
    private static async Task<string> ChallengeAsync(
        HttpClient client, BrowserCookies browser, AppleTokenAnswer apple, LoginStartRequest request)
    {
        using var start = browser.Request(HttpMethod.Post, new Uri(Site, "/api/auth/login/start"), crossSite: false);
        start.Content = JsonContent.Create(request);
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
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorize.Query);
        apple.Nonce = query["nonce"].ToString();
        var state = query["state"].ToString();
        Assert.False(string.IsNullOrEmpty(apple.Nonce));
        Assert.False(string.IsNullOrEmpty(state));
        return state;
    }

    /// <summary>
    /// Apple's cross-site form post, then every redirect the app answers with that stays on this site
    /// and inside /api. Returns the first location outside /api, and what the last answer said.
    /// </summary>
    private static async Task<(string? Landing, string Failure)> AppleSendsThePersonBackAsync(
        HttpClient client, BrowserCookies browser, string callbackPath, string state)
    {
        using var callback = browser.Request(HttpMethod.Post, new Uri(Site, callbackPath), crossSite: true);
        callback.Headers.TryAddWithoutValidation("Origin", AppleIssuer);
        callback.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["code"] = "apple-authorization-code",
            ["state"] = state
        });
        var answer = await browser.SendAsync(client, callback);
        try
        {
            for (var hop = 0; hop < 5; hop++)
            {
                var next = answer.Headers.Location is { } location
                    && (int)answer.StatusCode is >= 300 and < 400
                        ? new Uri(Site, location)
                        : null;
                if (next is null || next.Host != Site.Host || !next.AbsolutePath.StartsWith("/api/", StringComparison.Ordinal))
                {
                    return (next?.PathAndQuery,
                        $"{(int)answer.StatusCode} to {next?.PathAndQuery ?? "nowhere"} {await answer.Content.ReadAsStringAsync()}");
                }

                answer.Dispose();
                using var follow = browser.Request(HttpMethod.Get, next, crossSite: false);
                answer = await browser.SendAsync(client, follow);
            }
            return (null, $"more than five redirects inside /api, the last {(int)answer.StatusCode}");
        }
        finally
        {
            answer.Dispose();
        }
    }

    /// <summary>The system clock moved forward by <see cref="Ahead"/>, for the app and the browser alike.</summary>
    private sealed class MovableClock : TimeProvider
    {
        public TimeSpan Ahead { get; set; }

        public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow() + Ahead;
    }

    /// <summary>
    /// The cookies one browser holds for this site, with the SameSite rule the browser applies to each
    /// and the expiry it drops each at, read on the browser's clock. A cookie set with no SameSite
    /// attribute is treated as Lax, as browsers do.
    /// </summary>
    private sealed class BrowserCookies(TimeProvider clock)
    {
        private readonly Dictionary<string, (string Value, SameSiteMode SameSite, DateTimeOffset? Expires)> _cookies =
            new(StringComparer.Ordinal);

        public void Set(string name, string value, SameSiteMode sameSite) => _cookies[name] = (value, sameSite, null);

        public string? Value(string name) => _cookies.TryGetValue(name, out var cookie) ? cookie.Value : null;

        // A request from another site sends only the SameSite=None cookies. A request from this site, or a
        // top-level GET a redirect from this site starts, sends the others as well.
        public HttpRequestMessage Request(HttpMethod method, Uri url, bool crossSite)
        {
            var now = clock.GetUtcNow();
            foreach (var expired in _cookies.Where(cookie => cookie.Value.Expires <= now).Select(cookie => cookie.Key).ToList())
                _cookies.Remove(expired);
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
                var now = clock.GetUtcNow();
                foreach (var cookie in cookies)
                {
                    var name = cookie.Name.ToString();
                    DateTimeOffset? expires = cookie.MaxAge is { } maxAge ? now + maxAge : cookie.Expires;
                    if (expires <= now)
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
                        },
                        expires);
                }
            }
            return response;
        }
    }

    /// <summary>
    /// Apple's side: its configuration and signing key, and its token endpoint, which trades the
    /// authorization code for an ID token that names the owner and carries the nonce of the sign-in it
    /// answers. The token is valid from a minute before the real time to half an hour after it, so it
    /// is current on any clock this test moves.
    /// </summary>
    private sealed class AppleTokenAnswer(SecurityKey signingKey) : HttpMessageHandler
    {
        public string? Nonce { get; set; }

        public void Configure(IServiceCollection services, string scheme)
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = AppleIssuer,
                AuthorizationEndpoint = "https://appleid.apple.com/auth/authorize",
                TokenEndpoint = AppleTokenEndpoint,
                JwksUri = "https://appleid.apple.com/auth/keys"
            };
            configuration.SigningKeys.Add(signingKey);
            services.PostConfigure<OpenIdConnectOptions>(scheme, options =>
            {
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                options.Backchannel = new HttpClient(this);
            });
        }

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
                NotBefore = now.AddMinutes(-1),
                Expires = now.AddMinutes(30),
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
