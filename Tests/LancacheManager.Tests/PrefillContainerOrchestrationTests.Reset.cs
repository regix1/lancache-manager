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
    // The Prefill Sessions reset deletes a live container's finished runs. The card stops listing them,
    // and neither the next sweep nor a restarted manager that adopts the container while its daemon still
    // lists the run as recent writes it back. The run still going keeps its row and entries throughout.
    [Fact]
    public async Task PrefillSessionsReset_ForgetsTheLiveSessionsFinishedRunsAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true, persistent: true);
        await using (var context = new AppDbContext(fixture.Options))
        {
            var row = await context.PrefillSessions.SingleAsync(session => session.SessionId == fixture.Session.Id);
            row.IsPersistent = true;
            row.Platform = PrefillPlatform.Steam;
            row.Status = PrefillSessionStatus.Active;
            await context.SaveChangesAsync();
        }
        var finished = await fixture.StartAsync("10");
        fixture.Client.Set(finished, "completed", 100, "success");
        await fixture.RefreshAsync();
        await finished.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await fixture.StartAsync("20");
        fixture.Client.Set(running, "downloading", 10, "downloading");
        await fixture.RefreshAsync();
        var runningEntries = await EntryCountAsync(fixture.Options, running.PrefillRunId);
        Assert.True(runningEntries > 0);

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = new AppDbContext(fixture.Options);
        await using var daemons = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var service = new DatabaseService(
            serviceContext,
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: new TestDbContextFactory(fixture.Options),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: daemons,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);
        var operationId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)reset.Invoke(service, [operationId, new List<string> { "PrefillSessions" }, false, CancellationToken.None])!);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.Equal([running.PrefillRunId], fixture.Daemon.GetRuns(fixture.Session.Id).Select(run => run.RunId));

        await fixture.RefreshAsync();
        Assert.Equal([running.PrefillRunId], fixture.Daemon.GetRuns(fixture.Session.Id).Select(run => run.RunId));
        Assert.Equal([running.PrefillRunId], await RunIdsAsync(fixture.Options));

        // A manager restart: a new service adopts the same container, whose daemon still lists the run as recent.
        var restarted = PrefillCacheChangeTests.NewDaemon(fixture.Options);
        var adopted = new DaemonSession
        {
            Id = fixture.Session.Id,
            UserId = fixture.Session.UserId,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = fixture.Session.Client
        };
        restarted.Daemon.InjectSession(adopted);
        var status = await adopted.Client.GetStatusAsync(CancellationToken.None);
        Assert.Contains(status!.RecentOperations!, operation => operation.OperationId == finished.PrefillRunId.ToString());
        await restarted.Daemon.RecoverRunsAsync(adopted, status, CancellationToken.None);
        await adopted.CancellationTokenSource.CancelAsync();

        Assert.Equal([running.PrefillRunId], restarted.Daemon.GetRuns(adopted.Id).Select(run => run.RunId));
        Assert.Equal([running.PrefillRunId], await RunIdsAsync(fixture.Options));
        Assert.Equal(0, await EntryCountAsync(fixture.Options, finished.PrefillRunId));
        Assert.Equal(runningEntries, await EntryCountAsync(fixture.Options, running.PrefillRunId));
    }

    // An idle container sends no session update of its own, so an open schedule page or Prefill panel learns of
    // the reset's deleted runs only from the reset: it sends the container's update once, not once per run.
    [Fact]
    public async Task PrefillSessionsReset_SendsTheIdleContainerItsSessionUpdateOnceAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true, persistent: true);
        await using (var context = new AppDbContext(fixture.Options))
        {
            var row = await context.PrefillSessions.SingleAsync(session => session.SessionId == fixture.Session.Id);
            row.IsPersistent = true;
            row.Platform = PrefillPlatform.Steam;
            row.Status = PrefillSessionStatus.Active;
            await context.SaveChangesAsync();
        }
        var failed = await fixture.StartAsync("10");
        fixture.Client.Set(failed, "failed", 0, "failed");
        await fixture.RefreshAsync();
        await failed.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var completed = await fixture.StartAsync("20");
        fixture.Client.Set(completed, "completed", 100, "success");
        await fixture.RefreshAsync();
        await completed.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var daemonNotifications = (RecordingNotificationProxy)typeof(PrefillDaemonServiceBase)
            .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Daemon)!;
        daemonNotifications.Invocations.Clear();

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = new AppDbContext(fixture.Options);
        await using var daemons = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var service = new DatabaseService(
            serviceContext,
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: new TestDbContextFactory(fixture.Options),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: daemons,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);
        var operationId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)reset.Invoke(service, [operationId, new List<string> { "PrefillSessions" }, false, CancellationToken.None])!);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        Assert.Empty(fixture.Daemon.GetRuns(fixture.Session.Id));
        var update = Assert.Single(daemonNotifications.Invocations, call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
            && Equals(call.Args[0], SignalREvents.DaemonSessionUpdated));
        Assert.Empty(Assert.IsType<DaemonSessionDto>(update.Args[1]).Runs);
    }

    // The reset runs after its request has answered, so the request's services are gone by the time it
    // forgets the deleted runs. A run that finishes while the reset is still deleting keeps its row and its
    // history, so it stays on the card too. With Prefill History also selected, the history delete runs first
    // and holds the running run's entries until the reset commits, so the run's finish waits for the commit,
    // its save then finds the entries gone and is dropped, and the next refresh finishes it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrefillSessionsReset_KeepsARunThatFinishedDuringTheResetAfterItsRequestEndedAsync(bool historyToo)
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true, persistent: true);
        await using (var context = new AppDbContext(fixture.Options))
        {
            var row = await context.PrefillSessions.SingleAsync(session => session.SessionId == fixture.Session.Id);
            row.IsPersistent = true;
            row.Platform = PrefillPlatform.Steam;
            row.Status = PrefillSessionStatus.Active;
            await context.SaveChangesAsync();
        }
        var finished = await fixture.StartAsync("10");
        fixture.Client.Set(finished, "completed", 100, "success");
        await fixture.RefreshAsync();
        await finished.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var running = await fixture.StartAsync("20");
        fixture.Client.Set(running, "downloading", 10, "downloading");
        await fixture.RefreshAsync();
        var runningEntries = await EntryCountAsync(fixture.Options, running.PrefillRunId);
        Assert.True(runningEntries > 0);

        await using var root = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var request = root.CreateScope();
        var tracker = DispatchProxy.Create<IUnifiedOperationTracker, ClearedTableHoldTracker>();
        var hold = (ClearedTableHoldTracker)(object)tracker;
        hold.Inner = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var finishedInsideReset = false;
        var waitedOnTheReset = false;
        Task? finish = null;
        hold.OnClearedTable = () =>
        {
            request.Dispose();
            fixture.Client.Set(running, "completed", 100, "success");
            finish = Task.Run(fixture.RefreshAsync);
            if (historyToo)
            {
                // The hook runs on the reset's own task, so it must not wait for a save that waits for the reset.
                waitedOnTheReset = WaitForALockWaitAsync(fixture.Options).GetAwaiter().GetResult();
                return;
            }
            finishedInsideReset = finish.Wait(TimeSpan.FromSeconds(5))
                && running.Completion.Task.Wait(TimeSpan.FromSeconds(5));
        };
        await using var serviceContext = new AppDbContext(fixture.Options);
        var service = new DatabaseService(
            serviceContext,
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: new TestDbContextFactory(fixture.Options),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: request.ServiceProvider,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);

        // Started on its own task as the reset endpoint's background work is, without the process-wide
        // running-reset entry that other test classes read.
        var operationId = tracker.RegisterOperation(OperationType.DatabaseReset, "Database reset", new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        List<string> tables = historyToo ? ["PrefillSessions", "PrefillHistoryEntries"] : ["PrefillSessions"];
        await Task.Run(() => (Task)reset.Invoke(service, [operationId, tables, false, CancellationToken.None])!)
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(OperationStatus.Completed, hold.Inner.GetOperation(operationId)!.Status);
        if (historyToo)
        {
            Assert.True(waitedOnTheReset);
            await finish!.WaitAsync(TimeSpan.FromSeconds(10));
            await fixture.RefreshAsync();
        }
        else
        {
            Assert.True(finishedInsideReset);
        }
        await running.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, running.TerminalCompletedFlag);
        Assert.Equal([running.PrefillRunId], fixture.Daemon.GetRuns(fixture.Session.Id).Select(run => run.RunId));
        Assert.Equal([running.PrefillRunId], await RunIdsAsync(fixture.Options));
        Assert.Equal(0, await EntryCountAsync(fixture.Options, finished.PrefillRunId));
        if (!historyToo)
        {
            Assert.Equal(runningEntries, await EntryCountAsync(fixture.Options, running.PrefillRunId));
        }
    }

    // True once another connection to the test database waits on a row lock, within five seconds.
    private static async Task<bool> WaitForALockWaitAsync(DbContextOptions<AppDbContext> options)
    {
        await using var context = new AppDbContext(options);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var waiting = await context.Database.SqlQueryRaw<int>(
                "SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                .SingleAsync();
            if (waiting > 0) return true;
            await Task.Delay(50);
        }
        return false;
    }

    // Holds the reset inside its delete transaction: the first table it reports cleared runs the hook.
    public class ClearedTableHoldTracker : DispatchProxy
    {
        public IUnifiedOperationTracker Inner { get; set; } = null!;
        public Action? OnClearedTable { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IUnifiedOperationTracker.UpdateProgress)
                && args![2] is "signalr.dbReset.clearedTable")
            {
                var hook = OnClearedTable;
                OnClearedTable = null;
                hook?.Invoke();
            }
            return targetMethod.Invoke(Inner, args);
        }
    }

    private static async Task<List<Guid>> RunIdsAsync(DbContextOptions<AppDbContext> options)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillRuns.AsNoTracking().Select(run => run.Id).ToListAsync();
    }

    private static async Task<int> EntryCountAsync(DbContextOptions<AppDbContext> options, Guid runId)
    {
        await using var context = new AppDbContext(options);
        return await context.PrefillHistoryEntries.AsNoTracking().CountAsync(entry => entry.RunId == runId);
    }
}
