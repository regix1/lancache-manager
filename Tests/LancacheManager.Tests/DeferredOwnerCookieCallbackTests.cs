using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// During external-database setup the account database does not exist yet, so the owner's GitHub or
/// OIDC sign-in waits for it. A browser that used the app at this address before still sends its old
/// session cookie with the callback, and that cookie must not make the waiting sign-in read the
/// database.
/// </summary>
public sealed class DeferredOwnerCookieCallbackTests : IDisposable
{
    private const string SessionCookieName = "LancacheManager.Session";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-deferred-cookie-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(LoginKind.GitHub)]
    [InlineData(LoginKind.Google)]
    [InlineData(LoginKind.CustomOidc)]
    public async Task DeferredOwnerCallbackFromABrowserWithAnOldSessionCookieFinishesSetup(LoginKind kind)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt"),
                ["Runtime:DatabaseSetupPending"] = bool.TrueString
            })
            .Build();
        var state = CreateStateService(_root);
        var missingDatabase = new ThrowingDbContextFactory();
        var access = new AccessService(state, configuration, missingDatabase);
        var loginId = kind switch
        {
            LoginKind.GitHub => "github",
            LoginKind.Google => "google",
            _ => "customOidc"
        };
        var response = access.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = kind,
                    ClientId = $"{kind}-client",
                    ClientSecret = "secret",
                    Authority = kind == LoginKind.CustomOidc ? "https://login.example.test" : null
                }
            },
            null);
        Assert.True(response.RequiresLoginTest);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(
            missingDatabase,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = SocialLoginTests.NewSignIns(access, missingDatabase, sessions);

        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = pending.Revision.ToString(CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["login_id"] = loginId;
        const string subject = "deferred-owner";

        // The browser used the app at this address before, so its callback carries that session's cookie.
        var callback = new DefaultHttpContext();
        callback.Request.Headers.Cookie = $"{SessionCookieName}=session-from-before-the-database-move";

        Exception? failure;
        if (kind == LoginKind.GitHub)
        {
            var scheme = new AuthenticationScheme(
                AccessService.GitHubSetupScheme,
                AccessService.GitHubSetupScheme,
                typeof(OAuthHandler<OAuthOptions>));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject)],
                scheme.Name));
            var context = new TicketReceivedContext(
                callback,
                scheme,
                new OAuthOptions(),
                new AuthenticationTicket(principal, properties, scheme.Name));
            var events = new GitHubEvents(
                access,
                signIns,
                new OptionsCache<OAuthOptions>(),
                TimeProvider.System,
                NullLogger<GitHubEvents>.Instance);

            failure = await Record.ExceptionAsync(() => events.TicketReceived(context));
        }
        else
        {
            properties.Items["oidc_issuer"] = kind == LoginKind.Google
                ? "https://accounts.google.com"
                : "https://login.example.test";
            properties.Items["oidc_subject"] = subject;
            var schemeName = AccessService.GetScheme(kind, setup: true);
            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OpenIdConnectHandler));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject)],
                scheme.Name));
            var context = new TicketReceivedContext(
                callback,
                scheme,
                new OpenIdConnectOptions(),
                new AuthenticationTicket(principal, properties, scheme.Name));
            var events = new OidcEvents(
                access,
                signIns,
                new OptionsCache<OpenIdConnectOptions>(),
                TimeProvider.System,
                NullLogger<OidcEvents>.Instance);

            failure = await Record.ExceptionAsync(() => events.TicketReceived(context));
        }

        Assert.True(
            failure is null,
            $"the {kind} owner sign-in during external-database setup failed because the browser sent an old "
            + $"session cookie, so the owner can never finish setup from this browser: {failure?.GetType().Name}: {failure?.Message}");
        Assert.Equal(StatusCodes.Status302Found, callback.Response.StatusCode);
        Assert.Contains("loginTest=success", callback.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Null(access.GetLoginSettings(loginId, setup: true));
    }

    // The owner starts the setup sign-in while sign-in is on, and the operator turns sign-in off (an
    // environment change and a restart) before the provider sends the browser back. Finishing that setup
    // would turn sign-in on again against the operator's choice, so the waiting sign-in is refused.
    [Theory]
    [InlineData(LoginKind.GitHub)]
    [InlineData(LoginKind.CustomOidc)]
    public async Task DeferredOwnerCallbackAfterSignInWasTurnedOffLeavesSignInOff(LoginKind kind)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt"),
                ["Runtime:DatabaseSetupPending"] = bool.TrueString
            })
            .Build();
        var state = CreateStateService(_root);
        var missingDatabase = new ThrowingDbContextFactory();
        var access = new AccessService(state, configuration, missingDatabase);
        var loginId = kind == LoginKind.GitHub ? "github" : "customOidc";
        const string issuer = "https://login.example.test";
        access.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = kind,
                    ClientId = $"{kind}-client",
                    ClientSecret = "secret",
                    Authority = kind == LoginKind.CustomOidc ? issuer : null
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
        Assert.True(access.IsAuthenticationEnabled(), "sign-in is not on when the setup sign-in starts");
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(
            missingDatabase,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = SocialLoginTests.NewSignIns(access, missingDatabase, sessions);

        // The transaction the challenge wrote while sign-in was on.
        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = pending.Revision.ToString(CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["access_sign_in_off"] = "false";
        properties.Items["login_id"] = loginId;
        const string subject = "deferred-owner";

        configuration["Security:EnableAuthentication"] = "false";
        Assert.False(access.IsAuthenticationEnabled(), "turning sign-in off did not take effect");

        var callback = new DefaultHttpContext();
        string location;
        if (kind == LoginKind.GitHub)
        {
            var scheme = new AuthenticationScheme(
                AccessService.GitHubSetupScheme,
                AccessService.GitHubSetupScheme,
                typeof(OAuthHandler<OAuthOptions>));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, subject)],
                scheme.Name));
            await new GitHubEvents(
                    access,
                    signIns,
                    new OptionsCache<OAuthOptions>(),
                    TimeProvider.System,
                    NullLogger<GitHubEvents>.Instance)
                .TicketReceived(new TicketReceivedContext(
                    callback,
                    scheme,
                    new OAuthOptions(),
                    new AuthenticationTicket(principal, properties, scheme.Name)));
            location = callback.Response.Headers.Location.ToString();
        }
        else
        {
            properties.Items["oidc_issuer"] = issuer;
            properties.Items["oidc_subject"] = subject;
            location = await new OidcEvents(
                    access,
                    signIns,
                    new OptionsCache<OpenIdConnectOptions>(),
                    TimeProvider.System,
                    NullLogger<OidcEvents>.Instance)
                .CompleteSignInAsync(properties, callback);
        }

        var wrong = new List<string>();
        if (!location.Contains("oidcError=unavailable", StringComparison.Ordinal))
            wrong.Add($"the callback sent the browser to {location}, not to the setup step's unavailable message");
        if (access.GetMode() != AccountMode.Unauthenticated)
            wrong.Add($"the mode is {access.GetMode()}, so the setup turned sign-in back on");
        if (access.GetLoginSettings(loginId, setup: true) is null)
            wrong.Add("the pending login was saved as the active login");
        Assert.True(wrong.Count == 0, $"a {kind} owner setup that returned after sign-in was turned off: " + string.Join("; ", wrong));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
