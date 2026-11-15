using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// A GitHub or OpenID Connect sign-in that comes back from the provider while the database is down
/// cannot make a session. The authentication handler runs the ticket event outside its own catch, so
/// a throw there reaches the browser as the raw JSON error page at the callback address. The event
/// sends the browser back to the app with the sign-in-unavailable code instead, the redirect it
/// already uses when no session could be made.
/// </summary>
// The first sign-in retires the shared session, which is process-wide (SessionService.cs:44).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ExternalSignInDatabaseOutageTests : IDisposable
{
    private const string OidcIssuer = "https://login.example.test";
    private const string Subject = "first-owner";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-signin-outage-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(LoginKind.GitHub, false)]
    [InlineData(LoginKind.GitHub, true)]
    [InlineData(LoginKind.CustomOidc, false)]
    [InlineData(LoginKind.CustomOidc, true)]
    public async Task SignInCallbackDuringADatabaseOutage_RedirectsToTheSignInUnavailableMessageAsync(LoginKind kind, bool browserHoldsACookie)
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var state = StateTestMethods.CreateStateService(_root);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var access = new AccessService(state, configuration, database.Factory);
        var loginId = kind == LoginKind.GitHub ? "github" : "customOidc";
        await using var app = new ServiceCollection().BuildServiceProvider();

        // The login service is set up and its first sign-in, the owner's, completes while the database works.
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
                    Authority = kind == LoginKind.CustomOidc ? OidcIssuer : null
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
        var working = new ExternalSignInService(
            access,
            database.Factory,
            new SessionService(database.Factory, apiKeyService: null!, NullLogger<SessionService>.Instance, state, notifications, configuration),
            notifications);
        var setup = await CallbackAsync(kind, access, working, Transaction(loginId, pending.Revision, setup: true), app, cookie: null);
        Assert.Contains("loginTest=success", setup.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        var active = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: false));

        // The owner signs in again later, and the provider's callback lands while the database is down. The
        // connection retries as the shipped one does (Program.cs:519-525), so the failure is the one it throws.
        var down = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                "Host=127.0.0.1;Port=1;Database=down;Username=u;Password=p;Timeout=2",
                npgsql => npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null))
            .Options);
        var outage = new ExternalSignInService(
            access,
            down,
            new SessionService(down, apiKeyService: null!, NullLogger<SessionService>.Instance, state, notifications, configuration),
            notifications);
        HttpContext? callback = null;
        Exception? thrown = null;
        try
        {
            callback = await CallbackAsync(
                kind,
                access,
                outage,
                Transaction(loginId, active.Revision, setup: false),
                app,
                browserHoldsACookie ? "LancacheManager.Session=a-cookie-the-browser-still-holds" : null);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        Assert.True(
            thrown is null,
            $"the {kind} callback threw {thrown?.GetType().Name} ({thrown?.Message}), so the browser lands on the raw JSON error page instead of the app");
        Assert.Equal(OidcEvents.FailureLocation("unavailable", returnToSetup: false), callback!.Response.Headers.Location.ToString());
    }

    private static AuthenticationProperties Transaction(string loginId, long revision, bool setup)
    {
        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = revision.ToString(CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = setup ? "true" : "false";
        properties.Items["access_owner"] = "false";
        properties.Items["login_id"] = loginId;
        return properties;
    }

    /// <summary>Delivers the provider's verified identity to the ticket event the handler calls.</summary>
    private static async Task<HttpContext> CallbackAsync(
        LoginKind kind,
        AccessService access,
        ExternalSignInService signIns,
        AuthenticationProperties properties,
        IServiceProvider app,
        string? cookie)
    {
        var callback = new DefaultHttpContext { RequestServices = app };
        callback.Request.Scheme = Uri.UriSchemeHttp;
        callback.Request.Host = new HostString("localhost");
        if (cookie is not null)
            callback.Request.Headers.Cookie = cookie;
        var setup = properties.Items["access_setup"] == "true";

        if (kind == LoginKind.GitHub)
        {
            var schemeName = setup ? AccessService.GitHubSetupScheme : AccessService.GetScheme(LoginKind.GitHub, setup: false);
            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OAuthHandler<OAuthOptions>));
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Subject)], scheme.Name));
            await new GitHubEvents(access, signIns, new OptionsCache<OAuthOptions>(), TimeProvider.System, NullLogger<GitHubEvents>.Instance)
                .TicketReceived(new TicketReceivedContext(callback, scheme, new OAuthOptions(), new AuthenticationTicket(principal, properties, scheme.Name)));
        }
        else
        {
            properties.Items["oidc_issuer"] = OidcIssuer;
            properties.Items["oidc_subject"] = Subject;
            var schemeName = AccessService.GetScheme(LoginKind.CustomOidc, setup);
            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OpenIdConnectHandler));
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject)], scheme.Name));
            await new OidcEvents(access, signIns, new OptionsCache<OpenIdConnectOptions>(), TimeProvider.System, NullLogger<OidcEvents>.Instance)
                .TicketReceived(new TicketReceivedContext(callback, scheme, new OpenIdConnectOptions(), new AuthenticationTicket(principal, properties, scheme.Name)));
        }

        return callback;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
