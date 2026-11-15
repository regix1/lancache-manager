using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
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
    // The events after which an open Users page reads its whole session list again
    // (RELOAD_EVENTS in Web/src/components/features/user/useUserSessions.ts).
    private static readonly string[] UsersPageReloadEvents =
    [
        SignalREvents.UserSessionRevoked, SignalREvents.UserSessionsCleared, SignalREvents.UserSessionCreated,
        SignalREvents.GuestDurationUpdated, SignalREvents.UserPreferencesReset, SignalREvents.GuestPrefillConfigChanged,
        SignalREvents.EpicGuestPrefillConfigChanged, SignalREvents.BattleNetGuestPrefillConfigChanged,
        SignalREvents.RiotGuestPrefillConfigChanged, SignalREvents.XboxGuestPrefillConfigChanged,
        SignalREvents.GuestRefreshRateUpdated, SignalREvents.AccountsChanged
    ];

    // A guest browser that signs in with GitHub, with another login service, or by recovering the main
    // administrator's password ends its guest session. An administrator watching the Users page must be
    // told, or the page keeps listing that guest as a live session with its live actions.
    // externalLogin null is the main administrator recovery.
    [Theory]
    [InlineData(LoginKind.GitHub)]
    [InlineData(LoginKind.CustomOidc)]
    [InlineData(null)]
    public async Task GuestEndedBySignIn_TellsAnOpenUsersPage(LoginKind? externalLogin)
    {
        var (_, contexts) = NewDatabase();
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, CacheScanDetectionPhaseTests.RecordingNotifications>();
        var sent = (CacheScanDetectionPhaseTests.RecordingNotifications)(object)notifications;
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true), notifications: notifications);
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

        const string guestToken = "guest-before-sign-in";
        var guest = await SeedGuestAsync(contexts, session => session.SessionTokenHash =
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(guestToken))));
        var prefill = AddGuest(daemons[0], steamGateway, guest);
        await history.CreateSessionAsync(prefill.Id, guest, prefill.ContainerId, prefill.ContainerName, prefill.ExpiresAt, DateTime.UtcNow);
        var sessions = app.GetRequiredService<SessionService>();
        var guestSession = await sessions.ValidateSessionAsync(guestToken);
        Assert.NotNull(guestSession);

        // Every request of this browser carries the guest cookie. Requests that pass the session check
        // carry the guest session too; the login service's callback is handled before that check runs.
        HttpContext GuestBrowserRequest(bool sessionChecked)
        {
            var http = new DefaultHttpContext { RequestServices = app };
            http.Request.Scheme = Uri.UriSchemeHttp;
            http.Request.Host = new HostString("localhost");
            http.Request.Headers.Cookie = $"{SessionCookieName}={guestToken}";
            if (sessionChecked)
                http.Items["Session"] = guestSession;
            return http;
        }

        string signIn;
        if (externalLogin is { } kind)
        {
            signIn = $"a {kind} sign-in";
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

            var startController = ActivatorUtilities.CreateInstance<AccessController>(app);
            startController.ControllerContext = new ControllerContext { HttpContext = GuestBrowserRequest(sessionChecked: true) };
            var started = await startController.StartLoginAsync(new LoginStartRequest { LoginId = loginId });
            var startUrl = Assert.IsType<OidcStartResponse>(Assert.IsType<OkObjectResult>(started.Result).Value).Url;
            var token = Uri.UnescapeDataString(startUrl[(startUrl.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);

            var challengeController = ActivatorUtilities.CreateInstance<AccessController>(app);
            challengeController.ControllerContext = new ControllerContext { HttpContext = GuestBrowserRequest(sessionChecked: true) };
            var challenge = Assert.IsType<ChallengeResult>(challengeController.GetOidcChallenge(token));
            var properties = Assert.IsType<AuthenticationProperties>(challenge.Properties);
            var schemeName = Assert.Single(challenge.AuthenticationSchemes);

            var callback = GuestBrowserRequest(sessionChecked: false);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], schemeName));
            if (kind == LoginKind.GitHub)
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
        }
        else
        {
            signIn = "a main administrator recovery";
            var account = new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            };
            account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, "Forgotten-Horse-9");
            await using (var context = await contexts.CreateDbContextAsync())
            {
                context.UserAccounts.Add(account);
                await context.SaveChangesAsync();
            }
            Assert.True(app.GetRequiredService<AccountClaimWindow>().OpenRecovery());

            var recovery = ActivatorUtilities.CreateInstance<AccountSetupController>(app);
            recovery.ControllerContext = new ControllerContext { HttpContext = GuestBrowserRequest(sessionChecked: true) };
            var recovered = await recovery.RecoverMainAdminPasswordAsync(
                new AccountCredentialsRequest
                {
                    Username = "owner",
                    Password = "Remembered-Otter-4",
                    ApiKey = app.GetRequiredService<ApiKeyService>().GetApiKey()
                },
                sessions,
                app.GetRequiredService<AccountLockout>());

            Assert.IsType<OkObjectResult>(recovered.Result);
        }

        Assert.True(
            (await sessions.GetSessionByIdAsync(guest))!.IsRevoked,
            $"the guest session this browser held is still live after {signIn}");
        // The guest's own event, not any reload event: the sign-in also announces the new session, and
        // that announcement can go out before the guest ends.
        Assert.True(
            sent.Count(SignalREvents.UserSessionRevoked,
                announced => DefaultGuestPreferencesTests.ReadPayload<string>(announced, "sessionId") == guest.ToString()) > 0,
            $"the guest session ended by {signIn} sent no session event, so an open Users page keeps listing it as live");
    }
}
