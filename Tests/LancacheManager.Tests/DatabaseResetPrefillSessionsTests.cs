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

/// <summary>
/// The Prefill Sessions reset promises that sessions and their history go, while a container that
/// is still running keeps its own row and the run in progress, and each platform keeps the row of
/// its newest persistent container. Which rows a cascade takes is a property of the real schema,
/// so the reset method itself runs here against PostgreSQL.
/// </summary>
public sealed class DatabaseResetPrefillSessionsTests
{
    [Fact]
    public async Task PrefillSessionsResetKeepsOnlyTheLiveSessionItsRunningRunAndInProgressEntriesAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var finishedRunId = Guid.NewGuid();
        var runningRunId = Guid.NewGuid();
        var endedSessionRunId = Guid.NewGuid();
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.AddRange(
                NewSession("active-session", PrefillSessionStatus.Active, now),
                NewSession("ended-session", PrefillSessionStatus.Terminated, now));
            seed.PrefillRuns.AddRange(
                NewRun(finishedRunId, "active-session", now, completedAtUtc: now.AddMinutes(-5)),
                NewRun(runningRunId, "active-session", now, completedAtUtc: null),
                NewRun(endedSessionRunId, "ended-session", now, completedAtUtc: now.AddMinutes(-20)));
            seed.PrefillHistoryEntries.AddRange(
                NewEntry("entry-of-finished-run", "active-session", finishedRunId, PrefillHistoryEntryStatus.Completed, now),
                NewEntry("entry-of-running-run", "active-session", runningRunId, PrefillHistoryEntryStatus.InProgress, now),
                NewEntry("entry-of-ended-session-run", "ended-session", endedSessionRunId, PrefillHistoryEntryStatus.Completed, now),
                NewEntry("runless-finished", "active-session", null, PrefillHistoryEntryStatus.Completed, now),
                NewEntry("runless-in-progress", "active-session", null, PrefillHistoryEntryStatus.InProgress, now));
            await seed.SaveChangesAsync();
        }

        await ResetPrefillSessionsAsync(database);

        await using var verify = new AppDbContext(database.Options);
        Assert.Equal(
            ["active-session"],
            await verify.PrefillSessions.AsNoTracking().Select(row => row.SessionId).OrderBy(id => id).ToListAsync());
        Assert.Equal(
            [runningRunId],
            await verify.PrefillRuns.AsNoTracking().Select(run => run.Id).ToListAsync());
        Assert.Equal(
            ["entry-of-running-run", "runless-in-progress"],
            await verify.PrefillHistoryEntries.AsNoTracking().Select(entry => entry.AppId).OrderBy(id => id).ToListAsync());
    }

    // A person's Stop that left its container running is read back at the next start through the
    // platform's newest persistent row, so the reset keeps that row and deletes the other ended rows.
    [Fact]
    public async Task PrefillSessionsResetKeepsTheNewestPersistentRowOfEachPlatformAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        await using (var seed = new AppDbContext(database.Options))
        {
            seed.PrefillSessions.AddRange(
                Persistent(NewSession("steam-older", PrefillSessionStatus.Terminated, now.AddHours(-2)), PrefillPlatform.Steam),
                Persistent(NewSession("steam-newest", PrefillSessionStatus.Terminated, now), PrefillPlatform.Steam),
                NewSession("steam-guest", PrefillSessionStatus.Terminated, now.AddHours(1)),
                Persistent(NewSession("epic-newest", PrefillSessionStatus.Terminated, now.AddHours(-1)), PrefillPlatform.Epic));
            await seed.SaveChangesAsync();
        }

        await ResetPrefillSessionsAsync(database);

        await using var verify = new AppDbContext(database.Options);
        Assert.Equal(
            ["epic-newest", "steam-newest"],
            await verify.PrefillSessions.AsNoTracking().Select(row => row.SessionId).OrderBy(id => id).ToListAsync());

        static PrefillSession Persistent(PrefillSession row, PrefillPlatform platform)
        {
            row.IsPersistent = true;
            row.Platform = platform;
            return row;
        }
    }

    private static async Task ResetPrefillSessionsAsync(TestDatabase database)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = new AppDbContext(database.Options);
        await using var noDaemons = new ServiceCollection().BuildServiceProvider();
        var service = new DatabaseService(
            serviceContext,
            notifications,
            NullLogger<DatabaseService>.Instance,
            pathResolver: null!,
            dbContextFactory: new TestDbContextFactory(database.Options),
            steamKit2Service: null!,
            xboxCatalogMappingService: null!,
            epicMappingService: null!,
            serviceProvider: noDaemons,
            cacheManagementService: null!,
            stateRepository: null!,
            datasourceService: null!,
            operationTracker: tracker);
        var operationId = tracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database reset",
            new CancellationTokenSource());
        var reset = typeof(DatabaseService).GetMethod(
            "DoResetAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await ((Task)reset.Invoke(
            service,
            [operationId, new List<string> { "PrefillSessions" }, false, CancellationToken.None])!);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
    }

    internal static PrefillSession NewSession(string sessionId, PrefillSessionStatus status, DateTime now) => new()
    {
        SessionId = sessionId,
        CreatedBySessionId = Guid.NewGuid(),
        Status = status,
        CreatedAtUtc = now.AddHours(-1),
        ExpiresAtUtc = now.AddHours(1),
        EndedAtUtc = status == PrefillSessionStatus.Active ? null : now.AddMinutes(-10)
    };

    internal static PrefillRun NewRun(Guid id, string sessionId, DateTime now, DateTime? completedAtUtc) => new()
    {
        Id = id,
        SessionId = sessionId,
        DaemonInstanceId = Guid.NewGuid().ToString(),
        OptionsJson = "{}",
        SnapshotJson = "{}",
        StartedAtUtc = now.AddMinutes(-30),
        CompletedAtUtc = completedAtUtc
    };

    // AppId carries the entry's role so the surviving set reads as names in the assertion.
    internal static PrefillHistoryEntry NewEntry(
        string appId,
        string sessionId,
        Guid? runId,
        PrefillHistoryEntryStatus status,
        DateTime now) => new()
    {
        AppId = appId,
        SessionId = sessionId,
        RunId = runId,
        Status = status,
        StartedAtUtc = now.AddMinutes(-25),
        CompletedAtUtc = status == PrefillHistoryEntryStatus.InProgress ? null : now.AddMinutes(-6)
    };
}
