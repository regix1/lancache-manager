using System.Security.Cryptography;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Two tabs of one browser were opened on the sign-in screen. The first clicked "Continue as guest"
    // and started a prefill; the second, still showing the sign-in screen, clicks it too. The new guest
    // cookie replaces the first guest's in both tabs, so no browser can sign the first guest out any
    // more: it has to end there, and the prefill it was running has to stop.
    [Fact]
    public async Task GuestStart_FromABrowserHoldingALiveGuest_EndsThatGuestAndStopsItsPrefill()
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
        services.AddSingleton<ISignalRNotificationService>(deps.Notifications);
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

        // Guests are offered only once the installation has an owner.
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow,
                PasswordHash = "not-used-here"
            });
            await context.SaveChangesAsync();
        }

        // The first tab's guest, with the prefill it started.
        const string firstGuestToken = "first-tab-guest";
        var firstGuest = await SeedGuestAsync(contexts, session => session.SessionTokenHash =
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(firstGuestToken))));
        var prefill = AddGuest(daemons[0], steamGateway, firstGuest);
        await history.CreateSessionAsync(prefill.Id, firstGuest, prefill.ContainerId, prefill.ContainerName, prefill.ExpiresAt, DateTime.UtcNow);
        var sessions = app.GetRequiredService<SessionService>();
        var firstGuestSession = await sessions.ValidateSessionAsync(firstGuestToken);
        Assert.NotNull(firstGuestSession);

        // The second tab's click carries the first guest's cookie and passes the session check with it.
        var secondTab = new DefaultHttpContext { RequestServices = app };
        secondTab.Request.Scheme = Uri.UriSchemeHttp;
        secondTab.Request.Host = new HostString("localhost");
        secondTab.Request.Headers.Cookie = $"{SessionCookieName}={firstGuestToken}";
        secondTab.Items["Session"] = firstGuestSession;
        var controller = ActivatorUtilities.CreateInstance<AuthController>(app);
        controller.ControllerContext = new ControllerContext { HttpContext = secondTab };

        var started = await controller.StartGuestAsync();

        var body = Assert.IsType<LoginResponse>(Assert.IsType<OkObjectResult>(started.Result).Value);
        Assert.Equal(SessionType.Guest, body.SessionType);
        Assert.Contains(
            secondTab.Response.Headers.SetCookie,
            header => header?.StartsWith($"{SessionCookieName}=", StringComparison.Ordinal) == true);
        Assert.True(
            (await sessions.GetSessionByIdAsync(firstGuest))!.IsRevoked,
            "the first tab's guest session is still live after the second tab started a guest and replaced its cookie");
        Assert.False(
            steamGateway.ContainsContainer(prefill.ContainerId),
            "the first tab's guest prefill container is still running after the second tab replaced its guest cookie");
    }

    // A guest start that fails keeps the cookie the browser already had, so the session behind it has to
    // stay live: ending it first would sign the person out of every tab for a click that did nothing.
    [Fact]
    public async Task GuestStart_ThatFails_LeavesTheSignedInSessionValid()
    {
        var (_, contexts) = NewDatabase(new GuestInsertFails());
        var host = await NewGuestStartAppAsync(contexts, authenticationEnabled: true);
        await using var app = host.App;
        var sessions = app.GetRequiredService<SessionService>();
        var (token, _) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), host.Owner);

        await Assert.ThrowsAsync<DbUpdateException>(async () =>
            await (await GuestStartFromAsync(app, token)).StartGuestAsync());

        Assert.NotNull(await sessions.ValidateSessionAsync(token));
    }

    // Only the session whose cookie the guest replaced ends. The same account signed in on another
    // device still holds its own cookie there.
    [Fact]
    public async Task GuestStart_EndsOnlyTheSessionThisBrowserHeld()
    {
        var (_, contexts) = NewDatabase();
        var host = await NewGuestStartAppAsync(contexts, authenticationEnabled: true);
        await using var app = host.App;
        var sessions = app.GetRequiredService<SessionService>();
        var (thisBrowser, _) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), host.Owner);
        var (otherDevice, _) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), host.Owner);

        var started = await (await GuestStartFromAsync(app, thisBrowser)).StartGuestAsync();

        Assert.IsType<OkObjectResult>(started.Result);
        Assert.Null(await sessions.ValidateSessionAsync(thisBrowser));
        Assert.NotNull(await sessions.ValidateSessionAsync(otherDevice));
    }

    // With authentication off every browser without a cookie of its own is handed one shared session,
    // and the clock and refresh-rate preferences hang off its id. A guest started from a browser holding
    // it leaves it live for everyone else. pointerSaved false is the state a failed state.json write
    // leaves: the session is cached and handed out, but the stored id does not name it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GuestStart_WithAuthenticationOff_KeepsTheSharedSession(bool pointerSaved)
    {
        var (_, contexts) = NewDatabase();
        var host = await NewGuestStartAppAsync(contexts, authenticationEnabled: false);
        await using var app = host.App;
        var sessions = app.GetRequiredService<SessionService>();
        var (sharedToken, _) = (await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        if (!pointerSaved)
            host.State.SetSharedAdminSessionId(null);

        var started = await (await GuestStartFromAsync(app, sharedToken)).StartGuestAsync();

        Assert.IsType<OkObjectResult>(started.Result);
        Assert.NotNull(await sessions.ValidateSessionAsync(sharedToken));
    }

    /// <summary>
    /// The services a guest start runs on, over an installation that has finished setup and has an owner.
    /// </summary>
    private async Task<(ServiceProvider App, StateService State, UserAccount Owner)> NewGuestStartAppAsync(
        IDbContextFactory<AppDbContext> contexts, bool authenticationEnabled)
    {
        var history = new PrefillSessionService(contexts, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(contexts, history, Config(PersistenceMode.FullPersistence, true));
        var root = NewTempRoot();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(root, "api_key.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled ? "true" : "false",
                ["Runtime:DatabaseSetupPending"] = "false"
            })
            .Build();
        var state = CreateStateService(root);
        state.SetSetupCompleted(true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(contexts);
        services.AddSingleton<ISignalRNotificationService>(deps.Notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(state);
        services.AddSingleton(new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!));
        services.AddSingleton<SessionService>();
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IdentityAuditService>();
        services.AddSingleton<AccountLockout>();
        services.AddSingleton<SteamDaemonService>(new TestSteamDaemon(deps, new RecordingContainerGateway()));
        services.AddSingleton<EpicPrefillDaemonService>(new TestEpicDaemon(deps, new RecordingContainerGateway()));
        services.AddSingleton<XboxPrefillDaemonService>(new TestXboxDaemon(deps, new RecordingContainerGateway()));
        services.AddSingleton<BattleNetDaemonService>(new TestBattleNetDaemon(deps, new RecordingContainerGateway()));
        services.AddSingleton<RiotDaemonService>(new TestRiotDaemon(deps, new RecordingContainerGateway()));
        var app = services.BuildServiceProvider();

        var owner = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.Add(owner);
            await context.SaveChangesAsync();
        }

        return (app, state, owner);
    }

    /// <summary>
    /// The controller a "Continue as guest" click reaches, carrying this browser's cookie and the session
    /// the request handler resolved from it.
    /// </summary>
    private static async Task<AuthController> GuestStartFromAsync(ServiceProvider app, string token)
    {
        var request = new DefaultHttpContext { RequestServices = app };
        request.Request.Scheme = Uri.UriSchemeHttp;
        request.Request.Host = new HostString("localhost");
        request.Request.Headers.Cookie = $"{SessionCookieName}={token}";
        request.Items["Session"] = await app.GetRequiredService<SessionService>().ValidateSessionAsync(token);
        var controller = ActivatorUtilities.CreateInstance<AuthController>(app);
        controller.ControllerContext = new ControllerContext { HttpContext = request };
        return controller;
    }

    /// <summary>Fails the save that inserts a guest session, the way a lost database connection would.</summary>
    private sealed class GuestInsertFails : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (saving.Context?.ChangeTracker.Entries<UserSession>()
                    .Any(entry => entry.State == EntityState.Added && entry.Entity.SessionType == SessionType.Guest) == true)
            {
                throw new DbUpdateException("The guest session could not be saved");
            }
            return base.SavingChangesAsync(saving, result, cancellationToken);
        }
    }
}
