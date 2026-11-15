using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The Data page reset of User sessions deletes every session row. A window signed in under one of those
/// sessions keeps its live connection open and stays listed as present until it is told otherwise, so the
/// reset closes each deleted session's connections and empties the session presence set, as a new API key
/// does when it clears every session.
/// </summary>
[Collection(nameof(DatabaseResetCollection))]
public sealed class DatabaseResetSignsOutLiveWindowsTests
{
    [Fact]
    public async Task UserSessionsReset_ClosesEachDeletedSessionsLiveConnectionAndPresenceAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var now = DateTime.UtcNow;
        var admin = Guid.NewGuid();
        var user = Guid.NewGuid();
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            foreach (var (id, type) in new[] { (admin, SessionType.Admin), (user, SessionType.User) })
            {
                seed.UserSessions.Add(new UserSession
                {
                    Id = id,
                    SessionTokenHash = "hash-" + id.ToString("N"),
                    SessionType = type,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = now.AddDays(1),
                    LastSeenAtUtc = now
                });
            }
            await seed.SaveChangesAsync();
        }

        // Each signed-in window holds a live connection opened under its session and is listed as present.
        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        var presence = new ActivityRegistry(
            DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>(),
            NullLogger<ActivityRegistry>.Instance);
        var windows = new Dictionary<string, (Guid SessionId, TaskCompletionSource Closed)>
        {
            ["the administrator's window"] = (admin, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)),
            ["the account holder's window"] = (user, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
        };
        foreach (var (name, window) in windows)
        {
            connections.RegisterConnection(window.SessionId, name, () => window.Closed.TrySetResult());
            await presence.ReportAsync(ActivityDomains.UserSession, window.SessionId.ToString(), ActivityAspects.Present, isActive: true);
        }

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

        Assert.Equal(OperationStatus.Completed, tracker.GetOperation(operationId)!.Status);
        await using (var cleared = await database.Factory.CreateDbContextAsync())
        {
            Assert.Empty(await cleared.UserSessions.ToListAsync());
        }
        var present = (await presence.GetSnapshotAsync()).Activities
            .Where(item => item.Domain == ActivityDomains.UserSession && item.Aspect == ActivityAspects.Present)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        var stillSignedIn = new List<string>();
        foreach (var (name, window) in windows)
        {
            if (!window.Closed.Task.IsCompleted)
                stillSignedIn.Add($"the reset deleted the session behind {name}, but that window's live connection is still open, so it stays drawn signed in");
            if (present.Contains(window.SessionId.ToString()))
                stillSignedIn.Add($"the reset deleted the session behind {name}, but the session presence set still lists it");
        }
        Assert.True(stillSignedIn.Count == 0, string.Join(Environment.NewLine, stillSignedIn));
    }
}
