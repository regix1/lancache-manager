using LancacheManager.Infrastructure.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public class LogRemovalProgressTests
{
    [Theory]
    [InlineData(0, 2, 0, 0)]
    [InlineData(0, 2, 100, 47.5)]
    [InlineData(1, 2, 0, 47.5)]
    [InlineData(1, 2, 100, 95)]
    public void MultiDatasourcePercentUsesMonotonicBands(
        int index,
        int count,
        double inner,
        double expected)
    {
        Assert.Equal(expected, RustLogRemovalService.ScaleIntoBand(inner, index, count, 95), 6);
    }

    [Fact]
    public void CountersAreCumulativeAcrossDatasources()
    {
        var cumulative = RustLogRemovalService.AddCumulativeCounters(
            completedFiles: 10,
            completedLines: 1_000,
            completedRemoved: 250,
            currentFiles: 3,
            currentLines: 400,
            currentRemoved: 100);

        Assert.Equal(13, cumulative.Files);
        Assert.Equal(1_400, cumulative.Lines);
        Assert.Equal(350, cumulative.Removed);
    }

    [Fact]
    public async Task ExternalCompletion_RejectsOldProgressAndPreservesTheNextRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-removal-terminal-" + Guid.NewGuid().ToString("N"));
        ServiceProvider? services = null;
        OperationStateService? repairOwner = null;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        Directory.CreateDirectory(Path.Combine(root, "cache"));
        Directory.CreateDirectory(Path.Combine(root, "GetOperationsDirectory"));
        try
        {
            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                ["LanCache:DataSources:0:LogPath"] = Path.Combine(root, "logs"),
                ["LanCache:DataSources:0:Enabled"] = "true"
            }).Build();
            var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
                NullLogger<UnifiedOperationTracker>.Instance);
            var notifications = DispatchProxy.Create<ISignalRNotificationService, RemovalMessages>();
            var messages = (RemovalMessages)(object)notifications;
            var retainedState = StateTestMethods.CreateStateService(root);
            retainedState.SetSetupCompleted(true);
            var datasourceService = new DatasourceService(
                configuration,
                paths,
                NullLogger<DatasourceService>.Instance);
            var logProcessor = (RustLogProcessorService)RuntimeHelpers.GetUninitializedObject(
                typeof(RustLogProcessorService));
            typeof(RustLogProcessorService)
                .GetField("_stateService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(logProcessor, retainedState);
            typeof(RustLogProcessorService)
                .GetField("_pathResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(logProcessor, paths);
            var cacheManager = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(
                typeof(CacheManagementService));
            typeof(CacheManagementService)
                .GetField("_pathResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cacheManager, paths);
            typeof(CacheManagementService)
                .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cacheManager, notifications);
            // The removal sends its started event while it holds the cache lock.
            typeof(CacheManagementService)
                .GetField("_cacheLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cacheManager, new SemaphoreSlim(1, 1));
            RustLogRemovalService? removal = null;
            services = new ServiceCollection()
                .AddSingleton(datasourceService)
                .AddSingleton(new DatasourceCapabilityService(datasourceService))
                .AddSingleton(notifications)
                .AddSingleton(logProcessor)
                .AddSingleton(cacheManager)
                .AddSingleton<RustLogRemovalService>(_ => removal!)
                .BuildServiceProvider();
            repairOwner = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                retainedState,
                services.GetRequiredService<IServiceScopeFactory>(),
                DispatchProxy.Create<IHostApplicationLifetime, NullReturningProxy>(),
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                tracker);
            await repairOwner.StartAsync(CancellationToken.None);
            removal = new RustLogRemovalService(NullLogger<RustLogRemovalService>.Instance,
                paths, notifications, cacheManager,
                new RustProcessHelper(NullLogger<RustProcessHelper>.Instance,
                    new ProcessManager(NullLogger<ProcessManager>.Instance), paths, tracker),
                null!, null!, datasourceService,
                tracker, null!, repairOwner);
            var sourceField = typeof(RustLogRemovalService).GetField("_cancellationTokenSource", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var start = typeof(RustLogRemovalService).GetMethod("StartRemovalForDatasourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var firstTask = (Task<bool>)start.Invoke(removal, ["steam", "default"])!;
            var first = await messages.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            var firstRegisteredSource = tracker.GetOperation(first)!.CancellationTokenSource!;
            using var firstCleanupSource = new DisposeTrackingCancellationTokenSource();
            sourceField.SetValue(removal, firstCleanupSource);
            firstRegisteredSource.Cancel();
            tracker.ForceKillOperation(first);
            tracker.CompleteOperation(first, false, cancelled: true);
            Assert.Throws<ObjectDisposedException>(() => _ = firstRegisteredSource.Token);
            Assert.Equal(0, firstCleanupSource.DisposeCalls);
            // The next removal registers at once but waits for the cache lock the first one still holds.
            var nextTask = (Task<bool>)start.Invoke(removal, ["epicgames", "default"])!;
            var next = removal.CurrentOperationId!.Value;
            Assert.NotEqual(first, next);
            var nextRegisteredSource = tracker.GetOperation(next)!.CancellationTokenSource!;
            using var nextCleanupSource = new DisposeTrackingCancellationTokenSource();
            sourceField.SetValue(removal, nextCleanupSource);
            messages.Release[first].TrySetResult();
            Assert.False(await firstTask.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(next, await messages.Started.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.Equal(next, removal.CurrentOperationId);
            Assert.True(removal.IsProcessing);
            Assert.Equal("epicgames", removal.CurrentService);
            Assert.Equal(0, removal.GetRemovalStatus().LinesRemoved);
            var complete = Assert.Single(messages.Completions);
            Assert.Equal(first, complete.OperationId);
            Assert.Equal("steam", complete.Service);
            Assert.True(complete.Cancelled);
            Assert.DoesNotContain(messages.Progress, id => id == first);

            nextRegisteredSource.Cancel();
            tracker.ForceKillOperation(next);
            tracker.CompleteOperation(next, false, cancelled: true);
            Assert.Throws<ObjectDisposedException>(() => _ = nextRegisteredSource.Token);
            Assert.Equal(0, nextCleanupSource.DisposeCalls);
            messages.Release[next].TrySetResult();
            Assert.False(await nextTask.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            if (repairOwner is not null)
            {
                await repairOwner.StopAsync(CancellationToken.None);
            }
            services?.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LogStep_HoldsTheLogLockAndMarksItsSourceAroundTheChildAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();

        Assert.True(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.True(harness.Rust.LockHeldAtLaunch);
        Assert.True(harness.Rust.SourceAtLaunch!.LogRewriteStarted);
        Assert.False(harness.Rust.SourceAtLaunch.LogPositionsKept);
        var source = Assert.Single(harness.ReadRepair(harness.Removal.CurrentOperationId!.Value).Sources);
        Assert.True(source.LogPositionsKept);
        await harness.Owner.WaitForLogStepAsync(active: false, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task LogStep_SendsServiceCountsChangedAfterTheRewriteAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();

        Assert.True(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var sent = Assert.Single(
            harness.CountsNotifications.Invocations,
            call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync));
        Assert.Equal(SignalREvents.ServiceCountsChanged, sent.Args[0]);
    }

    [Fact]
    public async Task LogStep_CancelWhileWaitingForTheLogLockIsRecordedAsACancelAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation => terminal.TrySetResult(operation);
        var holderId = harness.Tracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database Reset",
            new CancellationTokenSource());
        Guid operationId;
        await using (await harness.Owner.LockLogFilesAsync(
            holderId,
            OperationType.DatabaseReset,
            LogFileLockKind.Rows,
            CancellationToken.None))
        {
            var run = harness.RunRemovalAsync();
            operationId = harness.Removal.CurrentOperationId!.Value;
            for (var attempt = 0; harness.Tracker.GetOperation(operationId)?.BlockedByName != "Database Reset"; attempt++)
            {
                Assert.True(attempt < 400, "The removal never waited for the log lock.");
                await Task.Delay(25);
            }

            Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
            Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        Assert.Equal(OperationStatus.Cancelled, (await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Empty(harness.Rust.Launches);
        var repair = await harness.WaitForOutcomeAsync(operationId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        Assert.Null(repair.Error);
        Assert.False(Assert.Single(repair.Sources).LogRewriteStarted);
    }

    [Fact]
    public async Task CancelDuringTheRewriteIsStoredAsACancelAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        harness.Rust.HeldLaunches = 1;
        var run = harness.RunRemovalAsync();
        var operationId = harness.Removal.CurrentOperationId!.Value;
        for (var attempt = 0; harness.Rust.Launches.IsEmpty; attempt++)
        {
            Assert.True(attempt < 400, "The removal never launched its child.");
            await Task.Delay(25);
        }

        Assert.Equal(OperationCancelResult.Requested, harness.Tracker.CancelOperation(operationId));
        Assert.False(await run.WaitAsync(TimeSpan.FromSeconds(10)));

        var repair = await harness.WaitForOutcomeAsync(operationId);
        Assert.Equal(OperationStatus.Cancelled, repair.Outcome);
        Assert.Null(repair.Error);
        var source = Assert.Single(repair.Sources);
        Assert.True(source.LogRewriteStarted);
        Assert.False(source.LogPositionsKept);
    }

    [Fact]
    public async Task Repair_RunsAnUnfinishedLogStepAgainWithoutTheCacheLockAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        var repairId = Guid.NewGuid();
        await harness.Owner.PrepareRepairAsync(
            new OperationRepair
            {
                Id = repairId,
                Type = OperationType.LogRemoval,
                Name = "Log Removal",
                StartedAt = DateTime.UtcNow,
                LogRemoval = new LogRemovalRepair { Service = "steam", Datasource = "default" },
                Sources =
                [
                    new OperationRepairSource
                    {
                        Datasource = "default",
                        LogRoot = harness.LogPath,
                        ResetLogPositions = true,
                        RefreshDownloads = true
                    }
                ]
            },
            CancellationToken.None);
        await harness.Owner.StartWorkAsync(repairId, "default", CancellationToken.None);
        // A force stop inside the rewrite leaves the source started with its positions not kept.
        await harness.Owner.MarkLogRewriteStartedAsync(repairId, "default");
        var repair = Assert.Single(harness.Owner.GetPendingRepairs(), pending => pending.Id == repairId);

        var cacheHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCache = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cacheHolder = harness.Cache.ExecuteWithLockAsync(async () =>
        {
            cacheHeld.TrySetResult();
            await releaseCache.Task;
            return true;
        });
        await cacheHeld.Task;
        try
        {
            await harness.Removal.ResumeRepairAsync(repair, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseCache.TrySetResult();
            await cacheHolder;
        }

        var launch = Assert.Single(harness.Rust.Launches);
        Assert.StartsWith($"remove \"{harness.LogPath}\" \"steam\" ", launch, StringComparison.Ordinal);
        Assert.True(Assert.Single(harness.ReadRepair(repairId).Sources).LogPositionsKept);
    }

    [Fact]
    public async Task LogRemoval_FailedOutcomeSaveStillEndsTheRunAndLandsLaterAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        harness.State.FailRepairStarts = 1;
        var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation => terminal.TrySetResult(operation);

        Assert.True(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Completed, ended.Status);
        Assert.Equal(0, harness.State.FailRepairStarts);
        var repair = await harness.WaitForOutcomeAsync(ended.Id);
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        Assert.Equal((1, 3L), (repair.LogRemoval!.FilesProcessed, repair.LogRemoval.LinesProcessed));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(137, false)]
    public async Task LogRemoval_AFailedChildNamesTheServiceAndDatasourceAsync(int exitCode, bool wroteReason)
    {
        // log_service_manager's reason for a log file it could not modify (remove_service_from_logs), behind
        // the context its run adds.
        const string reason = "Service removal failed: FAILED: 1 log file(s) could not be modified due to permission errors. This is likely caused by incorrect PUID/PGID settings. The lancache container is configured to run as UID/GID 1000:1000. Please check your docker-compose.yml and ensure PUID and PGID match the cache file ownership.";
        await using var harness = await LogStepHarness.CreateAsync();
        // Exit 1 after a permission failure, or 137 for a child the kernel killed before it wrote anything.
        harness.Rust.ExitCode = exitCode;
        harness.Rust.FailureMessage = wroteReason ? reason : null;
        var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation => terminal.TrySetResult(operation);

        Assert.False(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Failed, ended.Status);
        var repair = await harness.WaitForOutcomeAsync(ended.Id);
        Assert.Equal(
            wroteReason
                ? $"Failed to remove steam entries from default (exit code {exitCode}): {reason}"
                : $"Failed to remove steam entries from default (exit code {exitCode})",
            repair.Error);
    }

    [Fact]
    public async Task LogRemoval_ADatasourceWithNoLogFileEndsCleanlyAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        File.Delete(Path.Combine(harness.LogPath, "access.log"));
        var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation => terminal.TrySetResult(operation);

        Assert.True(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Completed, ended.Status);
        var repair = await harness.WaitForOutcomeAsync(ended.Id);
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        Assert.True(Assert.Single(repair.Sources).LogPositionsKept);
        // The child still gets a check, one that binds no file, so it would refuse a log file that appeared.
        Assert.Equal(0, harness.Rust.CheckedFilesAtLaunch);
    }

    [Fact]
    public async Task LogRemoval_AnotherServiceLogDeletedOutsideTheAppEndsAsAWarningAndIsReadAgainAsync()
    {
        await using var harness = await LogStepHarness.CreateAsync();
        // A bare-metal folder: the removed service, the service whose log disappears, and one left alone.
        File.Delete(Path.Combine(harness.LogPath, "access.log"));
        await File.WriteAllTextAsync(Path.Combine(harness.LogPath, "steam-access.log"), "s1\n");
        await File.WriteAllTextAsync(Path.Combine(harness.LogPath, "blizzard-access.log"), "b1\n");
        await File.WriteAllTextAsync(Path.Combine(harness.LogPath, "epicgames-access.log"), "e1\ne2\n");
        harness.State.SetLogSourcePositions("default", new Dictionary<string, long>
        {
            ["steam-access.log"] = 1,
            ["blizzard-access.log"] = 1,
            ["epicgames-access.log"] = 2
        });
        harness.Rust.GoneAtLaunch = "blizzard-access.log";
        var terminal = new TaskCompletionSource<OperationInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Tracker.OperationTerminal += operation => terminal.TrySetResult(operation);

        Assert.True(await harness.RunRemovalAsync().WaitAsync(TimeSpan.FromSeconds(10)));

        var ended = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(OperationStatus.Completed, ended.Status);
        // The amber card names the other log and stays until someone closes it.
        var row = Assert.Single(harness.Tracker.GetRuns().Runs, run => run.OperationId == ended.Id);
        var warning = Assert.Single(row.Warnings);
        Assert.Equal("signalr.logRemoval.otherLogsGone", warning.StageKey);
        Assert.Equal("blizzard-access.log", warning.Context["fileNames"]);
        Assert.True(row.Retained);
        // The removed series and the vanished one start again at line 1; the untouched one keeps its place.
        Assert.Equal(
            new Dictionary<string, long> { ["epicgames-access.log"] = 2 },
            harness.State.GetLogSourcePositions("default"));
        var repair = await harness.WaitForOutcomeAsync(ended.Id);
        Assert.Equal(OperationStatus.Completed, repair.Outcome);
        Assert.True(Assert.Single(repair.Sources).LogPositionsKept);
    }

    /// <summary>
    /// A per-datasource log removal against a real repair owner, log lock and nginx reopen check,
    /// with a recording child in place of log_service_manager.
    /// </summary>
    private sealed class LogStepHarness : IAsyncDisposable
    {
        private readonly string _root;
        private readonly OperationRepairTests.RepairHarness _repairs;

        private LogStepHarness(
            string root,
            string logPath,
            OperationRepairTests.RepairHarness repairs,
            OperationRepairTests.FailingStateService state,
            StepRustProcessHelper rust,
            CacheManagementService cache,
            RecordingNotificationProxy countsNotifications,
            RustLogRemovalService removal)
        {
            _root = root;
            LogPath = logPath;
            _repairs = repairs;
            State = state;
            Rust = rust;
            Cache = cache;
            CountsNotifications = countsNotifications;
            Removal = removal;
        }

        public string LogPath { get; }
        public OperationStateService Owner => _repairs.Owner;
        public UnifiedOperationTracker Tracker => _repairs.Tracker;
        public OperationRepairTests.FailingStateService State { get; }
        public StepRustProcessHelper Rust { get; }
        public CacheManagementService Cache { get; }
        public RecordingNotificationProxy CountsNotifications { get; }
        public RustLogRemovalService Removal { get; }

        public static async Task<LogStepHarness> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "log-removal-step-" + Guid.NewGuid().ToString("N"));
            var logs = Path.Combine(root, "logs");
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(Path.Combine(root, "cache"));
            await File.WriteAllTextAsync(Path.Combine(logs, "access.log"), "line\n");

            var state = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "state"));
            // Outcome retries run at once instead of a minute later.
            var repairs = await OperationRepairTests.RepairHarness.CreateAsync(
                Path.Combine(root, "repair"),
                stateService: state,
                waitUntil: (_, _) => Task.CompletedTask);

            var paths = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
            ((PathResolverProxy)(object)paths).Root = root;
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "default",
                ["LanCache:DataSources:0:CachePath"] = Path.Combine(root, "cache"),
                ["LanCache:DataSources:0:LogPath"] = logs,
                ["LanCache:DataSources:0:Enabled"] = "true"
            }).Build();
            var datasources = new DatasourceService(
                configuration,
                paths,
                NullLogger<DatasourceService>.Instance);
            var rust = new StepRustProcessHelper(paths, repairs.Tracker, repairs.Owner);
            var cache = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(
                typeof(CacheManagementService));
            typeof(CacheManagementService)
                .GetField("_cacheLock", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cache, new SemaphoreSlim(1, 1));
            // The step refreshes the service counts, which reads the paths and sends one notification.
            typeof(CacheManagementService)
                .GetField("_pathResolver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cache, paths);
            var countsNotifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
            typeof(CacheManagementService)
                .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(cache, countsNotifications);
            var removal = new RustLogRemovalService(
                NullLogger<RustLogRemovalService>.Instance,
                paths,
                DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                cache,
                rust,
                new NginxLogRotationService(
                    NullLogger<NginxLogRotationService>.Instance,
                    configuration,
                    new ProcessManager(NullLogger<ProcessManager>.Instance),
                    paths),
                null!,
                datasources,
                repairs.Tracker,
                state,
                repairs.Owner);
            return new LogStepHarness(
                root,
                datasources.GetDatasource("default")!.LogPath,
                repairs,
                state,
                rust,
                cache,
                (RecordingNotificationProxy)(object)countsNotifications,
                removal);
        }

        public Task<bool> RunRemovalAsync() => (Task<bool>)typeof(RustLogRemovalService)
            .GetMethod("StartRemovalForDatasourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Removal, ["steam", "default"])!;

        public OperationRepair ReadRepair(Guid operationId) =>
            State.LoadOperationRepairs().Single(repair => repair.Id == operationId);

        public async Task<OperationRepair> WaitForOutcomeAsync(Guid operationId)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var repair = ReadRepair(operationId);
                if (repair.Outcome.HasValue)
                {
                    return repair;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"Log removal repair {operationId} never stored its outcome.");
        }

        public async ValueTask DisposeAsync()
        {
            await _repairs.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>
    /// Stands in for log_service_manager: records what the step holds at launch and writes a
    /// finished progress file.
    /// </summary>
    private sealed class StepRustProcessHelper(
        IPathResolver paths,
        IUnifiedOperationTracker tracker,
        OperationStateService owner)
        : RustProcessHelper(
            NullLogger<RustProcessHelper>.Instance,
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            paths,
            tracker)
    {
        public ConcurrentQueue<string> Launches { get; } = new();
        public bool LockHeldAtLaunch { get; private set; }
        public OperationRepairSource? SourceAtLaunch { get; private set; }

        /// <summary>The first this many launches run until the cancel and write nothing, as a killed child does.</summary>
        public int HeldLaunches { get; set; }

        /// <summary>The exit code each launch returns.</summary>
        public int ExitCode { get; set; }

        /// <summary>
        /// The reason a failing launch writes into its progress file before it exits, as log_service_manager
        /// does; null for a child killed before it wrote anything.
        /// </summary>
        public string? FailureMessage { get; set; }

        /// <summary>How many log files the publication check attached at launch binds; null when none is attached.</summary>
        public int? CheckedFilesAtLaunch { get; private set; }

        /// <summary>
        /// A log of another service that something outside the app deletes while the child runs; null
        /// when nothing outside the app touches the folder.
        /// </summary>
        public string? GoneAtLaunch { get; set; }

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            Launches.Enqueue(start.Arguments);
            LockHeldAtLaunch = await RemovalRepairHarness.StepHeldAsync(owner);
            SourceAtLaunch = owner.GetPendingRepairs()
                .Single(repair => repair.Id == operationId)
                .Sources
                .Single();
            var check = start.Environment.TryGetValue("LANCACHE_LOG_CHECK", out var checkPath) && checkPath is not null
                ? JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                    await File.ReadAllTextAsync(checkPath, cancellationToken),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))
                : null;
            CheckedFilesAtLaunch = check?.Files.Count;
            if (Launches.Count <= HeldLaunches)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            // The quoted arguments are the log directory, the service and the progress file.
            var arguments = start.Arguments.Split('"');
            var progressPath = arguments[5];
            // The real path resolver creates the operations directory each time it returns it; this one does not.
            Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
            if (ExitCode == 0 && GoneAtLaunch is not null)
            {
                // remove_service_from_logs on a folder holding steam-access.log: it deletes that file, and
                // publish_deleted_files records it deleted and every other checked file unchanged, the one
                // deleted outside the app included, without failing the publication. Then the final progress.
                File.Delete(Path.Combine(arguments[1], GoneAtLaunch));
                File.Delete(Path.Combine(arguments[1], "steam-access.log"));
                await File.WriteAllTextAsync(
                    start.Environment["LANCACHE_LOG_RESULT"]!,
                    JsonSerializer.Serialize(
                        new NginxPublicationResult(
                            true,
                            check!.Files.Select(file => Path.GetFileName(file.TargetPath) == "steam-access.log"
                                ? new NginxPublicationRecord(
                                    file.TargetPath,
                                    file.OriginalIdentity,
                                    null,
                                    null,
                                    Changed: true,
                                    Deleted: true)
                                : new NginxPublicationRecord(
                                    file.TargetPath,
                                    file.OriginalIdentity,
                                    null,
                                    file.OriginalIdentity,
                                    Changed: false,
                                    Deleted: false)).ToList()),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    cancellationToken);
                await File.WriteAllTextAsync(
                    progressPath,
                    JsonSerializer.Serialize(new LogRemovalProgress
                    {
                        PercentComplete = 100,
                        Status = "completed",
                        StageKey = "signalr.logRemoval.complete",
                        Message = "Removed 1 steam entries from 1 total lines across 1 files in 0.00s",
                        FilesProcessed = 1,
                        LinesProcessed = 1,
                        LinesRemoved = 1
                    }),
                    cancellationToken);
            }
            else if (ExitCode == 0)
            {
                // With no log file the child reports that and changes nothing (log_service_manager.rs:1137-1156).
                var noLogFile = !Directory.EnumerateFiles(arguments[1]).Any();
                await File.WriteAllTextAsync(
                    progressPath,
                    JsonSerializer.Serialize(noLogFile
                        ? new LogRemovalProgress
                        {
                            PercentComplete = 100,
                            Status = "completed",
                            StageKey = "signalr.logRemoval.completeNoFiles",
                            Message = "No log files found"
                        }
                        : new LogRemovalProgress
                        {
                            PercentComplete = 100,
                            Status = "completed",
                            StageKey = "signalr.logRemoval.complete",
                            FilesProcessed = 1,
                            LinesProcessed = 3
                        }),
                    cancellationToken);
            }
            else if (FailureMessage is not null)
            {
                // A log file it could not modify: remove_all_log_entries_for_service publishes success false
                // with no record for that file, publish_deleted_files adds an unchanged record for every checked
                // file still missing one, and run replaces the progress with the reason (write_error_progress)
                // before the child exits 1.
                await File.WriteAllTextAsync(
                    start.Environment["LANCACHE_LOG_RESULT"]!,
                    JsonSerializer.Serialize(
                        new NginxPublicationResult(
                            false,
                            check!.Files.Select(file => new NginxPublicationRecord(
                                file.TargetPath,
                                file.OriginalIdentity,
                                null,
                                file.OriginalIdentity,
                                Changed: false,
                                Deleted: false)).ToList()),
                        new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                    cancellationToken);
                await File.WriteAllTextAsync(
                    progressPath,
                    JsonSerializer.Serialize(new LogRemovalProgress { Status = "error", Message = FailureMessage }),
                    cancellationToken);
            }

            return new ProcessExecutionResult { ExitCode = ExitCode };
        }
    }

    public class RemovalMessages : DispatchProxy
    {
        public System.Threading.Channels.Channel<Guid> Started { get; } = System.Threading.Channels.Channel.CreateUnbounded<Guid>();
        public ConcurrentDictionary<Guid, TaskCompletionSource> Release { get; } = new();
        public ConcurrentQueue<SignalRNotifications.LogRemovalComplete> Completions { get; } = new();
        public ConcurrentQueue<Guid> Progress { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                var eventName = (string)args![0]!;
                var value = args[1]!;
                if (value is SignalRNotifications.LogRemovalComplete completion)
                    Completions.Enqueue(completion);
                if (eventName == "LogRemovalStarted")
                {
                    var id = (Guid)value.GetType().GetProperty("OperationId")!.GetValue(value)!;
                    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    Release[id] = release;
                    Started.Writer.TryWrite(id);
                    return release.Task;
                }
                if (eventName == "LogRemovalProgress")
                    Progress.Enqueue((Guid)value.GetType().GetProperty("OperationId")!.GetValue(value)!);
                return Task.CompletedTask;
            }
            return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }

    private sealed class DisposeTrackingCancellationTokenSource : CancellationTokenSource
    {
        public int DisposeCalls { get; private set; }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
            }

            base.Dispose(disposing);
        }
    }
}
