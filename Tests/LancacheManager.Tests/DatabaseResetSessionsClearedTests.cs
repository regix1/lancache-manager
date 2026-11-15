using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Every window signs out when it hears <c>UserSessionsCleared</c>. A User sessions reset deletes the rows first
/// inside one transaction, and a cancel or a later table's failure rolls that delete back, so the windows are
/// told only once the delete is committed: a window told earlier would sit on the sign-in screen while its
/// session still works. The window that started the reset also hears its Completed report before it signs out.
/// </summary>
[Collection(nameof(DatabaseResetCollection))]
public sealed class DatabaseResetSessionsClearedTests
{
    [Fact]
    public async Task UserSessionsReset_CanceledAfterTheDelete_TellsNoWindowToSignOutAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await SeedSessionAsync(database);
        using var cancel = new CancellationTokenSource();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, CancelAfterSessionsDeletedProxy>();
        var sent = (CancelAfterSessionsDeletedProxy)(object)notifications;
        sent.Cancel = cancel;
        await using var serviceContext = await database.Factory.CreateDbContextAsync();
        await using var app = NewApp();
        var (service, tracker, operationId) = NewReset(database, serviceContext, app, notifications);

        await RunResetAsync(service, operationId, cancel.Token);

        Assert.True(sent.Canceled, "the reset never reported the User sessions delete, so the cancel never ran after it");
        Assert.Equal(OperationStatus.Canceled, tracker.GetOperation(operationId)!.Status);
        await using (var read = await database.Factory.CreateDbContextAsync())
            Assert.True(await read.UserSessions.AnyAsync(row => row.Id == session), "the canceled reset did not roll the session delete back");
        Assert.DoesNotContain(sent.Invocations, call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && Equals(call.Args[0], SignalREvents.UserSessionsCleared));
    }

    [Fact]
    public async Task UserSessionsReset_Completed_TellsEveryWindowAfterTheCompletedReportAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var session = await SeedSessionAsync(database);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var sent = (RecordingNotificationProxy)(object)notifications;
        await using var serviceContext = await database.Factory.CreateDbContextAsync();
        await using var app = NewApp();
        var (service, tracker, operationId) = NewReset(database, serviceContext, app, notifications);

        await RunResetAsync(service, operationId, CancellationToken.None);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        var completedReport = sent.Invocations.FindIndex(call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && Equals(call.Args[0], SignalREvents.DatabaseResetProgress)
            && call.Args[1] is DatabaseResetProgress { Status: OperationStatus.Completed });
        var signOut = sent.Invocations.FindIndex(call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && Equals(call.Args[0], SignalREvents.UserSessionsCleared));
        Assert.True(completedReport >= 0, "the reset sent no Completed progress report");
        Assert.True(signOut >= 0, "the completed reset never told the windows their sessions are gone");
        Assert.True(
            signOut > completedReport,
            "the windows were told to sign out before the reset's Completed report, so the window that started it signs out without learning it finished");

        // A window signs out only when the event names its session, so one that signed in while the reset
        // was still stopping prefill keeps its new session.
        var cleared = sent.Invocations[signOut].Args[1]!;
        var named = cleared.GetType().GetProperty("sessionIds")?.GetValue(cleared) as IEnumerable<string>;
        Assert.True(
            named?.Contains(session.ToString()) == true,
            "the reset's UserSessionsCleared does not name the session it deleted, so no window can tell whether its own session ended");
    }

    private static async Task<Guid> SeedSessionAsync(TestDatabase database)
    {
        var now = DateTime.UtcNow;
        var id = Guid.NewGuid();
        await using var seed = await database.Factory.CreateDbContextAsync();
        seed.UserSessions.Add(new UserSession
        {
            Id = id,
            SessionTokenHash = "hash-" + id.ToString("N"),
            SessionType = SessionType.Admin,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(1),
            LastSeenAtUtc = now
        });
        await seed.SaveChangesAsync();
        return id;
    }

    private static ServiceProvider NewApp() => new ServiceCollection()
        .AddSingleton(new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance))
        .BuildServiceProvider();

    private static (DatabaseService Service, UnifiedOperationTracker Tracker, Guid OperationId) NewReset(
        TestDatabase database,
        AppDbContext serviceContext,
        ServiceProvider app,
        ISignalRNotificationService notifications)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        // The platform sign-in services are left out, so each of their sign-outs throws into the reset's own
        // catch, as in DatabaseResetInitiatorWindowTests.
        var service = new DatabaseService(
            serviceContext,
            notifications,
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
        return (service, tracker, operationId);
    }

    private static Task RunResetAsync(DatabaseService service, Guid operationId, CancellationToken cancellationToken)
    {
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)reset.Invoke(service, [operationId, new List<string> { "UserSessions" }, false, cancellationToken])!;
    }

    /// <summary>
    /// Cancels the reset when it reports the User sessions delete, which runs after the delete and before the
    /// commit.
    /// </summary>
    private class CancelAfterSessionsDeletedProxy : RecordingNotificationProxy
    {
        public CancellationTokenSource? Cancel { get; set; }

        public bool Canceled { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [_, DatabaseResetProgress { StageKey: "signalr.dbReset.clearedUserSessions" }])
            {
                Cancel!.Cancel();
                Canceled = true;
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
