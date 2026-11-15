using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A guest who signs in with GitHub or another login service from the guest-only Authenticate tab
    // loses its guest session, and with it the prefill that session was running, the same way a guest
    // who signs in with a password does. Otherwise the Users page lists a live guest no browser holds
    // and its prefill keeps running until the guest session expires.
    [Theory]
    [InlineData(LoginKind.GitHub)]
    [InlineData(LoginKind.CustomOidc)]
    public async Task ExternalSignIn_FromAGuestBrowser_EndsTheGuestAndStopsItsPrefill(LoginKind kind)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var steamGateway = new RecordingContainerGateway();
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, steamGateway), new TestEpicDaemon(deps, new RecordingContainerGateway()),
            new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
            new TestRiotDaemon(deps, new RecordingContainerGateway())
        };

        var root = NewTempRoot();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true",
                ["Runtime:DatabaseSetupPending"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(contexts);
        services.AddSingleton<ISignalRNotificationService>(deps.Notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(CreateStateService(root));
        services.AddSingleton(new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!));
        services.AddSingleton<AccessService>();
        services.AddSingleton<OidcChallengeStore>();
        services.AddSingleton<AccountClaimWindow>();
        services.AddSingleton<IOptionsMonitorCache<OpenIdConnectOptions>, OptionsCache<OpenIdConnectOptions>>();
        services.AddSingleton<IOptionsMonitorCache<OAuthOptions>, OptionsCache<OAuthOptions>>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<ExternalSignInService>();
        services.AddSingleton((SteamDaemonService)daemons[0]);
        services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
        services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
        services.AddSingleton((BattleNetDaemonService)daemons[3]);
        services.AddSingleton((RiotDaemonService)daemons[4]);
        await using var app = services.BuildServiceProvider();

        // The login service is set up and its owner verified, so the install signs in through it.
        var access = app.GetRequiredService<AccessService>();
        var loginId = kind == LoginKind.GitHub ? "github" : "customOidc";
        var issuer = kind == LoginKind.GitHub ? "https://github.com" : "https://login.example.test";
        const string subject = "owner";
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
        Assert.True(access.PromotePendingLogin(loginId, pending.Revision, issuer, subject));

        const string guestToken = "guest-before-external-sign-in";
        var guest = await SeedGuestAsync(contexts, session => session.SessionTokenHash =
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(guestToken))));
        var prefill = AddGuest(daemons[0], steamGateway, guest);
        await history.CreateSessionAsync(prefill.Id, guest, prefill.ContainerId, prefill.ContainerName, prefill.ExpiresAt, DateTime.UtcNow);
        var sessions = app.GetRequiredService<SessionService>();
        var guestSession = await sessions.ValidateSessionAsync(guestToken);
        Assert.NotNull(guestSession);

        // Every request of this browser carries the guest cookie. The start and the challenge are
        // same-site requests that pass the session check, which records the guest session on them; the
        // provider's callback is handled before that check runs.
        HttpContext BrowserRequest(bool sessionChecked)
        {
            var http = new DefaultHttpContext { RequestServices = app };
            http.Request.Scheme = Uri.UriSchemeHttp;
            http.Request.Host = new HostString("localhost");
            http.Request.Headers.Cookie = $"{SessionCookieName}={guestToken}";
            if (sessionChecked)
                http.Items["Session"] = guestSession;
            return http;
        }

        var startController = ActivatorUtilities.CreateInstance<AccessController>(app);
        startController.ControllerContext = new ControllerContext { HttpContext = BrowserRequest(sessionChecked: true) };
        var started = await startController.StartLoginAsync(new LoginStartRequest { LoginId = loginId });
        var startUrl = Assert.IsType<OidcStartResponse>(Assert.IsType<OkObjectResult>(started.Result).Value).Url;
        var token = Uri.UnescapeDataString(startUrl[(startUrl.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);

        var challengeController = ActivatorUtilities.CreateInstance<AccessController>(app);
        challengeController.ControllerContext = new ControllerContext { HttpContext = BrowserRequest(sessionChecked: true) };
        var challenge = Assert.IsType<ChallengeResult>(challengeController.GetOidcChallenge(token));
        var properties = Assert.IsType<AuthenticationProperties>(challenge.Properties);
        var schemeName = Assert.Single(challenge.AuthenticationSchemes);

        var callback = BrowserRequest(sessionChecked: false);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], schemeName));
        if (kind == LoginKind.GitHub)
        {
            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OAuthHandler<OAuthOptions>));
            await ActivatorUtilities.CreateInstance<GitHubEvents>(app).TicketReceived(new TicketReceivedContext(
                callback, scheme, new OAuthOptions(), new AuthenticationTicket(principal, properties, schemeName)));
        }
        else
        {
            // The provider's token was checked first, which stores the identity it names (OidcEvents.TokenValidated).
            properties.Items["oidc_issuer"] = issuer;
            properties.Items["oidc_subject"] = subject;
            var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OpenIdConnectHandler));
            await ActivatorUtilities.CreateInstance<OidcEvents>(app).TicketReceived(new TicketReceivedContext(
                callback, scheme, new OpenIdConnectOptions(), new AuthenticationTicket(principal, properties, schemeName)));
        }

        Assert.Equal(StatusCodes.Status302Found, callback.Response.StatusCode);
        Assert.Equal("/", callback.Response.Headers.Location.ToString());
        Assert.Contains(
            callback.Response.Headers.SetCookie,
            header => header?.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal) == true);
        Assert.True(
            (await sessions.GetSessionByIdAsync(guest))!.IsRevoked,
            $"the guest session this browser held is still live after it signed in with {kind}");
        Assert.False(
            steamGateway.ContainsContainer(prefill.ContainerId),
            $"the guest's prefill container is still running after the browser signed in with {kind}");
    }
}
