using System.Collections.Concurrent;
using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Two tabs of one browser were opened on the sign-in screen. The first signed in; the second, still
    // showing the sign-in screen, signs in with a username and password. The new cookie replaces the
    // first one in both tabs, so no browser can sign the first session out any more, and an account or
    // administrator session never expires: it has to end there, and an open Users page has to be told
    // so it stops listing it as live.
    // held null is a session that proved the API key and has no account behind it.
    [Theory]
    [InlineData(SessionType.Admin)]
    [InlineData(SessionType.User)]
    [InlineData(null)]
    public async Task PasswordSignIn_FromABrowserSignedIn_EndsThatSessionAndTellsAnOpenUsersPage(SessionType? held)
    {
        const string colleaguePassword = "Correct-Horse-9";
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
        var state = CreateStateService(root);
        state.SetSetupCompleted(true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(contexts);
        services.AddSingleton(notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(state);
        services.AddSingleton(new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!));
        services.AddSingleton<SessionService>();
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

        var owner = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
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
        // The account the second tab signs in with.
        var colleague = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "colleague",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        colleague.PasswordHash = app.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(colleague, colleaguePassword);
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(owner, member, colleague);
            await context.SaveChangesAsync();
        }

        // The first tab's sign-in.
        var (signedInToken, signedIn) = held switch
        {
            SessionType.Admin => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner),
            SessionType.User => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member),
            _ => (await sessions.CreateAdminSessionAsync(apiKey, new DefaultHttpContext()))!.Value
        };
        var kind = held is { } heldRole ? $"{heldRole} account" : "API key";
        var signedInSession = await sessions.ValidateSessionAsync(signedInToken);
        Assert.NotNull(signedInSession);

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

        // The second tab's sign-in carries the first tab's cookie, and the session check passes with it.
        var secondTab = new DefaultHttpContext { RequestServices = app };
        secondTab.Request.Scheme = Uri.UriSchemeHttp;
        secondTab.Request.Host = new HostString("localhost");
        secondTab.Request.Headers.Cookie = $"{SessionCookieName}={signedInToken}";
        secondTab.Items["Session"] = signedInSession;
        var controller = ActivatorUtilities.CreateInstance<AuthController>(app);
        controller.ControllerContext = new ControllerContext { HttpContext = secondTab };

        var result = await controller.LoginAsync(
            new LoginRequest { Username = colleague.Username, Password = colleaguePassword, ApiKey = apiKey });

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(SessionType.Admin, body.SessionType);
        Assert.Contains(
            secondTab.Response.Headers.SetCookie,
            header => header?.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal) == true);
        Assert.True(
            (await sessions.GetSessionByIdAsync(signedIn.Id))!.IsRevoked,
            $"the {kind} session this browser held is still live after a password sign-in in another tab replaced its cookie");
        Assert.True(
            signedInListedAsLive.Any(live => !live),
            $"the {kind} session replaced by a password sign-in sent no list event after it ended, so an open Users page keeps listing it as live");
    }
}
