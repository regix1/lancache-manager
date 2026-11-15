using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
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
    // Recovering the main administrator's password ends every session the old password opened and signs
    // this browser in with a new one. Another administrator watching the Users page reloads the session
    // list on each list event; one of those reloads has to come after both changes, or the page keeps the
    // old session as current and never shows the new one. The browser either held a guest, whose end sends
    // a list event before the password changes, or held nothing.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MainAdminRecovery_ShowsTheEndedAndTheNewSessionOnAnOpenUsersPage(bool fromGuestBrowser)
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
        services.AddSingleton<AccountClaimWindow>();
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

        // The session the old password opened on another device, listed on the Users page as current.
        var (_, oldSession) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);

        var browser = new DefaultHttpContext { RequestServices = app };
        browser.Request.Scheme = Uri.UriSchemeHttp;
        browser.Request.Host = new HostString("localhost");
        if (fromGuestBrowser)
        {
            const string guestToken = "guest-before-recovery";
            await SeedGuestAsync(contexts, session => session.SessionTokenHash =
                Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(guestToken))));
            browser.Request.Headers.Cookie = $"{SessionCookieName}={guestToken}";
            browser.Items["Session"] = await sessions.ValidateSessionAsync(guestToken);
        }

        // What an open Users page would read back after each list event: the main administrator's live sessions.
        var listsLoaded = new ConcurrentQueue<Guid[]>();
        sent.OnSent = (eventName, _) =>
        {
            if (!UsersPageReloadEvents.Contains(eventName))
                return;
            using var context = contexts.CreateDbContext();
            listsLoaded.Enqueue(context.UserSessions.AsNoTracking()
                .Where(s => s.AccountId == account.Id && !s.IsRevoked)
                .Select(s => s.Id)
                .ToArray());
        };

        Assert.True(app.GetRequiredService<AccountClaimWindow>().OpenRecovery());
        var recovery = ActivatorUtilities.CreateInstance<AccountSetupController>(app);
        recovery.ControllerContext = new ControllerContext { HttpContext = browser };
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
        Assert.True(
            (await sessions.GetSessionByIdAsync(oldSession.Id))!.IsRevoked,
            "the session the old password opened is still live after the recovery");
        Guid newSession;
        await using (var context = await contexts.CreateDbContextAsync())
        {
            newSession = Assert.Single(await context.UserSessions.AsNoTracking()
                .Where(s => s.AccountId == account.Id && !s.IsRevoked)
                .Select(s => s.Id)
                .ToListAsync());
        }

        var from = fromGuestBrowser ? "from a browser holding a guest" : "from a browser with no session";
        Assert.True(
            listsLoaded.Any(list => !list.Contains(oldSession.Id) && list.Contains(newSession)),
            $"after the main administrator's password was recovered {from}, no list event came after both the old " +
            $"session ended and the new one was created ({listsLoaded.Count} list events sent), so an open Users page " +
            "keeps the old session as current and does not show the new one");
    }
}
