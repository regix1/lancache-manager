using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Claims;
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
using Microsoft.AspNetCore.Identity;
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
    // Two tabs of one browser were opened on the sign-in screen. The first signed in; the second, still
    // showing the sign-in screen, signs in with GitHub, with another login service, or by recovering the
    // main administrator's password. The new cookie replaces the first one in both tabs, so no browser
    // can sign the first session out any more, and an account or administrator session never expires:
    // it has to end there, and an open Users page has to be told so it stops listing it as live.
    // externalLogin null is the main administrator recovery; held null is a session that proved the
    // API key and has no account behind it.
    [Theory]
    [InlineData(LoginKind.GitHub, SessionType.Admin)]
    [InlineData(LoginKind.GitHub, SessionType.User)]
    [InlineData(LoginKind.GitHub, null)]
    [InlineData(LoginKind.CustomOidc, SessionType.Admin)]
    [InlineData(LoginKind.CustomOidc, SessionType.User)]
    [InlineData(LoginKind.CustomOidc, null)]
    [InlineData(null, SessionType.Admin)]
    [InlineData(null, SessionType.User)]
    [InlineData(null, null)]
    public async Task ExternalOrRecoverySignIn_FromABrowserSignedIn_EndsThatSessionAndTellsAnOpenUsersPage(
        LoginKind? externalLogin, SessionType? held)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, CacheScanDetectionPhaseTests.RecordingNotifications>();
        var sent = (CacheScanDetectionPhaseTests.RecordingNotifications)(object)notifications;
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true), notifications: notifications);
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, new RecordingContainerGateway()), new TestEpicDaemon(deps, new RecordingContainerGateway()),
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
        services.AddSingleton(notifications);
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
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IdentityAuditService>();
        services.AddSingleton<AccountLockout>();
        services.AddSingleton((SteamDaemonService)daemons[0]);
        services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
        services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
        services.AddSingleton((BattleNetDaemonService)daemons[3]);
        services.AddSingleton((RiotDaemonService)daemons[4]);
        await using var app = services.BuildServiceProvider();
        var sessions = app.GetRequiredService<SessionService>();
        var apiKey = app.GetRequiredService<ApiKeyService>().GetApiKey();

        // The accounts the first tab may have signed in with. Neither is the main administrator: the
        // recovery ends that account's own sessions already, and the login service's owner identity
        // becomes the main administrator below.
        var deputy = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "deputy",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var member = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "member",
            Role = SessionType.User,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(deputy, member);
            await context.SaveChangesAsync();
        }

        // The first tab's sign-in.
        var (signedInToken, signedIn) = held switch
        {
            SessionType.Admin => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), deputy),
            SessionType.User => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member),
            _ => (await sessions.CreateAdminSessionAsync(apiKey, new DefaultHttpContext()))!.Value
        };
        var kind = held is { } heldRole ? $"{heldRole} account" : "API key";
        var signedInSession = await sessions.ValidateSessionAsync(signedInToken);
        Assert.NotNull(signedInSession);

        // Every request of this browser carries the first tab's cookie. Requests that pass the session
        // check carry its session too; the login service's callback is handled before that check runs.
        HttpContext SecondTabRequest(bool sessionChecked)
        {
            var http = new DefaultHttpContext { RequestServices = app };
            http.Request.Scheme = Uri.UriSchemeHttp;
            http.Request.Host = new HostString("localhost");
            http.Request.Headers.Cookie = $"{SessionCookieName}={signedInToken}";
            if (sessionChecked)
                http.Items["Session"] = signedInSession;
            return http;
        }

        // Whether an open Users page, reading the list again after each list event, still sees the
        // first tab's session as live.
        var signedInListedAsLive = new ConcurrentQueue<bool>();
        sent.OnSent = (eventName, _) =>
        {
            if (!UsersPageReloadEvents.Contains(eventName))
                return;
            using var context = contexts.CreateDbContext();
            signedInListedAsLive.Enqueue(context.UserSessions.AsNoTracking()
                .Any(s => s.Id == signedIn.Id && !s.IsRevoked));
        };

        string signIn;
        if (externalLogin is { } login)
        {
            signIn = $"a {login} sign-in";
            var access = app.GetRequiredService<AccessService>();
            var loginId = login == LoginKind.GitHub ? "github" : "customOidc";
            var issuer = login == LoginKind.GitHub ? "https://github.com" : "https://login.example.test";
            const string subject = "owner";
            access.Apply(
                new AccessSetupRequest
                {
                    Mode = AccountMode.Oidc,
                    ApiKey = "checked-by-controller",
                    Login = new LoginSetupRequest
                    {
                        Kind = login,
                        ClientId = $"{login}-client",
                        ClientSecret = "secret",
                        Authority = login == LoginKind.CustomOidc ? issuer : null
                    }
                },
                null);
            var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
            Assert.True(access.PromotePendingLogin(loginId, pending.Revision, issuer, subject));

            var startController = ActivatorUtilities.CreateInstance<AccessController>(app);
            startController.ControllerContext = new ControllerContext { HttpContext = SecondTabRequest(sessionChecked: true) };
            var started = await startController.StartLoginAsync(new LoginStartRequest { LoginId = loginId });
            var startUrl = Assert.IsType<OidcStartResponse>(Assert.IsType<OkObjectResult>(started.Result).Value).Url;
            var token = Uri.UnescapeDataString(startUrl[(startUrl.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);

            var challengeController = ActivatorUtilities.CreateInstance<AccessController>(app);
            challengeController.ControllerContext = new ControllerContext { HttpContext = SecondTabRequest(sessionChecked: true) };
            var challenge = Assert.IsType<ChallengeResult>(challengeController.GetOidcChallenge(token));
            var properties = Assert.IsType<AuthenticationProperties>(challenge.Properties);
            var schemeName = Assert.Single(challenge.AuthenticationSchemes);

            var callback = SecondTabRequest(sessionChecked: false);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], schemeName));
            if (login == LoginKind.GitHub)
            {
                var scheme = new AuthenticationScheme(schemeName, schemeName, typeof(OAuthHandler<OAuthOptions>));
                await ActivatorUtilities.CreateInstance<GitHubEvents>(app).TicketReceived(new TicketReceivedContext(
                    callback, scheme, new OAuthOptions(), new AuthenticationTicket(principal, properties, schemeName)));
            }
            else
            {
                // The login service's token was checked first, which stores the identity it names.
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
        }
        else
        {
            signIn = "a main administrator recovery";
            var owner = new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            owner.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(owner, "Forgotten-Horse-9");
            await using (var context = await contexts.CreateDbContextAsync())
            {
                context.UserAccounts.Add(owner);
                await context.SaveChangesAsync();
            }
            Assert.True(app.GetRequiredService<AccountClaimWindow>().OpenRecovery());

            var request = SecondTabRequest(sessionChecked: true);
            var recovery = ActivatorUtilities.CreateInstance<AccountSetupController>(app);
            recovery.ControllerContext = new ControllerContext { HttpContext = request };
            var recovered = await recovery.RecoverMainAdminPasswordAsync(
                new AccountCredentialsRequest
                {
                    Username = "owner",
                    Password = "Remembered-Otter-4",
                    ApiKey = apiKey
                },
                sessions,
                app.GetRequiredService<AccountLockout>());

            Assert.IsType<OkObjectResult>(recovered.Result);
            Assert.Contains(
                request.Response.Headers.SetCookie,
                header => header?.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal) == true);
        }

        Assert.True(
            (await sessions.GetSessionByIdAsync(signedIn.Id))!.IsRevoked,
            $"the {kind} session this browser held is still live after {signIn} in another tab replaced its cookie");
        Assert.True(
            signedInListedAsLive.Any(live => !live),
            $"the {kind} session replaced by {signIn} sent no list event after it ended, so an open Users page keeps listing it as live");
    }
}
