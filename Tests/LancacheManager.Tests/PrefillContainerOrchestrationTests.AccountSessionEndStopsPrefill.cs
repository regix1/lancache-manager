using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
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
    private const string ColleaguePassword = "Correct-Horse-9";

    // An administrator or user session with a temporary prefill running, and the app around it.
    private sealed record AccountWithPrefill(
        ServiceProvider App,
        SessionService Sessions,
        RecordingContainerGateway SteamGateway,
        PrefillDaemonServiceBase Steam,
        string Token,
        UserSession Session,
        DaemonSession Prefill,
        string ApiKey);

    private async Task<AccountWithPrefill> StartAccountPrefillAsync(SessionType role)
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
        var state = CreateStateService(root);
        state.SetSetupCompleted(true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(contexts);
        services.AddSingleton(deps.Notifications);
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
        var app = services.BuildServiceProvider();
        var sessions = app.GetRequiredService<SessionService>();

        var held = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = role == SessionType.Admin ? "owner" : "member",
            Role = role,
            IsMainAdmin = role == SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        // The account the other window signs in with.
        var colleague = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "colleague",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow
        };
        colleague.PasswordHash = app.GetRequiredService<IPasswordHasher<UserAccount>>().HashPassword(colleague, ColleaguePassword);
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.AddRange(held, colleague);
            await context.SaveChangesAsync();
        }

        var (token, _) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), held);
        var session = Assert.IsType<UserSession>(await sessions.ValidateSessionAsync(token));

        // The prefill page of this session started a temporary prefill session, which runs its own container.
        var prefill = AddGuest(daemons[0], steamGateway, session.Id);
        await history.CreateSessionAsync(prefill.Id, session.Id, prefill.ContainerId, prefill.ContainerName, prefill.ExpiresAt, DateTime.UtcNow);

        return new AccountWithPrefill(app, sessions, steamGateway, daemons[0], token, session, prefill,
            app.GetRequiredService<ApiKeyService>().GetApiKey());
    }

    // A request from a window of the browser that holds the account session's cookie.
    private static AuthController WindowHoldingTheCookie(AccountWithPrefill scene, bool sessionChecked)
    {
        var window = new DefaultHttpContext { RequestServices = scene.App };
        window.Request.Scheme = Uri.UriSchemeHttp;
        window.Request.Host = new HostString("localhost");
        window.Request.Headers.Cookie = $"{SessionCookieName}={scene.Token}";
        if (sessionChecked)
            window.Items["Session"] = scene.Session;
        var controller = ActivatorUtilities.CreateInstance<AuthController>(scene.App);
        controller.ControllerContext = new ControllerContext { HttpContext = window };
        return controller;
    }

    // Window B runs a temporary prefill session on the prefill page. In window A the same browser signs
    // in again (with a password, or with "Continue as guest"); the new cookie replaces B's, and B's
    // session is revoked. The page in B then reconnects under the new session, which lists nothing, so
    // it says the session ended; the prefill container must not keep running out of everyone's reach.
    [Theory]
    [InlineData("password", SessionType.Admin)]
    [InlineData("password", SessionType.User)]
    [InlineData("guest", SessionType.Admin)]
    [InlineData("guest", SessionType.User)]
    public async Task SignInOverAnAccountSession_StopsThePrefillThatSessionStarted(string signIn, SessionType role)
    {
        var scene = await StartAccountPrefillAsync(role);
        await using var app = scene.App;
        var controller = WindowHoldingTheCookie(scene, sessionChecked: true);

        var answer = signIn == "password"
            ? await controller.LoginAsync(new LoginRequest { Username = "colleague", Password = ColleaguePassword, ApiKey = scene.ApiKey })
            : await controller.StartGuestAsync();

        Assert.IsType<OkObjectResult>(answer.Result);
        Assert.True(
            (await scene.Sessions.GetSessionByIdAsync(scene.Session.Id))!.IsRevoked,
            $"the {role} session the {signIn} sign-in replaced is still live");
        Assert.False(
            scene.SteamGateway.ContainsContainer(scene.Prefill.ContainerId!),
            $"the {role} session's temporary prefill container is still running after a {signIn} sign-in in another window revoked that session, and its page can no longer reach it");
        Assert.Empty(scene.Steam.GetUserSessions(scene.Session.Id));
    }
}
