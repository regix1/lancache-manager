using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed partial class PrefillContainerOrchestrationTests
{
    // A reset that clears User sessions and Downloads holds the log files while it deletes rows, so the live log
    // import pauses for the delete. The stop of each deleted session's prefill runs after the delete and waits on
    // Docker; the live import does not wait for that. Both orders of the import's next pass run: a pass already
    // waiting while the delete runs, and a pass that asks while the stop waits on the Docker kill.
    [Fact]
    public async Task UserSessionsAndDownloadsReset_LetsTheLiveImportRunWhileThePrefillStopKills()
    {
        var pausedImports = new List<string>();
        foreach (var askedDuringDelete in new[] { true, false })
        {
            var order = askedDuringDelete
                ? "an import pass that asked for the logs while the reset deleted rows"
                : "an import pass that asked for the logs while the prefill stop waited on the Docker kill";
            await using var database = await TestDatabase.CreateAsync();
            var now = DateTime.UtcNow;
            var history = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
            var deps = MakeDeps(database.Factory, history, Config(PersistenceMode.FullPersistence, true));
            var steamGateway = new RecordingContainerGateway { HoldKillContainer = true };
            var daemons = new PrefillDaemonServiceBase[]
            {
                new TestSteamDaemon(deps, steamGateway), new TestEpicDaemon(deps, new RecordingContainerGateway()),
                new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
                new TestRiotDaemon(deps, new RecordingContainerGateway())
            };

            // The guest whose session the reset deletes, with a temporary prefill running.
            var guestId = Guid.NewGuid();
            await using (var seed = await database.Factory.CreateDbContextAsync())
            {
                seed.UserSessions.Add(new UserSession
                {
                    Id = guestId,
                    SessionTokenHash = "hash-" + guestId.ToString("N"),
                    SessionType = SessionType.Guest,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = now.AddDays(1),
                    LastSeenAtUtc = now
                });
                await seed.SaveChangesAsync();
            }
            AddGuest(daemons[0], steamGateway, guestId);

            var tracker = new UnifiedOperationTracker(
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            var logs = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                new ConfigurationBuilder().Build(),
                stateService: null!,
                scopes: null!,
                applicationLifetime: null!,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                tracker);
            var services = new ServiceCollection();
            services.AddSingleton(logs);
            services.AddSingleton((SteamDaemonService)daemons[0]);
            services.AddSingleton((EpicPrefillDaemonService)daemons[1]);
            services.AddSingleton((XboxPrefillDaemonService)daemons[2]);
            services.AddSingleton((BattleNetDaemonService)daemons[3]);
            services.AddSingleton((RiotDaemonService)daemons[4]);
            await using var app = services.BuildServiceProvider();

            // The live import's next pass asks for the logs the way the log processor does.
            var importId = tracker.RegisterOperation(OperationType.LogProcessing, "Live log import", new CancellationTokenSource(), liveIngest: true);
            Task<LogFileLock>? importPass = null;
            var logger = new LancacheManager.Tests.CapturingLogger<DatabaseService>
            {
                OnLogged = entry =>
                {
                    if (askedDuringDelete && importPass is null
                        && entry.Message.StartsWith("Cleared ", StringComparison.Ordinal)
                        && entry.Message.EndsWith(" downloads", StringComparison.Ordinal))
                    {
                        importPass = logs.LockLogFilesAsync(importId, OperationType.LogProcessing, LogFileLockKind.Ingest, CancellationToken.None);
                    }
                }
            };
            await using var serviceContext = await database.Factory.CreateDbContextAsync();
            // The platform sign-in services are left out, so each of their sign-outs throws into the reset's own
            // catch, as in DatabaseResetProgressContractTests.
            var service = new DatabaseService(
                serviceContext,
                DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
                logger,
                PathResolverProxy.New(NewTempRoot()),
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
            var resetting = (Task)reset.Invoke(service, [operationId, new List<string> { "UserSessions", "Downloads" }, false, CancellationToken.None])!;

            await steamGateway.KillContainerEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var askedBeforeTheKill = importPass is not null;
            importPass ??= logs.LockLogFilesAsync(importId, OperationType.LogProcessing, LogFileLockKind.Ingest, CancellationToken.None);
            // The kill is still held here, so a pass that is not granted within this bound waits for the stop.
            bool ranDuringKill;
            try
            {
                await importPass.WaitAsync(TimeSpan.FromSeconds(5));
                ranDuringKill = true;
            }
            catch (TimeoutException)
            {
                ranDuringKill = false;
            }
            steamGateway.ReleaseKillContainer.TrySetResult();
            await resetting.WaitAsync(TimeSpan.FromSeconds(30));
            await (await importPass.WaitAsync(TimeSpan.FromSeconds(30))).DisposeAsync();

            Assert.Equal(askedDuringDelete, askedBeforeTheKill);
            Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
            if (!ranDuringKill)
                pausedImports.Add($"{order}: the live log import stayed paused behind the reset's log lock while the reset's prefill stop waited on the Docker kill");
        }

        Assert.True(pausedImports.Count == 0, string.Join(Environment.NewLine, pausedImports));
    }
}
