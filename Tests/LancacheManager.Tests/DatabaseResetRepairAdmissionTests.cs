using System.Reflection;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

[CollectionDefinition(nameof(DatabaseResetRepairAdmissionCollection), DisableParallelization = true)]
public sealed class DatabaseResetRepairAdmissionCollection
{
}

[Collection(nameof(DatabaseResetRepairAdmissionCollection))]
public sealed class DatabaseResetRepairAdmissionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetWaitsForRestoredRepairBeforeStartingProducerAsync(bool fullReset)
    {
        var waitLimit = TimeSpan.FromSeconds(10);
        var root = Path.Combine(
            Path.GetTempPath(),
            "lcm-reset-repair-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var database = await TestDatabase.CreateAsync();
            var repairId = Guid.NewGuid();
            var unrelatedCheckpointId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await using (var seed = database.Factory.CreateDbContext())
            {
                seed.EvictionScanCheckpoints.AddRange(
                    new EvictionScanCheckpoint
                    {
                        OperationId = repairId,
                        Processed = 8,
                        Evicted = 3,
                        UnEvicted = 2,
                        StartedAtUtc = now.AddMinutes(-2)
                    },
                    new EvictionScanCheckpoint
                    {
                        OperationId = unrelatedCheckpointId,
                        Processed = 5,
                        Evicted = 1,
                        UnEvicted = 1,
                        StartedAtUtc = now.AddMinutes(-1),
                        FinalizedAtUtc = now
                    });
                await seed.SaveChangesAsync();
            }

            var state = StateTestMethods.CreateStateService(Path.Combine(root, "state"));
            state.SetSetupCompleted(true);
            state.SaveOperationRepairs(
            [
                new OperationRepair
                {
                    Id = repairId,
                    Type = OperationType.EvictionScan,
                    Name = "Eviction scan",
                    StartedAt = now.AddMinutes(-3),
                    Phase = OperationRepairPhase.Repairing,
                    Outcome = OperationStatus.Failed,
                    Error = "signalr.generic.interruptedByRestart",
                    Sources =
                    [
                        new OperationRepairSource
                        {
                            Datasource = "alpha",
                            LogRoot = Path.Combine(root, "logs"),
                            CacheRoot = Path.Combine(root, "cache"),
                            KeyScheme = "steam"
                        }
                    ],
                    EvictionScanId = repairId,
                    EvictionScan = new EvictionScanRepair
                    {
                        Processed = 8,
                        Evicted = 3,
                        UnEvicted = 2
                    }
                }
            ]);

            var restoreEntered = new TaskCompletionSource<Guid>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseRestore = new ManualResetEventSlim();
            var applyEntered = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseApply = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var resetContexts = new ResetContexts(database.Options);

            async Task ApplyAsync(OperationRepair repair, CancellationToken cancellationToken)
            {
                try
                {
                    await using var context = database.Factory.CreateDbContext();
                    var checkpoint = await context.EvictionScanCheckpoints.SingleAsync(
                        item => item.OperationId == repair.EvictionScanId,
                        cancellationToken);
                    applyEntered.TrySetResult();
                    await releaseApply.Task.WaitAsync(cancellationToken);
                    checkpoint.FinalizedAtUtc = DateTime.UtcNow;
                    await context.SaveChangesAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    applyEntered.TrySetException(exception);
                    throw;
                }
            }

            await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
                root,
                start: false,
                stateService: state,
                apply: ApplyAsync,
                onRestore: (operationId, stoppingToken) =>
                {
                    restoreEntered.TrySetResult(operationId);
                    releaseRestore.Wait(stoppingToken);
                });
            var tracker = harness.Tracker;
            var conflictChecker = new OperationConflictChecker(
                tracker,
                harness.Owner,
                NullLogger<OperationConflictChecker>.Instance);
            var queue = new OperationWaitlistService(
                tracker,
                conflictChecker,
                NullLogger<OperationWaitlistService>.Instance);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                    ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                    ["LanCache:DataSources:0:Enabled"] = "false",
                    ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
                })
                .Build();
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var datasources = new DatasourceService(
                configuration,
                paths,
                NullLogger<DatasourceService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
            var notificationCalls = (RecordingNotificationProxy)(object)notifications;
            var cacheManager = new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                paths,
                rustProcessHelper: null!,
                nginxLogRotationService: null!,
                datasources,
                state,
                database.Factory,
                gameCacheDetectionService: null!,
                tracker,
                notifications,
                DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
                conflictChecker,
                new DatasourceCapabilityService(datasources),
                CacheScanGateHarness.Idle(),
                harness.Owner,
                null!);
            await using var serviceContext = database.Factory.CreateDbContext();
            // The full wipe clears Downloads and LogEntries, so it takes the log lock from the owner.
            await using var resetServices = new ServiceCollection()
                .AddSingleton(harness.Owner)
                .AddSingleton(new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance))
                .BuildServiceProvider();
            var databaseService = new DatabaseService(
                serviceContext,
                notifications,
                NullLogger<DatabaseService>.Instance,
                paths,
                resetContexts,
                steamKit2Service: null!,
                xboxCatalogMappingService: null!,
                epicMappingService: null!,
                serviceProvider: resetServices,
                cacheManager,
                state,
                datasources,
                tracker);
            var controller = new DatabaseController(
                databaseService,
                NullLogger<DatabaseController>.Instance,
                queue);

            Task<IActionResult> InvokeResetAsync() => fullReset
                ? controller.ResetDatabaseAsync(CancellationToken.None)
                : controller.ResetSelectedTablesAsync(
                    new ResetTablesRequest { Tables = ["EvictionScanCheckpoints"] },
                    CancellationToken.None);

            async Task AssertCheckpointsAsync()
            {
                await using var context = database.Factory.CreateDbContext();
                var checkpoints = await context.EvictionScanCheckpoints
                    .AsNoTracking()
                    .OrderBy(item => item.OperationId)
                    .ToListAsync();
                Assert.Equal(
                    new[] { repairId, unrelatedCheckpointId }.Order(),
                    checkpoints.Select(item => item.OperationId));
                var referenced = Assert.Single(checkpoints, item => item.OperationId == repairId);
                Assert.Equal((8, 3, 2), (referenced.Processed, referenced.Evicted, referenced.UnEvicted));
            }

            async Task AssertRepairBlockAsync()
            {
                var conflict = await conflictChecker.CheckAsync(
                    OperationType.DatabaseReset,
                    ConflictScope.Bulk(),
                    CancellationToken.None);
                Assert.NotNull(conflict);
                Assert.Equal(repairId, conflict!.ActiveOperationId);
                Assert.Equal(nameof(OperationType.EvictionScan), conflict.ActiveOperationType);
                Assert.Equal("repair", conflict.ActiveOperationScope);
                Assert.NotNull(conflict.Context);
                Assert.Equal(true, conflict.Context!["repairPending"]);
            }

            static OperationStartResponse AssertWaiting(IActionResult result)
            {
                var accepted = Assert.IsType<AcceptedResult>(result);
                var waiting = Assert.IsType<OperationStartResponse>(accepted.Value);
                Assert.Equal(OperationStartStatus.Waiting, waiting.Status);
                return waiting;
            }

            bool ResetStarted() => notificationCalls.Invocations.Any(call =>
                call.Args.FirstOrDefault() as string == SignalREvents.DatabaseResetStarted);

            Task? startup = null;
            Task? finish = null;
            Guid? waitingId = null;
            Guid? resetId = null;
            var resetEnded = new TaskCompletionSource<OperationInfo>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void OnTerminal(OperationInfo operation)
            {
                if (operation.Type == OperationType.DatabaseReset
                    && operation.Id != waitingId)
                {
                    resetEnded.TrySetResult(operation);
                }
            }
            tracker.OperationTerminal += OnTerminal;
            try
            {
                startup = Task.Run(() => harness.Owner.StartAsync(CancellationToken.None));
                Assert.Equal(repairId, await restoreEntered.Task.WaitAsync(waitLimit));
                await harness.Owner.WaitForRecoveryOwnershipAsync(CancellationToken.None);

                Assert.Null(tracker.GetOperation(repairId));
                Assert.Contains(harness.Owner.GetPendingRepairs(), item => item.Id == repairId);
                await AssertRepairBlockAsync();
                var first = AssertWaiting(await InvokeResetAsync());
                waitingId = first.OperationId;
                Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(first.OperationId)!.Status);
                Assert.False(ResetStarted());
                Assert.False(resetContexts.Entered.IsCompleted);
                await AssertCheckpointsAsync();

                releaseRestore.Set();
                await applyEntered.Task.WaitAsync(waitLimit);

                Assert.NotNull(tracker.GetOperation(repairId));
                Assert.Contains(harness.Owner.GetPendingRepairs(), item => item.Id == repairId);
                await AssertRepairBlockAsync();
                // The same reset joins the waiting card: same id, still waiting.
                var repeated = AssertWaiting(await InvokeResetAsync());
                Assert.Equal(first.OperationId, repeated.OperationId);
                Assert.Equal(OperationStatus.Waiting, tracker.GetOperation(first.OperationId)!.Status);
                Assert.False(ResetStarted());
                Assert.False(resetContexts.Entered.IsCompleted);
                await AssertCheckpointsAsync();

                finish = harness.Owner.FinishRepairAsync(
                    repairId,
                    success: false,
                    canceled: false,
                    error: "signalr.generic.interruptedByRestart");
                releaseApply.TrySetResult();
                await finish.WaitAsync(waitLimit);
                await resetContexts.Entered.WaitAsync(waitLimit);

                Assert.DoesNotContain(harness.Owner.GetPendingRepairs(), item => item.Id == repairId);
                var completedRepair = Assert.Single(
                    state.LoadOperationRepairs(),
                    item => item.Id == repairId);
                Assert.Equal(OperationRepairPhase.Completed, completedRepair.Phase);
                await AssertCheckpointsAsync();

                var reset = Assert.Single(
                    tracker.GetActiveOperations(OperationType.DatabaseReset),
                    item => item.Id != first.OperationId && item.Status != OperationStatus.Waiting);
                resetId = reset.Id;
                Assert.True(ResetStarted());

                if (fullReset)
                {
                    Assert.Equal(OperationCancelResult.Requested, tracker.CancelOperation(reset.Id));
                    var terminal = await resetEnded.Task.WaitAsync(waitLimit);
                    Assert.Equal(OperationStatus.Canceled, terminal.Status);
                    await cacheManager.ExecuteWithLockAsync(() => Task.FromResult(true))
                        .WaitAsync(waitLimit);
                    await AssertCheckpointsAsync();
                }
                else
                {
                    resetContexts.Release();
                    var terminal = await resetEnded.Task.WaitAsync(waitLimit);
                    Assert.Equal(OperationStatus.Completed, terminal.Status);
                    await using var verify = database.Factory.CreateDbContext();
                    Assert.Empty(await verify.EvictionScanCheckpoints.AsNoTracking().ToListAsync());
                }
            }
            finally
            {
                resetId ??= tracker.GetActiveOperations(OperationType.DatabaseReset)
                    .FirstOrDefault(item => item.Status != OperationStatus.Waiting)?.Id;
                if (resetId is { } runningId
                    && tracker.GetOperation(runningId) is { Status: not OperationStatus.Completed
                        and not OperationStatus.Failed and not OperationStatus.Canceled })
                {
                    tracker.CancelOperation(runningId);
                }
                if (waitingId is { } parkedId
                    && tracker.GetOperation(parkedId) is { Status: OperationStatus.Waiting })
                {
                    tracker.CancelOperation(parkedId);
                }
                releaseRestore.Set();
                releaseApply.TrySetResult();
                resetContexts.Release();
                if (resetId.HasValue && !resetEnded.Task.IsCompleted)
                {
                    await resetEnded.Task.WaitAsync(waitLimit);
                }
                if (finish is not null)
                {
                    await finish.WaitAsync(waitLimit);
                }
                if (startup is not null)
                {
                    await startup.WaitAsync(waitLimit);
                }
                if (fullReset)
                {
                    await cacheManager.ExecuteWithLockAsync(() => Task.FromResult(true))
                        .WaitAsync(waitLimit);
                }
                tracker.OperationTerminal -= OnTerminal;
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // Two tabs reset at the same moment. One reset runs and the other waits behind it, so the tables are
    // not wiped twice side by side.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TwoResetsSentTogetherStartOnce(bool fullReset)
    {
        var waitLimit = TimeSpan.FromSeconds(10);
        var root = Path.Combine(
            Path.GetTempPath(),
            "lcm-reset-together-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await using var database = await TestDatabase.CreateAsync();
            var state = StateTestMethods.CreateStateService(Path.Combine(root, "state"));
            state.SetSetupCompleted(true);
            var resetContexts = new ResetContexts(database.Options);
            await using var harness = await OperationRepairTests.RepairHarness.CreateAsync(
                root,
                stateService: state);
            var tracker = harness.Tracker;
            var checker = new OperationConflictChecker(
                tracker,
                harness.Owner,
                NullLogger<OperationConflictChecker>.Instance);
            // On code that checks outside the queue's lock, two resets both act on "nothing running".
            var conflicts = new SecondCallerBarrier(checker);
            var queue = new OperationWaitlistService(
                tracker,
                conflicts,
                NullLogger<OperationWaitlistService>.Instance);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["LanCache:DataSources:0:Name"] = "alpha",
                    ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                    ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                    ["LanCache:DataSources:0:Enabled"] = "false",
                    ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
                })
                .Build();
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var datasources = new DatasourceService(
                configuration,
                paths,
                NullLogger<DatasourceService>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
            var cacheManager = new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                paths,
                rustProcessHelper: null!,
                nginxLogRotationService: null!,
                datasources,
                state,
                database.Factory,
                gameCacheDetectionService: null!,
                tracker,
                notifications,
                DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
                checker,
                new DatasourceCapabilityService(datasources),
                CacheScanGateHarness.Idle(),
                harness.Owner,
                null!);
            await using var serviceContext = database.Factory.CreateDbContext();
            await using var resetServices = new ServiceCollection()
                .AddSingleton(harness.Owner)
                .AddSingleton(new PrefillSessionService(database.Factory, NullLogger<PrefillSessionService>.Instance))
                .BuildServiceProvider();
            var databaseService = new DatabaseService(
                serviceContext,
                notifications,
                NullLogger<DatabaseService>.Instance,
                paths,
                resetContexts,
                steamKit2Service: null!,
                xboxCatalogMappingService: null!,
                epicMappingService: null!,
                serviceProvider: resetServices,
                cacheManager,
                state,
                datasources,
                tracker);
            var controller = new DatabaseController(
                databaseService,
                NullLogger<DatabaseController>.Instance,
                queue);

            Task<IActionResult> ResetAsync() => fullReset
                ? controller.ResetDatabaseAsync(CancellationToken.None)
                : controller.ResetSelectedTablesAsync(
                    new ResetTablesRequest { Tables = ["EvictionScanCheckpoints"] },
                    CancellationToken.None);

            var answers = await Task.WhenAll(ResetAsync(), ResetAsync());
            try
            {
                // The started reset waits here for its database context, so it is still running below.
                await resetContexts.Entered.WaitAsync(waitLimit);
                CorruptionRemovalContractTests.AssertStartedOnce(tracker, answers, OperationType.DatabaseReset);
            }
            finally
            {
                foreach (var waiting in tracker.GetWaitingOperations().Where(operation => operation.Type == OperationType.DatabaseReset).ToList())
                    tracker.CancelOperation(waiting.Id);
                foreach (var reset in tracker.GetActiveOperations(OperationType.DatabaseReset).ToList())
                    tracker.CancelOperation(reset.Id);
                resetContexts.Release();
                for (var attempt = 0; attempt < 400 && tracker.GetActiveOperations(OperationType.DatabaseReset).Any(); attempt++)
                    await Task.Delay(25);
                if (fullReset)
                {
                    await cacheManager.ExecuteWithLockAsync(() => Task.FromResult(true))
                        .WaitAsync(waitLimit);
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class ResetContexts(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        private readonly TaskCompletionSource _entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public AppDbContext CreateDbContext() => new(options);

        public async Task<AppDbContext> CreateDbContextAsync(
            CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
            return CreateDbContext();
        }

        public void Release() => _release.TrySetResult();
    }
}
