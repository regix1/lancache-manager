using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A detection's notice is decided per run by the caller and must travel with THAT run's operation,
/// never through a service-wide field. A silent scheduled run and a manual visible start can
/// overlap: the second call is rejected as "already active" (or triggers stale cleanup of the
/// first), and neither may give the running run the other run's notice. A run that was running when
/// the server stopped comes back with the notice it was saved with, including a state saved by the
/// previous version, which carried display flags instead.
/// </summary>
public class GameDetectionVisibilityRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoringPersistedRunsPreservesIdentityAndCancelsOrphanedChildren(bool child)
    {
        using var ctx = new ServiceContext();
        var id = Guid.NewGuid();
        Guid? parent = child ? Guid.NewGuid() : null;
        var startedAt = DateTime.UtcNow.AddMinutes(-2);
        ctx.States.SaveState($"gameDetection_{id}", new LancacheManager.Core.Services.OperationState
        {
            Key = $"gameDetection_{id}", Type = OperationType.GameDetection.ToWireString(), Status = "running",
            Fields = JsonSerializer.SerializeToElement(new
            {
                operationId = id, parentOperationId = parent, startedAt,
                scanType = DetectionScanType.Full, showNotification = false
            })
        });
        await ctx.Service.RestoreInterruptedOperations(CancellationToken.None);
        var notice = ctx.Tracker.NoticeOf(id)!;
        if (child)
        {
            Assert.Null(ctx.Tracker.Get(id));
            var complete = Assert.IsType<SignalRNotifications.GameDetectionComplete>(Assert.Single(ctx.Notifications.Events).Value);
            Assert.Equal(parent, complete.ParentOperationId);
            Assert.True(complete.Cancelled);
            // A restored child phase stays hidden, as the live one is.
            Assert.Equal(NotificationMode.Hidden, notice.Mode);
            Assert.Equal(RunTrigger.Manual, notice.Trigger);
            Assert.Equal("cancelled", ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString())).Status);
        }
        else
        {
            var operation = ctx.Tracker.Get(id)!;
            Assert.Equal(startedAt, operation.StartedAt);
            var metrics = Assert.IsType<GameDetectionMetrics>(operation.Metadata);
            Assert.Equal(DetectionScanType.Full, metrics.ScanType);
            Assert.Equal(NotificationMode.Silent, notice.Mode);
            Assert.Equal(RunTrigger.Scheduled, notice.Trigger);
            Assert.Null(operation.ParentOperationId);
        }
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, RunVisibility.Background)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    public async Task ARunningDetectionRestoresWithTheNoticeItWasSavedWith(NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new ServiceContext();
        var id = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        ctx.States.SaveState($"gameDetection_{id}", new LancacheManager.Core.Services.OperationState
        {
            Key = $"gameDetection_{id}",
            Type = OperationType.GameDetection.ToWireString(),
            Status = "running",
            Fields = JsonSerializer.SerializeToElement(new
            {
                operationId = id,
                startedAt,
                scanType = DetectionScanType.Incremental,
                notificationMode = mode,
                trigger
            })
        });
        Assert.Equal("running", ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString())).Status);

        // A second service over the same saved states performs the explicit startup restoration.
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await ctx.Restart(tracker);

        var restored = tracker.GetOperation(id)!.Notice!;
        Assert.Equal(mode, restored.Mode);
        Assert.Equal(trigger, restored.Trigger);
        Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id).Visibility);
    }

    [Theory]
    [InlineData("hidden", NotificationMode.Hidden, RunTrigger.Manual, RunVisibility.Hidden)]
    [InlineData("shown", NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData("silent", NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData("none", NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    public async Task ARunningDetectionSavedByThePreviousVersionRestoresWithTheSameLook(
        string saved, NotificationMode mode, RunTrigger trigger, RunVisibility expected)
    {
        using var ctx = new ServiceContext();
        var id = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        object data = saved switch
        {
            "hidden" => new { operationId = id, startedAt, scanType = DetectionScanType.Full, showNotification = false, hideNotification = true },
            "shown" => new { operationId = id, startedAt, scanType = DetectionScanType.Full, showNotification = true, hideNotification = false },
            "silent" => new { operationId = id, startedAt, scanType = DetectionScanType.Full, showNotification = false, hideNotification = false },
            _ => new { operationId = id, startedAt, scanType = DetectionScanType.Full }
        };
        ctx.States.SaveState($"gameDetection_{id}", new LancacheManager.Core.Services.OperationState
        {
            Key = $"gameDetection_{id}", Type = OperationType.GameDetection.ToWireString(), Status = "running",
            Fields = JsonSerializer.SerializeToElement(data)
        });
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);

        await ctx.Restart(tracker);

        var restored = tracker.GetOperation(id)!.Notice!;
        Assert.Equal(mode, restored.Mode);
        Assert.Equal(trigger, restored.Trigger);
        Assert.Equal(expected, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == id).Visibility);
    }

    [Fact]
    public async Task CompletedRepairOwnershipPreventsLegacyRestart()
    {
        using var ctx = new ServiceContext();
        var id = Guid.NewGuid();
        var startedAt = DateTime.UtcNow.AddMinutes(-1);
        var notice = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await ctx.States.PrepareRepairAsync(
            new OperationRepair
            {
                Id = id,
                Type = OperationType.GameDetection,
                Name = "Game Detection",
                StartedAt = startedAt,
                Notice = notice,
                GameDetection = new GameDetectionMetrics
                {
                    StartTime = startedAt,
                    ScanType = DetectionScanType.Full
                }
            },
            CancellationToken.None);
        await ctx.States.FinishRepairAsync(
            id,
            success: true,
            cancelled: false,
            error: null);
        ctx.States.SaveState($"gameDetection_{id}", new LancacheManager.Core.Services.OperationState
        {
            Key = $"gameDetection_{id}",
            Type = OperationType.GameDetection.ToWireString(),
            Status = OperationStatus.Running.ToWireString(),
            Fields = JsonSerializer.SerializeToElement(new
            {
                operationId = id,
                startedAt,
                scanType = DetectionScanType.Full,
                notificationMode = notice.Mode,
                trigger = notice.Trigger
            })
        });

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        await ctx.Restart(tracker);

        Assert.True(ctx.States.OwnsRepair(id));
        Assert.Null(tracker.GetOperation(id));
    }

    [Fact]
    public async Task ParentIdentityTravelsThroughStartedRecoveryAndWinningTerminal()
    {
        using var ctx = new ServiceContext();
        var parent = Guid.NewGuid();
        var id = (await ctx.Service.StartDetectionAsync(new RunNotice(NotificationMode.Hidden, RunTrigger.Manual),
            incremental: false, parentOperationId: parent))!.Value;
        var active = ctx.Service.GetActiveOperation()!;
        Assert.Equal(parent, active.ParentOperationId);
        Assert.Equal(DetectionScanType.Full, active.ScanType);
        Assert.Equal(parent, ctx.Tracker.Get(id)!.ParentOperationId);
        var started = JsonSerializer.SerializeToElement(ctx.Notifications.Events.Single(e => e.Event == SignalREvents.GameDetectionStarted).Value);
        Assert.Equal(parent, started.GetProperty("ParentOperationId").GetGuid());
        var running = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString()));
        Assert.Equal(parent, running.Fields!.Value.GetProperty("parentOperationId").GetGuid());
        Assert.Equal("hidden", running.Fields.Value.GetProperty("notificationMode").GetString());
        Assert.Equal((int)RunTrigger.Manual, running.Fields.Value.GetProperty("trigger").GetInt32());
        Assert.Equal(active.StartTime, running.Fields.Value.GetProperty("startedAt").GetDateTime());
        ctx.Tracker.FireTerminal(id, success: false, cancelled: true, error: "Cancelled by user");
        var complete = Assert.IsType<SignalRNotifications.GameDetectionComplete>(ctx.Notifications.Events.Single(e => e.Event == SignalREvents.GameDetectionComplete).Value);
        Assert.Equal(parent, complete.ParentOperationId);
        Assert.True(complete.Cancelled);
        var persisted = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString()));
        Assert.Equal("cancelled", persisted.Status);
        Assert.Equal(parent, persisted.Fields!.Value.GetProperty("parentOperationId").GetGuid());
    }

    [Fact]
    public async Task LosingFinalizerCannotChangeTerminalPersistenceOrClearANewerRun()
    {
        using var ctx = new ServiceContext();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        typeof(GameCacheDetectionService).GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ctx.Service, tracker);
        var started = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.Notifications.OnSend = (name, value) =>
        {
            if (name != SignalREvents.GameDetectionStarted) return Task.CompletedTask;
            started.TrySetResult((Guid)value!.GetType().GetProperty("OperationId")!.GetValue(value)!);
            return release.Task;
        };
        var start = ctx.Service.StartDetectionAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));
        var oldId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = tracker.GetOperation(oldId)!;
        tracker.CompleteOperation(oldId, success: false, error: "Cancelled by user", cancelled: true);
        var persisted = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(oldId.ToString())).Fields!.Value.GetRawText();
        release.TrySetResult();
        await start;

        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStart = ctx.Service.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
        var nextId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finalize = typeof(GameCacheDetectionService).GetMethod("FinalizeDetectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)finalize.Invoke(ctx.Service, [oldId, true, OperationStatus.Completed, "signalr.gameDetect.complete.full", false,
            new Dictionary<string, object?> { ["newGamesCount"] = 999 }, 999, 999])!;
        Assert.Equal(OperationStatus.Cancelled, old.Status);
        Assert.Null(((GameDetectionMetrics)old.Metadata!).CompletionContext);
        Assert.Equal(persisted, ctx.States.GetAllStates().Single(s => s.Key.EndsWith(oldId.ToString())).Fields!.Value.GetRawText());
        Assert.Equal(nextId, typeof(GameCacheDetectionService).GetField("_currentTrackerOperationId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.Service));
        tracker.CompleteOperation(nextId, success: false, cancelled: true);
        release.TrySetResult();
        await nextStart;
    }

    [Fact]
    public async Task SecondStart_DuringActiveRun_DoesNotAlterActiveRunNotice()
    {
        using var ctx = new ServiceContext();

        // A silent (scheduled) run claims the operation.
        var silent = new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled);
        var firstId = await ctx.Service.StartDetectionAsync(silent, incremental: true);
        Assert.NotNull(firstId);
        // The harness's own worker returns at once (the fake tracker has no operation for it), which
        // a new start reads as a detection nothing will finish. Record one that is still running.
        typeof(GameCacheDetectionService).GetField("_currentDetectionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ctx.Service, ((Guid, Task)?)(firstId!.Value, new TaskCompletionSource().Task));

        // A visible (manual) start arrives while the silent run is active - it is rejected.
        var secondId = await ctx.Service.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: true);
        Assert.Null(secondId);

        // The active silent run still ends under ITS OWN notice, not the rejected attempt's.
        ctx.Tracker.FireTerminal(firstId!.Value, success: true, cancelled: false, error: null);

        Assert.True(ctx.Notifications.Completed(firstId.Value));
        Assert.Same(silent, ctx.Tracker.NoticeOf(firstId.Value));
    }

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Manual)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled)]
    [InlineData(NotificationMode.Silent, RunTrigger.RunAll)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual)]
    public async Task ADetectionRegistersWithTheNoticeItWasStartedWith(NotificationMode mode, RunTrigger trigger)
    {
        using var ctx = new ServiceContext();
        var notice = new RunNotice(mode, trigger);

        var id = await ctx.Service.StartDetectionAsync(notice, incremental: true);

        Assert.Same(notice, ctx.Tracker.NoticeOf(id!.Value));
    }

    [Fact]
    public async Task StaleCleanupTerminal_CarriesStaleRunNotice_NotNewAttemptNotice()
    {
        using var ctx = new ServiceContext();

        // A silent run is registered, then its worker exits while the tracker row remains active.
        var stale = new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled);
        var staleId = await ctx.Service.StartDetectionAsync(stale, incremental: true);
        Assert.NotNull(staleId);
        var current = ((Guid, Task)?)typeof(GameCacheDetectionService)
            .GetField("_currentDetectionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(ctx.Service);
        Assert.NotNull(current);
        Assert.Equal(staleId!.Value, current.Value.Item1);
        await current.Value.Item2;

        // A visible manual start arrives; its stale-cleanup pass completes the abandoned silent run.
        var visible = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var newId = await ctx.Service.StartDetectionAsync(visible, incremental: true);
        Assert.NotNull(newId);

        // The stale run ends under its OWN notice; the new attempt's notice goes only to the new run.
        Assert.True(ctx.Notifications.Completed(staleId.Value));
        Assert.Same(stale, ctx.Tracker.NoticeOf(staleId.Value));
        Assert.Same(visible, ctx.Tracker.NoticeOf(newId!.Value));
    }

    [Fact]
    public async Task ADetectionCancelledBeforeItsSaveEndsAtOnceAndKeepsNothingAsync()
    {
        await using var run = await DetectionRun.CreateAsync();
        // Another job's repair is still running; the cancel handler must not wait for it.
        var blocker = new OperationRepair
        {
            Id = Guid.NewGuid(),
            Type = OperationType.GameRemoval,
            Name = "Game removal",
            StartedAt = DateTime.UtcNow,
            Sources = [new OperationRepairSource { Datasource = "default" }],
            Target = new CacheRepairTarget { SteamAppId = 480 },
            Removal = new RemovalRepair { EntityKey = "480", EntityName = "Game", EntityKind = "steam" }
        };
        run.BlockedRepair = blocker.Id;
        await run.Repairs.Owner.PrepareRepairAsync(blocker, CancellationToken.None);
        await run.Repairs.Owner.StartWorkAsync(blocker.Id, "default", CancellationToken.None);
        await run.Repairs.Owner.FinishRepairAsync(blocker.Id, false, true, null);

        var id = (await run.Detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false))!.Value;

        // The run waits for the repair before its first write, so it has not walked the cache.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.False(run.Repairs.Tracker.GetOperation(id)!.Status.IsTerminal());
        Assert.False(File.Exists(Path.Combine(run.OperationsDirectory, $"game_detection_{id}_default.json")));

        run.Repairs.Tracker.CancelOperation(id);
        var terminal = await run.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        var stored = run.Repairs.StateService.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.Equal(OperationRepairPhase.Completed, stored.Phase);
        Assert.Equal(OperationStatus.Cancelled, stored.Outcome);
        Assert.False(stored.DatabaseWriteStarted);
        await using var context = run.CreateContext();
        Assert.Equal(0, await context.CachedGameDetections.CountAsync());
    }

    [Fact]
    public async Task ADetectionCancelledWhileSavingKeepsItsPartialResultsAsync()
    {
        await using var run = await DetectionRun.CreateAsync();
        run.CancelAtStage = "signalr.gameDetect.db.complete";
        run.CancelOnFirstSave = true;

        var id = (await run.Detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false))!.Value;
        var terminal = await run.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(OperationStatus.Cancelled, terminal.Status);
        Assert.True(run.Repairs.StateService.LoadOperationRepairs().Single(repair => repair.Id == id).DatabaseWriteStarted);
        await using var context = run.CreateContext();
        Assert.Equal(570, Assert.Single(await context.CachedGameDetections.ToListAsync()).GameAppId);
    }

    [Fact]
    public async Task ASuccessfulDetectionSendsItsCompletionStageKeyAsync()
    {
        await using var run = await DetectionRun.CreateAsync();

        var id = (await run.Detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual), incremental: false))!.Value;
        await run.WaitForTerminalAsync(id).WaitAsync(TimeSpan.FromSeconds(5));

        var complete = Assert.IsType<SignalRNotifications.GameDetectionComplete>(
            Assert.Single(run.Notifications.Events, e => e.Event == SignalREvents.GameDetectionComplete).Value);
        Assert.True(complete.Success);
        Assert.StartsWith("signalr.gameDetect.complete", complete.StageKey);
        Assert.Equal(1, complete.TotalGamesDetected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFailedOutcomeSaveStillEndsTheDetectionAsync(bool cancelled)
    {
        await using var run = await DetectionRun.CreateAsync();
        var startedAt = DateTime.UtcNow;
        var id = run.Repairs.Tracker.RegisterOperation(
            OperationType.GameDetection,
            "Game Detection",
            new CancellationTokenSource(),
            new GameDetectionMetrics { StartTime = startedAt, ScanType = DetectionScanType.Full },
            ownerCompletes: true);
        await run.Repairs.Owner.PrepareRepairAsync(
            new OperationRepair
            {
                Id = id,
                Type = OperationType.GameDetection,
                Name = "Game Detection",
                StartedAt = startedAt,
                GameDetection = new GameDetectionMetrics { StartTime = startedAt, ScanType = DetectionScanType.Full }
            },
            CancellationToken.None);
        await run.Repairs.Owner.StartWorkAsync(id, null, CancellationToken.None);
        var stageKey = cancelled ? "signalr.gameDetect.cancelled" : "signalr.gameDetect.complete.full";

        run.State.FailNextRepairWrite = true;
        var finalize = typeof(GameCacheDetectionService).GetMethod(
            "FinalizeDetectionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)finalize.Invoke(run.Detection, [id, !cancelled,
            cancelled ? OperationStatus.Cancelled : OperationStatus.Completed, stageKey, cancelled, null, 3, 1])!;

        Assert.Equal(
            cancelled ? OperationStatus.Cancelled : OperationStatus.Completed,
            run.Repairs.Tracker.GetOperation(id)!.Status);
        var unsaved = run.State.LoadOperationRepairs().Single(repair => repair.Id == id);
        Assert.Equal(OperationRepairPhase.Running, unsaved.Phase);
        Assert.Null(unsaved.GameDetection!.CompletionStageKey);

        run.RetryRelease.Release();
        var landed = await run.WaitForCompletedRepairAsync(id);
        Assert.Equal(cancelled ? OperationStatus.Cancelled : OperationStatus.Completed, landed.Outcome);
        Assert.Equal(stageKey, landed.GameDetection!.CompletionStageKey);
        Assert.Equal(3, landed.GameDetection.TotalGamesDetected);
    }

    // A real detection over a test database, its native scan replaced by a report of one game.
    private sealed class DetectionRun : IAsyncDisposable
    {
        private readonly TestDatabase _database;
        private readonly string _root;
        private readonly TaskCompletionSource _blockerRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Guid? _cancelling;

        private DetectionRun(string root, TestDatabase database)
        {
            _root = root;
            _database = database;
        }

        public OperationRepairTests.RepairHarness Repairs { get; private set; } = null!;
        public OperationRepairTests.FailingStateService State { get; private set; } = null!;
        public GameCacheDetectionService Detection { get; private set; } = null!;
        public SemaphoreSlim RetryRelease { get; } = new(0);
        public Guid BlockedRepair { get; set; }
        public string OperationsDirectory { get; private set; } = null!;
        public RecordingNotificationsProxy Notifications { get; private set; } = null!;
        public string? CancelAtStage { get; set; }
        public bool CancelOnFirstSave { get; set; }

        public static async Task<DetectionRun> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "lcm-detection-run", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var run = new DetectionRun(root, await TestDatabase.CreateAsync());
            run.State = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "state"));
            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(run.State, new AppState());
            run.Repairs = await OperationRepairTests.RepairHarness.CreateAsync(
                Path.Combine(root, "state"),
                stateService: run.State,
                waitUntil: (_, cancellationToken) => run.RetryRelease.WaitAsync(cancellationToken),
                apply: (repair, cancellationToken) => repair.Id == run.BlockedRepair
                    ? run._blockerRelease.Task.WaitAsync(cancellationToken)
                    : Task.CompletedTask);

            var paths = new TempDirPathResolver(root);
            run.OperationsDirectory = paths.GetOperationsDirectory();
            var configuration = new ConfigurationBuilder().Build();
            var datasources = new DatasourceService(configuration, paths, NullLogger<DatasourceService>.Instance);
            var datasource = Assert.Single(datasources.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            Directory.CreateDirectory(datasource.CachePath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);
            var detector = paths.GetRustGameDetectorPath();
            Directory.CreateDirectory(Path.GetDirectoryName(detector)!);
            File.WriteAllText(detector, string.Empty);
            var notifications = (RecordingNotificationsProxy)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotificationsProxy>();
            run.Notifications = notifications;
            notifications.OnSend = (_, value) =>
            {
                if (value?.GetType().GetProperty("StageKey")?.GetValue(value) is string stage
                    && stage == run.CancelAtStage
                    && value.GetType().GetProperty("OperationId")?.GetValue(value) is Guid operationId)
                {
                    if (run.CancelOnFirstSave)
                    {
                        run._cancelling = operationId;
                    }
                    else
                    {
                        run.Repairs.Tracker.CancelOperation(operationId);
                    }
                }
                return Task.CompletedTask;
            };
            var contexts = new SaveContexts(run, run._database.Factory);
            run.Detection = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                paths,
                run.Repairs.Owner,
                contexts,
                new GameCacheDetectionDataService(contexts, NullLogger<GameCacheDetectionDataService>.Instance),
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: new UnknownGameResolutionService(contexts, NullLogger<UnknownGameResolutionService>.Instance),
                new OneGameReport(paths, run.Repairs.Tracker, paths.GetOperationsDirectory()),
                (ISignalRNotificationService)(object)notifications,
                datasources,
                new DatasourceCapabilityService(datasources),
                run.Repairs.Tracker,
                CacheScanGateHarness.Idle());
            return run;
        }

        public AppDbContext CreateContext() => new(_database.Options);

        public async Task<OperationInfo> WaitForTerminalAsync(Guid operationId)
        {
            while (Repairs.Tracker.GetOperation(operationId) is not { } operation || !operation.Status.IsTerminal())
            {
                await Task.Delay(25);
            }
            return Repairs.Tracker.GetOperation(operationId)!;
        }

        public async Task<OperationRepair> WaitForCompletedRepairAsync(Guid operationId)
        {
            for (var attempt = 0; attempt < 400; attempt++)
            {
                var repair = State.LoadOperationRepairs().Single(item => item.Id == operationId);
                if (repair.Phase == OperationRepairPhase.Completed)
                {
                    return repair;
                }
                await Task.Delay(25);
            }
            throw new TimeoutException($"Detection repair {operationId} did not complete.");
        }

        public async ValueTask DisposeAsync()
        {
            _blockerRelease.TrySetResult();
            RetryRelease.Release(100);
            await Repairs.DisposeAsync();
            Detection.Dispose();
            await _database.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }

        // Cancels the detection when its save phase first reaches the database.
        private sealed class SaveContexts(DetectionRun run, TestDbContextFactory inner) : IDbContextFactory<AppDbContext>
        {
            public AppDbContext CreateDbContext() => inner.CreateDbContext();

            public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            {
                if (run._cancelling is { } operationId)
                {
                    run._cancelling = null;
                    run.Repairs.Tracker.CancelOperation(operationId);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return inner.CreateDbContextAsync(cancellationToken);
            }
        }

        private sealed class OneGameReport(IPathResolver paths, IUnifiedOperationTracker tracker, string reportDirectory)
            : RustProcessHelper(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                paths,
                tracker)
        {
            public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
                ProcessStartInfo process,
                Guid? operationId,
                CancellationToken cancellationToken,
                Func<RustProgressEvent, Task>? onProgressEvent,
                string processLabel = "rust")
            {
                // The report path the detection reads back for its one default datasource.
                var output = Path.Combine(reportDirectory, $"game_detection_{operationId}_default.json");
                var game = new GameCacheInfo
                {
                    GameAppId = 570,
                    GameName = "Dota 2",
                    CacheFilesFound = 1,
                    TotalSizeBytes = 1024,
                    Service = "steam"
                };
                var report = new Dictionary<string, object>
                {
                    ["total_games_detected"] = 1,
                    ["total_services_detected"] = 0,
                    ["indexed_cache_files"] = 1,
                    ["games"] = new[] { game },
                    ["services"] = Array.Empty<ServiceCacheInfo>()
                };
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report), cancellationToken);
                return new ProcessExecutionResult { ExitCode = 0 };
            }
        }
    }

    /// <summary>
    /// Builds a real <see cref="GameCacheDetectionService"/> wired to a fake tracker and a recording
    /// notification service. Only the persistence and datasource collaborators are real (rooted at a
    /// throwaway temp directory); the collaborators exercised solely by the background scan are left
    /// null because the fake tracker returns null from <c>GetOperation</c>, so the spawned run exits
    /// immediately without touching them.
    /// </summary>
    private sealed class ServiceContext : IDisposable
    {
        private readonly string _root;
        private readonly List<GameCacheDetectionService> _restarted = [];
        private readonly Func<IUnifiedOperationTracker, GameCacheDetectionService> _create;
        private readonly ServiceProvider _services;

        public FakeTrackerProxy Tracker { get; }
        public RecordingNotificationsProxy Notifications { get; }
        public GameCacheDetectionService Service { get; }
        public OperationStateService States { get; }

        public ServiceContext()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-gamedetect-visibility", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var pathResolver = new TempDirPathResolver(_root);
            var configuration = new ConfigurationBuilder().Build();
            var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);
            var stateService = new StateService(
                NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);

            // Seed an empty in-memory state so the persistence path never runs the legacy-file migration.
            var cachedStateField = typeof(StateService).GetField(
                "_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!;
            cachedStateField.SetValue(stateService, new AppState());

            Notifications = (RecordingNotificationsProxy)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotificationsProxy>();
            Tracker = (FakeTrackerProxy)DispatchProxy
                .Create<IUnifiedOperationTracker, FakeTrackerProxy>();
            var datasourceService = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);
            var capabilityService = new DatasourceCapabilityService(datasourceService);
            GameCacheDetectionService? repairOwner = null;
            _services = new ServiceCollection()
                .AddSingleton(datasourceService)
                .AddSingleton(capabilityService)
                .AddSingleton<GameCacheDetectionService>(_ => repairOwner
                    ?? throw new InvalidOperationException("The game detection service is not initialized."))
                .BuildServiceProvider();
            var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
            States = new OperationStateService(
                NullLogger<OperationStateService>.Instance,
                configuration,
                stateService,
                _services.GetRequiredService<IServiceScopeFactory>(),
                new HostLifetime(),
                processManager,
                (IUnifiedOperationTracker)(object)Tracker);

            // These lifecycle tests exercise operation races rather than datasource discovery,
            // so provide one unambiguous monolithic log source for the required safety check.
            var datasource = Assert.Single(datasourceService.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

            _create = tracker => new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                pathResolver,
                States,
                dbContextFactory: null!,
                detectionDataService: null!,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rustProcessHelper: null!,
                (ISignalRNotificationService)(object)Notifications,
                datasourceService,
                capabilityService,
                tracker,
                CacheScanGateHarness.Idle());
            Service = _create((IUnifiedOperationTracker)(object)Tracker);
            repairOwner = Service;
            _services.GetRequiredService<GameCacheDetectionService>();
            States.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        }

        /// <summary>
        /// A second service over the same saved states, as after a server restart: its constructor
        /// restores every detection saved as running.
        /// </summary>
        public async Task Restart(IUnifiedOperationTracker tracker)
        {
            var service = _create(tracker);
            _restarted.Add(service);
            await service.RestoreInterruptedOperations(CancellationToken.None);
        }

        public void Dispose()
        {
            foreach (var restarted in _restarted) restarted.Dispose();
            States.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _services.Dispose();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of the throwaway temp dir; a locked file must not fail the test.
            }
        }
    }

    private sealed class HostLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _started = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly CancellationTokenSource _stopped = new();

        public CancellationToken ApplicationStarted => _started.Token;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => _stopped.Token;
        public void StopApplication() => _stopping.Cancel();
    }

    /// <summary>
    /// Minimal <see cref="IUnifiedOperationTracker"/> stand-in. <c>RegisterOperation</c> mints an id,
    /// records the run's <c>onTerminalEmit</c> closure and adds an active row; <c>CompleteOperation</c>
    /// (used by the service's stale-cleanup pass) fires that run's own closure; <c>GetOperation</c>
    /// returns null for a running run so the background scan exits immediately. Not sealed for DispatchProxy.Create.
    /// </summary>
    private class FakeTrackerProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Func<OperationTerminalInfo, Task>> _emits = new();
        private readonly List<OperationInfo> _active = new();
        // A run stays readable after it ends, as in the real tracker, so a terminal emit can read its final metadata.
        private readonly Dictionary<Guid, OperationInfo> _finished = new();
        // Kept after a run ends, so a test can still read the notice a finished run carried.
        private readonly Dictionary<Guid, RunNotice?> _notices = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                case nameof(IUnifiedOperationTracker.TryRestoreOperation):
                {
                    var restoring = targetMethod.Name == nameof(IUnifiedOperationTracker.TryRestoreOperation);
                    var offset = restoring ? 1 : 0;
                    var id = restoring ? (Guid)args![0]! : Guid.NewGuid();
                    var name = args?[1 + offset] as string ?? "Game Detection";
                    var metadata = args?[3 + offset];
                    var emit = args?[5 + offset] as Func<OperationTerminalInfo, Task>;
                    lock (_sync)
                    {
                        if (emit != null)
                        {
                            _emits[id] = emit;
                        }

                        _notices[id] = (RunNotice?)args![restoring ? 9 : 10];

                        _active.Add(new OperationInfo
                        {
                            Id = id,
                            Type = OperationType.GameDetection,
                            Name = name,
                            StartedAt = (DateTime?)args![8] ?? DateTime.UtcNow,
                            ParentOperationId = (Guid?)args![7],
                            // Carry the run's metadata so GetActiveOperation can surface the run-stable
                            // visibility flag the recovery endpoint reports.
                            Metadata = metadata
                        });
                    }

                    return restoring ? true : id;
                }
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                    lock (_sync)
                    {
                        return _active.ToArray();
                    }
                case nameof(IUnifiedOperationTracker.CompleteOperation):
                {
                    var id = (Guid)args![0]!;
                    var success = (bool)args[1]!;
                    var error = args[2] as string;
                    FireTerminal(id, success, cancelled: (bool?)args[3] ?? false, error,
                        args[5] as Action<OperationInfo>);
                    return null;
                }
                case nameof(IUnifiedOperationTracker.UpdateProgress):
                    lock (_sync)
                    {
                        var operation = _active.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                        if (operation != null)
                        {
                            operation.PercentComplete = (double)args![1]!;
                            operation.Message = args[2] as string ?? string.Empty;
                            (args[3] as Action<OperationInfo>)?.Invoke(operation);
                        }
                    }
                    return null;
                case nameof(IUnifiedOperationTracker.GetOperation):
                    lock (_sync)
                    {
                        return _finished.GetValueOrDefault((Guid)args![0]!);
                    }
                default:
                    return DefaultReturnValue(targetMethod);
            }
        }

        internal OperationInfo? Get(Guid id) { lock (_sync) return _active.FirstOrDefault(o => o.Id == id); }

        internal RunNotice? NoticeOf(Guid id) { lock (_sync) return _notices[id]; }

        internal void FireTerminal(Guid id, bool success, bool cancelled, string? error, Action<OperationInfo>? onCompleting = null)
        {
            Func<OperationTerminalInfo, Task>? emit;
            lock (_sync)
            {
                var operation = _active.FirstOrDefault(o => o.Id == id);
                if (operation == null) return;
                onCompleting?.Invoke(operation);
                operation.Status = cancelled ? OperationStatus.Cancelled : success ? OperationStatus.Completed : OperationStatus.Failed;
                _active.Remove(operation);
                _finished[id] = operation;
                _emits.Remove(id, out emit);
            }

            emit?.Invoke(new OperationTerminalInfo(success, cancelled, error));
        }
    }

    /// <summary>
    /// Records the operation id of every emitted <c>GameDetectionComplete</c> terminal, across both
    /// the plain and failure broadcast choke points. Every other member returns its type default.
    /// Not sealed for DispatchProxy.Create.
    /// </summary>
    private class RecordingNotificationsProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly HashSet<Guid> _completed = [];
        internal readonly List<(string Event, object? Value)> Events = [];
        internal Func<string, object?, Task>? OnSend { get; set; }

        internal bool Completed(Guid id)
        {
            lock (_sync)
            {
                return _completed.Contains(id);
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod?.Name;
            if (args is [string eventName, var value, ..])
            {
                lock (_sync) Events.Add((eventName, value));
                if (OnSend != null) return OnSend(eventName, value);
            }
            if ((name == nameof(ISignalRNotificationService.NotifyAllAsync)
                    || name == nameof(ISignalRNotificationService.NotifyOperationFailedAsync))
                && args is { Length: >= 2 }
                && args[1] is { } payload
                && payload.GetType().Name == "GameDetectionComplete")
            {
                if (payload.GetType().GetProperty("OperationId")?.GetValue(payload) is Guid operationId)
                {
                    lock (_sync)
                    {
                        _completed.Add(operationId);
                    }
                }
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    private static object? DefaultReturnValue(MethodInfo? targetMethod)
    {
        var returnType = targetMethod?.ReturnType;

        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
