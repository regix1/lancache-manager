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
    // showing the sign-in screen, clicks "Continue as guest". The guest cookie replaces the signed-in one
    // in both tabs, so no browser can sign that session out any more: it has to end there, and an open
    // Users page has to be told so it stops listing it as live.
    // role null is a session that proved the API key and has no account behind it.
    [Theory]
    [InlineData(SessionType.Admin)]
    [InlineData(SessionType.User)]
    [InlineData(null)]
    public async Task GuestStart_FromABrowserSignedIn_EndsThatSessionAndTellsAnOpenUsersPage(SessionType? role)
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

        // Guests are offered only once the installation has an owner.
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
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(owner, member);
            await context.SaveChangesAsync();
        }

        // The first tab's sign-in.
        var (signedInToken, signedIn) = role switch
        {
            SessionType.Admin => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner),
            SessionType.User => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member),
            _ => (await sessions.CreateAdminSessionAsync(
                app.GetRequiredService<ApiKeyService>().GetApiKey(), new DefaultHttpContext()))!.Value
        };
        var kind = role is { } accountRole ? $"{accountRole} account" : "API key";
        var signedInSession = await sessions.ValidateSessionAsync(signedInToken);
        Assert.NotNull(signedInSession);

        // Whether an open Users page, reading the list again after each list event, still sees the
        // signed-in session as live.
        var signedInListedAsLive = new ConcurrentQueue<bool>();
        sent.OnSent = (eventName, _) =>
        {
            if (!UsersPageReloadEvents.Contains(eventName))
                return;
            using var context = contexts.CreateDbContext();
            signedInListedAsLive.Enqueue(context.UserSessions.AsNoTracking()
                .Any(s => s.Id == signedIn.Id && !s.IsRevoked));
        };

        // The second tab's click carries the signed-in cookie and passes the session check with it.
        var secondTab = new DefaultHttpContext { RequestServices = app };
        secondTab.Request.Scheme = Uri.UriSchemeHttp;
        secondTab.Request.Host = new HostString("localhost");
        secondTab.Request.Headers.Cookie = $"{SessionCookieName}={signedInToken}";
        secondTab.Items["Session"] = signedInSession;
        var controller = ActivatorUtilities.CreateInstance<AuthController>(app);
        controller.ControllerContext = new ControllerContext { HttpContext = secondTab };

        var started = await controller.StartGuestAsync();

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(started.Result).Value);
        Assert.Equal(SessionType.Guest, body.SessionType);
        Assert.Contains(
            secondTab.Response.Headers.SetCookie,
            header => header?.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal) == true);
        Assert.True(
            (await sessions.GetSessionByIdAsync(signedIn.Id))!.IsRevoked,
            $"the {kind} session this browser held is still live after Continue as guest replaced its cookie");
        Assert.True(
            signedInListedAsLive.Any(live => !live),
            $"the {kind} session ended by Continue as guest sent no list event after it ended, so an open Users page keeps listing it as live");
    }
}
