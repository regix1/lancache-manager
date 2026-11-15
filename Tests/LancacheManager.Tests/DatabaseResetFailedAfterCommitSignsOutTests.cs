using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A full reset clears the User sessions and the log rows together. Once its delete has committed the
/// sessions are gone whatever happens next, so every window has to hear <c>UserSessionsCleared</c> and
/// lose its live connection even when a later step fails. The step that fails here is the save of the
/// cleared log positions to <c>state.json</c> (a full disk or a read-only data folder), which runs right
/// after the commit.
/// </summary>
[Collection(nameof(DatabaseResetCollection))]
public sealed class DatabaseResetFailedAfterCommitSignsOutTests
{
    [Fact]
    public async Task ResetThatFailsAfterItsSessionDeleteCommits_StillSignsEveryWindowOutAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "lcm-reset-after-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await using var database = await TestDatabase.CreateAsync();
            var sessionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var seed = new AppDbContext(database.Options))
            {
                seed.UserSessions.Add(new UserSession
                {
                    Id = sessionId,
                    SessionTokenHash = "hash-" + sessionId.ToString("N"),
                    SessionType = SessionType.Guest,
                    CreatedAtUtc = now,
                    ExpiresAtUtc = now.AddDays(1),
                    LastSeenAtUtc = now
                });
                await seed.SaveChangesAsync();
            }

            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                    ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                    ["LanCache:DataSources:0:Enabled"] = "false"
                })
                .Build();
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
                new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, paths),
                NullLogger<SecureStateEncryptionService>.Instance);
            var state = new ScheduledPrefillServiceTests.TestStateService(
                NullLogger<StateService>.Instance,
                paths,
                encryption,
                new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption));
            var refusedWrites = 0;
            state.BeforeWrite = () =>
            {
                if (state.FailWrites)
                    refusedWrites++;
            };

            // The guest's window holds a live connection under its session.
            var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connections.RegisterConnection(sessionId, "the guest's window", () => closed.TrySetResult());

            await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(root, stateService: state);
            try
            {
                await using var resetServices = new ServiceCollection()
                    .AddSingleton(harness.Owner)
                    .AddSingleton(new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance))
                    .AddSingleton(connections)
                    .BuildServiceProvider();
                var notifications = DispatchProxy.Create<ISignalRNotificationService, FailStateWritesAtCleanupProxy>();
                var sent = (FailStateWritesAtCleanupProxy)(object)notifications;
                sent.State = state;
                await using var serviceContext = new AppDbContext(database.Options);
                var service = new DatabaseService(
                    serviceContext,
                    notifications,
                    NullLogger<DatabaseService>.Instance,
                    paths,
                    database.Factory,
                    steamKit2Service: null!,
                    xboxCatalogMappingService: null!,
                    epicMappingService: null!,
                    serviceProvider: resetServices,
                    cacheManagementService: null!,
                    state,
                    new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance),
                    harness.Tracker);
                var operationId = harness.Tracker.RegisterOperation(
                    OperationType.DatabaseReset,
                    "Database reset",
                    new CancellationTokenSource());
                var reset = typeof(DatabaseService).GetMethod("DoResetAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

                await ((Task)reset.Invoke(
                    service,
                    [operationId, new List<string> { "UserSessions", "LogEntries" }, false, CancellationToken.None])!);

                Assert.True(sent.Armed, "the reset never reported its file cleanup, so the state write failure was never injected");
                Assert.True(refusedWrites > 0, "no state write was refused after the commit, so the reset never met the failure");
                await using (var read = new AppDbContext(database.Options))
                    Assert.False(
                        await read.UserSessions.AnyAsync(row => row.Id == sessionId),
                        "the guest's session row is still there, so the delete did not commit before the failure");

                var wrong = new List<string>();
                if (!sent.Invocations.Any(call =>
                        call.Method == nameof(ISignalRNotificationService.NotifyAllAsync)
                        && Equals(call.Args[0], SignalREvents.UserSessionsCleared)))
                    wrong.Add("no window was sent UserSessionsCleared, so the guest's window stays drawn signed in on a deleted session");
                if (!closed.Task.IsCompleted)
                    wrong.Add("the guest's live connection is still open");
                Assert.True(
                    wrong.Count == 0,
                    $"the reset deleted the sessions and then failed ({harness.Tracker.GetOperation(operationId)?.Status}): {string.Join("; ", wrong)}");
            }
            finally
            {
                state.FailWrites = false;
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Makes every state write fail from the reset's file cleanup on. That report is the last step before
    /// the delete commits, and no state write runs between it and the commit.
    /// </summary>
    private class FailStateWritesAtCleanupProxy : RecordingNotificationProxy
    {
        public ScheduledPrefillServiceTests.TestStateService? State { get; set; }

        public bool Armed { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [_, DatabaseResetProgress { StageKey: "signalr.dbReset.cleanup" }])
            {
                State!.FailWrites = true;
                Armed = true;
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
