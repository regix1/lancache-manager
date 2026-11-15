using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // The Data page reset of User sessions deletes every session row, so everyone is signed out. A guest has a
    // temporary prefill running and an administrator a prefill that is not temporary; once their sessions are
    // gone no page can reach those prefills, so they stop with the sessions, as they do when the API key
    // rotation clears them.
    [Fact]
    public async Task UserSessionsReset_StopsThePrefillEachClearedSessionStarted()
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

        var guestId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            foreach (var (id, type) in new[] { (guestId, SessionType.Guest), (adminId, SessionType.Admin) })
            {
                seed.UserSessions.Add(new UserSession
                {
                    Id = id,
                    SessionTokenHash = "hash-" + id.ToString("N"),
                    SessionType = type,
                    CreatedAtUtc = DateTime.UtcNow,
                    ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                    LastSeenAtUtc = DateTime.UtcNow
                });
            }
            await seed.SaveChangesAsync();
        }
        var running = new[]
        {
            (Who: "guest", Owner: guestId, Prefill: AddGuest(steam, steamGateway, guestId)),
            (Who: "administrator", Owner: adminId, Prefill: AddGuest(steam, steamGateway, adminId, temporary: false))
        };

        var services = new ServiceCollection();
        services.AddSingleton((SteamDaemonService)daemons[0]);
        services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
        services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
        services.AddSingleton((BattleNetDaemonService)daemons[3]);
        services.AddSingleton((RiotDaemonService)daemons[4]);
        await using var app = services.BuildServiceProvider();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = await database.Factory.CreateDbContextAsync();
        // The platform sign-in services are left out, so each of their sign-outs throws into the reset's own
        // catch, as in DatabaseResetProgressContractTests.
        var service = new DatabaseService(
            serviceContext,
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: database.Factory,
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: app,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);
        var operationId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)reset.Invoke(service, [operationId, new List<string> { "UserSessions" }, false, CancellationToken.None])!);

        await using (var cleared = await database.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await cleared.UserSessions.ToListAsync());
        }
        foreach (var (who, ownerId, prefill) in running)
        {
            Assert.False(
                steamGateway.ContainsContainer(prefill.ContainerId!),
                $"the {who} session's prefill container is still running after the User sessions reset ended every session, and no page can reach it");
            Assert.Empty(steam.GetUserSessions(ownerId));
        }
    }
}
