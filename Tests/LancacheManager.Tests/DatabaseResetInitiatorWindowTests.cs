using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// With authentication off, the browser that starts a User sessions reset is signed in under the shared
/// session, so its own session is among the rows the reset deletes. That window learns the reset finished
/// from the reset's Completed progress report over its live connection: the dashboard refetch and the reset
/// dialog's completion line run on it. Its connection is closed only after that report went out, because a
/// closed connection waits 10 s before it reconnects and would miss it.
/// </summary>
[Collection(nameof(DatabaseResetCollection))]
public sealed class DatabaseResetInitiatorWindowTests
{
    [Fact]
    public async Task UserSessionsReset_TheWindowThatStartedItReceivesTheCompletedReportAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var shared = Guid.NewGuid();
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserSessions.Add(new UserSession
            {
                Id = shared,
                SessionTokenHash = "hash-" + shared.ToString("N"),
                SessionType = SessionType.Admin,
                CreatedAtUtc = now,
                ExpiresAtUtc = now.AddDays(1),
                LastSeenAtUtc = now
            });
            await seed.SaveChangesAsync();
        }

        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var sent = (RecordingNotificationProxy)(object)notifications;
        // The number of broadcasts the reset had sent when the window's connection was closed.
        int? sentBeforeClose = null;
        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        connections.RegisterConnection(shared, "initiator-window", () => sentBeforeClose ??= sent.Invocations.Count);
        var presence = new ActivityRegistry(
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<ActivityRegistry>.Instance);
        await presence.ReportAsync(ActivityDomains.UserSession, shared.ToString(), ActivityAspects.Present, isActive: true);

        await using var app = new ServiceCollection()
            .AddSingleton(connections)
            .AddSingleton<IActivityRegistry>(presence)
            .BuildServiceProvider();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await using var serviceContext = await database.Factory.CreateDbContextAsync();
        // The platform sign-in services are left out, so each of their sign-outs throws into the reset's own
        // catch, as in DatabaseResetProgressContractTests.
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
        var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await ((Task)reset.Invoke(service, [operationId, new List<string> { "UserSessions" }, false, CancellationToken.None])!);

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        var completedReport = sent.Invocations.FindIndex(call =>
            call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
            && Equals(call.Args[0], SignalREvents.DatabaseResetProgress)
            && call.Args[1] is DatabaseResetProgress { Status: OperationStatus.Completed });
        Assert.True(completedReport >= 0, "the reset sent no Completed progress report");
        Assert.True(
            sentBeforeClose is null || sentBeforeClose > completedReport,
            "the window that started the reset had its live connection closed before the reset's Completed report went out, so it never learns the reset finished");
    }
}
