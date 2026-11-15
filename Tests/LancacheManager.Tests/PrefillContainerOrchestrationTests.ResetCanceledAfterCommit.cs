using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
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
    // A reset of User sessions and Prefill sessions commits its delete, then stops the prefill each deleted
    // session started and drops the deleted runs from the live sessions' cards, in either order. Once the delete
    // has committed, a Cancel click on the reset card brings no row back, so the card and the completion event
    // say the reset completed. Both landings of the click run: while the stop waits on the Docker kill of the
    // guest's container, and while the update that drops a live session's deleted run is being sent. Each step
    // is held when it is reached and let go once the click has landed in the other, whichever comes first.
    [Fact]
    public async Task UserAndPrefillSessionsReset_CanceledAfterItsDeleteCommitted_EndsCompleted()
    {
        var wrongEndings = new List<string>();
        foreach (var cancelDuringKill in new[] { true, false })
        {
            var landing = cancelDuringKill
                ? "Cancel clicked while the prefill stop waited on the Docker kill"
                : "Cancel clicked while the live session's update for its deleted run was being sent";
            await using var database = await TestDatabase.CreateAsync();
            var now = DateTime.UtcNow;
            var history = new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
            var recorder = (EventRecordingNotificationsProxy)(object)notifications;
            var deps = MakeDeps(database.Factory, history, Config(PersistenceMode.FullPersistence, true), notifications: notifications);
            var steamGateway = new RecordingContainerGateway { HoldKillContainer = true };
            var epicGateway = new RecordingContainerGateway();
            var daemons = new PrefillDaemonServiceBase[]
            {
                new TestSteamDaemon(deps, steamGateway), new TestEpicDaemon(deps, epicGateway),
                new TestXboxDaemon(deps, new RecordingContainerGateway()), new TestBattleNetDaemon(deps, new RecordingContainerGateway()),
                new TestRiotDaemon(deps, new RecordingContainerGateway())
            };

            // The guest whose session the reset deletes, with a temporary prefill running.
            var guestId = Guid.NewGuid();
            AddGuest(daemons[0], steamGateway, guestId);

            // A live prefill whose owner has no session row here, so it keeps running; its finished run's
            // row is deleted by the reset, so the reset sends its card an update without that run.
            var live = AddGuest(daemons[1], epicGateway, Guid.NewGuid());
            var finishedRunId = Guid.NewGuid();
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
                seed.PrefillSessions.Add(DatabaseResetPrefillSessionsTests.NewSession(live.Id, PrefillSessionStatus.Active, now));
                seed.PrefillRuns.Add(DatabaseResetPrefillSessionsTests.NewRun(finishedRunId, live.Id, now, completedAtUtc: now.AddMinutes(-5)));
                await seed.SaveChangesAsync();
            }
            var instance = Guid.NewGuid().ToString();
            live.Runs[finishedRunId] = new DaemonRun
            {
                PrefillRunId = finishedRunId,
                SessionId = live.Id,
                DaemonInstanceId = instance,
                Options = new DaemonRunOptions { AppIds = ["10"] },
                Snapshot = new DaemonRunSnapshot
                {
                    OperationId = finishedRunId.ToString(),
                    DaemonInstanceId = instance,
                    StartedAt = now.AddMinutes(-30),
                    UpdatedAt = now.AddMinutes(-5),
                    State = "completed"
                },
                TerminalCompletedFlag = 2,
                CompletedAtUtc = now.AddMinutes(-5)
            };

            // The held update is the one sent once the deleted run left the live session, which only the reset's
            // forget of the deleted runs does, so no earlier send of that card is taken for it.
            var updateSending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            recorder.SendHandler = name =>
                name == SignalREvents.EpicDaemonSessionUpdated && !live.Runs.ContainsKey(finishedRunId) && updateSending.TrySetResult()
                    ? releaseUpdate.Task
                    : Task.CompletedTask;

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
            using var resetCancel = new CancellationTokenSource();
            OperationTerminalInfo? ending = null;
            var operationId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", resetCancel,
                onTerminalEmit: terminal =>
                {
                    ending = terminal;
                    return Task.CompletedTask;
                });
            var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var resetting = (Task)reset.Invoke(service, [operationId, new List<string> { "UserSessions", "PrefillSessions" }, false, resetCancel.Token])!;

            // Each held step: reached, and its release. The click lands in the one this landing names.
            var killHeld = (Reached: steamGateway.KillContainerEntered.Task, Release: steamGateway.ReleaseKillContainer);
            var updateHeld = (Reached: updateSending.Task, Release: releaseUpdate);
            var clickedIn = cancelDuringKill ? killHeld : updateHeld;
            var stillHeld = new[] { killHeld, updateHeld }.ToList();
            bool? sessionsGoneAtClick = null;
            while (stillHeld.Count > 0)
            {
                await Task.WhenAny(stillHeld.Select(held => held.Reached)).WaitAsync(TimeSpan.FromSeconds(30));
                var reached = stillHeld.First(held => held.Reached.IsCompleted);
                if (reached == clickedIn)
                {
                    await using (var read = await database.Factory.CreateDbContextAsync())
                    {
                        sessionsGoneAtClick = !await read.UserSessions.AnyAsync();
                    }
                    tracker.CancelOperation(operationId);
                }
                reached.Release.TrySetResult();
                stillHeld.Remove(reached);
            }
            await resetting.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(sessionsGoneAtClick == true, $"{landing}: the step the click landed in ran before the reset's delete committed");
            await using (var cleared = await database.Factory.CreateDbContextAsync())
            {
                Assert.Empty(await cleared.UserSessions.ToListAsync());
                Assert.Empty(await cleared.PrefillRuns.ToListAsync());
            }
            var card = tracker.GetOperation(operationId)!.Status;
            if (card != OperationStatus.Completed)
                wrongEndings.Add($"{landing}: the reset had already deleted every session and finished run, but its card says {card}");
            if (ending is not { Success: true, Canceled: false })
                wrongEndings.Add($"{landing}: the reset had already deleted every session and finished run, but its completion event says Success={ending?.Success}, Canceled={ending?.Canceled}");
        }

        Assert.True(wrongEndings.Count == 0, string.Join(Environment.NewLine, wrongEndings));
    }
}
