using LancacheManager.Controllers;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
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
    // Rotating the API key, deleting every account, and a start that finds no account each end every
    // session at once. A guest and an administrator each have a prefill running; once their sessions are
    // gone no page can reach those prefills, so they stop with the sessions, as they do when one session
    // is revoked.
    [Theory]
    [InlineData("API key rotation")]
    [InlineData("deleting every account")]
    [InlineData("a start that finds no account")]
    public async Task EndingEverySession_StopsThePrefillEachSessionStarted(string end)
    {
        await using var database = await TestDatabase.CreateAsync();
        var history = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
        var deps = MakeDeps(database.Factory, history, Config(PersistenceMode.FullPersistence, true));
        var steamGateway = new RecordingContainerGateway();
        var daemons = new PrefillDaemonServiceBase[]
        {
            new TestSteamDaemon(deps, steamGateway), new TestEpicDaemon(deps, new RecordingContainerGateway()),
            new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
            new TestRiotDaemon(deps, new RecordingContainerGateway())
        };
        var steam = daemons[0];

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
        services.AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory);
        services.AddSingleton(deps.Notifications);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(state);
        services.AddSingleton(new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!));
        services.AddSingleton<SessionService>();
        services.AddSingleton<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IdentityAuditService>();
        services.AddSingleton<AccountLockout>();
        services.AddSingleton(new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance));
        // Deleting every account runs in AccessService and clears each account's game logins first.
        using var logins = new IntegrationFixture();
        services.AddSingleton(new AccessService(state, configuration, database.Factory));
        services.AddSingleton<SteamAuthStorageService>(logins.Storage);
        services.AddSingleton(logins.Epic);
        services.AddSingleton(logins.Xbox);
        services.AddSingleton((SteamDaemonService)daemons[0]);
        services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
        services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
        services.AddSingleton((BattleNetDaemonService)daemons[3]);
        services.AddSingleton((RiotDaemonService)daemons[4]);
        await using var app = services.BuildServiceProvider();
        var sessions = app.GetRequiredService<SessionService>();
        var apiKeys = app.GetRequiredService<ApiKeyService>();

        // The owner makes the request on the two paths an administrator starts; a start that clears the
        // sessions runs only while no account exists.
        UserSession? owner = null;
        if (end != "a start that finds no account")
        {
            var account = new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow,
                PasswordHash = "not-used-here"
            };
            await using (var context = await database.Factory.CreateDbContextAsync())
            {
                context.UserAccounts.Add(account);
                await context.SaveChangesAsync();
            }
            owner = (await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account)).Session;
        }

        var admin = (await sessions.CreateAdminSessionAsync(apiKeys.GetApiKey(), new DefaultHttpContext()))!.Value;
        var guest = (await sessions.CreateGuestSessionAsync(new DefaultHttpContext()))!.Value;
        var running = new[]
        {
            (Who: "guest", Token: guest.RawToken, Owner: guest.Session.Id, Prefill: AddGuest(steam, steamGateway, guest.Session.Id)),
            (Who: "administrator", Token: admin.RawToken, Owner: admin.Session.Id,
                Prefill: AddGuest(steam, steamGateway, admin.Session.Id, temporary: false))
        };

        var request = new DefaultHttpContext { RequestServices = app };
        request.Items["Session"] = owner;
        switch (end)
        {
            case "API key rotation":
                var encryption = new SecureStateEncryptionService(
                    DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
                    apiKeys,
                    NullLogger<SecureStateEncryptionService>.Instance);
                var storage = PathResolverProxy.New(root);
                var rotation = new ApiKeysController(
                    database.Factory,
                    apiKeys,
                    steamKit2Service: null!,
                    xboxCatalogMappingService: null!,
                    epicMappingService: null!,
                    new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, storage, encryption),
                    new XboxAuthStorageService(NullLogger<XboxAuthStorageService>.Instance, storage, encryption),
                    new EpicAuthStorageService(NullLogger<EpicAuthStorageService>.Instance, storage, encryption),
                    state,
                    sessions,
                    app.GetRequiredService<IdentityAuditService>(),
                    configuration,
                    new AuthenticationHelper(apiKeys, NullLogger<AuthenticationHelper>.Instance),
                    NullLogger<ApiKeysController>.Instance)
                {
                    ControllerContext = new ControllerContext { HttpContext = request }
                };
                Assert.IsType<OkObjectResult>(await rotation.RegenerateApiKeyAsync());
                break;
            case "deleting every account":
                var accounts = ActivatorUtilities.CreateInstance<AccountsController>(app);
                accounts.ControllerContext = new ControllerContext { HttpContext = request };
                Assert.IsType<OkObjectResult>((await accounts.WipeAccountsAsync()).Result);
                break;
            default:
                await new FirstAdminSessionResetService(
                        database.Factory,
                        app.GetRequiredService<IServiceScopeFactory>(),
                        NullLogger<FirstAdminSessionResetService>.Instance)
                    .StartAsync(CancellationToken.None);
                break;
        }

        foreach (var (who, token, ownerId, prefill) in running)
        {
            Assert.Null(await sessions.ValidateSessionAsync(token));
            Assert.False(
                steamGateway.ContainsContainer(prefill.ContainerId!),
                $"the {who} session's prefill container is still running after {end} ended every session, and no page can reach it");
            Assert.Empty(steam.GetUserSessions(ownerId));
        }
    }
}
