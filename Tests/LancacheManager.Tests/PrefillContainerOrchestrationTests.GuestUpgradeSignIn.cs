using System.Security.Cryptography;
using System.Text;
using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // Signing in as an administrator from a browser that holds a guest session ends that guest session.
    // A guest who loses its session loses prefill access, so the prefill it was running stops, the same
    // way it stops when the guest signs out or an administrator revokes the guest.
    [Fact]
    public async Task AdminSignIn_FromAGuestBrowser_StopsTheGuestsRunningPrefill()
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
        var setup = CreateGuestControllers(contexts, deps, daemons);
        using var services = setup.Services;

        const string guestToken = "guest-before-admin-sign-in";
        var guest = await SeedGuestAsync(contexts, session => session.SessionTokenHash =
            Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(guestToken))));
        var prefill = AddGuest(daemons[0], steamGateway, guest);
        await history.CreateSessionAsync(prefill.Id, guest, prefill.ContainerId, prefill.ContainerName, prefill.ExpiresAt, DateTime.UtcNow);

        const string password = "Upgrade-Heron-5";
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow
        };
        account.PasswordHash = new PasswordHasher<UserAccount>().HashPassword(account, password);
        await using (var context = await contexts.CreateDbContextAsync())
        {
            context.UserAccounts.Add(account);
            await context.SaveChangesAsync();
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(NewTempRoot(), "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        var apiKeys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(contexts, apiKeys, NullLogger<SessionService>.Instance, stateService: null!, deps.Notifications, configuration);
        var request = new DefaultHttpContext { RequestServices = services };
        request.Request.Headers.Cookie = $"LancacheManager.Session={guestToken}";
        var controller = new AuthController(
            sessions,
            NullLogger<AuthController>.Instance,
            contexts,
            stateService: null!,
            deps.Notifications,
            apiKeys,
            new PasswordHasher<UserAccount>(),
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            new IdentityAuditService(contexts, NullLogger<IdentityAuditService>.Instance))
        {
            ControllerContext = new ControllerContext { HttpContext = request }
        };

        var signedIn = await controller.LoginAsync(new LoginRequest
        {
            Username = "owner",
            Password = password,
            ApiKey = apiKeys.GetApiKey()
        });

        Assert.IsType<OkObjectResult>(signedIn.Result);
        Assert.True((await sessions.GetSessionByIdAsync(guest))!.IsRevoked, "the guest session the browser held is ended");
        Assert.False(
            steamGateway.ContainsContainer(prefill.ContainerId),
            "the revoked guest's prefill container is still running after the administrator signed in");
    }
}
