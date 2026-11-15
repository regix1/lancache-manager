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
    // Every login service, every session the browser held when the sign-in started (an account, a
    // guest, or none), every session another tab of the browser makes while the person is on the
    // login service's page, and both orders of that other sign-in and the start of this one.
    public static TheoryData<LoginKind, SessionType?, SessionType, bool> SignInsMadeWhileOnTheLoginPage()
    {
        var cases = new TheoryData<LoginKind, SessionType?, SessionType, bool>();
        foreach (var login in new[] { LoginKind.GitHub, LoginKind.CustomOidc })
            foreach (var heldAtStart in new SessionType?[] { SessionType.Admin, SessionType.Guest, null })
                foreach (var madeMeanwhile in new[] { SessionType.Admin, SessionType.User, SessionType.Guest })
                    foreach (var whileOnLoginPage in new[] { true, false })
                        cases.Add(login, heldAtStart, madeMeanwhile, whileOnLoginPage);
        return cases;
    }

    // One tab starts a sign-in with a login service. Before its callback arrives, another tab of the same
    // browser signs in (with a password, or as a guest), which ends the session the browser held and sets a
    // new cookie. The callback carries that newer cookie and replaces it with the login service's session,
    // so no browser holds the newer session any more: the callback has to end it, and an open Users page
    // has to be told so it stops listing it as live. whileOnLoginPage false is the other tab signing in
    // before this sign-in started. heldAtStart null is a browser with no session at the start.
    [Theory]
    [MemberData(nameof(SignInsMadeWhileOnTheLoginPage))]
    public async Task ExternalSignIn_AfterAnotherTabSignedIn_EndsTheSessionItsCallbackCarries(
        LoginKind login, SessionType? heldAtStart, SessionType madeMeanwhile, bool whileOnLoginPage)
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

        // None of these is the main administrator: the login service's owner identity becomes that
        // account below, and it is the account this sign-in ends on.
        var deputy = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "deputy",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var colleague = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "colleague",
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
            context.UserAccounts.AddRange(deputy, colleague, member);
            await context.SaveChangesAsync();
        }

        // An account session for the account given, or a guest session when there is none.
        async Task<string> NewSessionAsync(UserAccount? account) => account is null
            ? Assert.NotNull(await sessions.CreateGuestSessionAsync(new DefaultHttpContext())).RawToken
            : (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account)).RawToken;

        // The cookie this browser holds, and the session it names once the session check has run.
        string? cookie = null;
        UserSession? cookieSession = null;
        if (heldAtStart is { } startKind)
        {
            cookie = await NewSessionAsync(startKind == SessionType.Guest ? null : deputy);
            cookieSession = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(cookie));
            Assert.Equal(startKind, cookieSession.SessionType);
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
            if (cookieSession is { SessionType: SessionType.Guest })
                await sessions.EndGuestSessionAsync(cookieSession.Id, app);
            else
                await sessions.EndReplacedSessionAsync(cookieSession, app);
            cookie = token;
            cookieSession = meanwhile;
        }

        HttpContext BrowserRequest(bool sessionChecked)
        {
            var http = new DefaultHttpContext { RequestServices = app };
            http.Request.Scheme = Uri.UriSchemeHttp;
            http.Request.Host = new HostString("localhost");
            if (cookie is not null)
                http.Request.Headers.Cookie = $"{SessionCookieName}={cookie}";
            if (sessionChecked && cookieSession is not null)
                http.Items["Session"] = cookieSession;
            return http;
        }

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

        if (!whileOnLoginPage)
            await OtherTabSignsInAsync();

        var startController = ActivatorUtilities.CreateInstance<AccessController>(app);
        startController.ControllerContext = new ControllerContext { HttpContext = BrowserRequest(sessionChecked: true) };
        var started = await startController.StartLoginAsync(new LoginStartRequest { LoginId = loginId });
        var startUrl = Assert.IsType<OidcStartResponse>(Assert.IsType<OkObjectResult>(started.Result).Value).Url;
        var challengeToken = Uri.UnescapeDataString(startUrl[(startUrl.IndexOf("token=", StringComparison.Ordinal) + "token=".Length)..]);

        var challengeController = ActivatorUtilities.CreateInstance<AccessController>(app);
        challengeController.ControllerContext = new ControllerContext { HttpContext = BrowserRequest(sessionChecked: true) };
        var challenge = Assert.IsType<ChallengeResult>(challengeController.GetOidcChallenge(challengeToken));
        var properties = Assert.IsType<AuthenticationProperties>(challenge.Properties);
        var schemeName = Assert.Single(challenge.AuthenticationSchemes);

        // The person is on the login service's page now.
        if (whileOnLoginPage)
            await OtherTabSignsInAsync();
        var replaced = Assert.IsType<UserSession>(meanwhile);

        // Whether an open Users page, reading the list again after each list event, still sees the session
        // the callback replaces as live.
        var replacedListedAsLive = new ConcurrentQueue<bool>();
        sent.OnSent = (eventName, _) =>
        {
            if (!UsersPageReloadEvents.Contains(eventName))
                return;
            using var context = contexts.CreateDbContext();
            replacedListedAsLive.Enqueue(context.UserSessions.AsNoTracking()
                .Any(s => s.Id == replaced.Id && !s.IsRevoked));
        };

        // The login service sends the browser back with the cookie it holds now; the callback is handled
        // before the session check runs.
        var callback = BrowserRequest(sessionChecked: false);
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

        var when = whileOnLoginPage ? "while the person was on the login service's page" : "before this sign-in started";
        var start = heldAtStart switch
        {
            SessionType.Admin => "an Admin account session",
            SessionType.Guest => "a guest session",
            _ => "no session"
        };
        var made = madeMeanwhile == SessionType.Guest ? "guest" : $"{madeMeanwhile} account";
        Assert.True(
            (await sessions.GetSessionByIdAsync(replaced.Id))!.IsRevoked,
            $"the {made} session another tab made {when} (the browser held {start} at the start) is still live after the {login} callback replaced the cookie that named it, so no browser holds it and nothing ends it");
        Assert.True(
            replacedListedAsLive.Any(live => !live),
            $"the {made} session the {login} callback replaced sent no list event after it ended, so an open Users page keeps listing it as live");
    }
}
