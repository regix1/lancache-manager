using System.Reflection;
using System.Text.Json;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Notification visibility (showNotification) is decided per run by the caller and must travel with
/// THAT run's operation, never through a service-wide field. A silent scheduled run and a manual
/// visible start can overlap: the second call is rejected as "already active" (or triggers stale
/// cleanup of the first), and neither situation may repaint the running run's lifecycle events with
/// the other run's visibility. These tests pin that the terminal event carries each operation's own
/// captured flag.
/// </summary>
public class GameDetectionVisibilityRaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoringPersistedRunsPreservesIdentityAndCancelsOrphanedChildren(bool child)
    {
        using var ctx = new ServiceContext();
        var id = Guid.NewGuid();
        Guid? parent = child ? Guid.NewGuid() : null;
        var startedAt = DateTime.UtcNow.AddMinutes(-2);
        ctx.States.SaveState($"gameDetection_{id}", new LancacheManager.Core.Services.OperationState
        {
            Key = $"gameDetection_{id}", Type = OperationType.GameDetection.ToWireString(), Status = "running",
            Data = JsonSerializer.SerializeToElement(new
            {
                operationId = id, parentOperationId = parent, startedAt,
                scanType = DetectionScanType.Full, showNotification = false
            })
        });
        typeof(GameCacheDetectionService).GetMethod("RestoreInterruptedOperations", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ctx.Service, null);
        if (child)
        {
            Assert.Null(ctx.Tracker.Get(id));
            var complete = Assert.IsType<SignalRNotifications.GameDetectionComplete>(Assert.Single(ctx.Notifications.Events).Value);
            Assert.Equal(parent, complete.ParentOperationId);
            Assert.True(complete.Cancelled);
            Assert.False(complete.ShowNotification);
            Assert.Equal("cancelled", ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString())).Status);
        }
        else
        {
            var operation = ctx.Tracker.Get(id)!;
            Assert.Equal(startedAt, operation.StartedAt);
            var metrics = Assert.IsType<GameDetectionMetrics>(operation.Metadata);
            Assert.Equal(DetectionScanType.Full, metrics.ScanType);
            Assert.False(metrics.ShowNotification);
            Assert.Null(operation.ParentOperationId);
        }
    }

    [Fact]
    public async Task ParentIdentityTravelsThroughStartedRecoveryAndWinningTerminal()
    {
        using var ctx = new ServiceContext();
        var parent = Guid.NewGuid();
        var id = (await ctx.Service.StartDetectionAsync(incremental: false, showNotification: false,
            parentOperationId: parent))!.Value;
        var active = ctx.Service.GetActiveOperation()!;
        Assert.Equal(parent, active.ParentOperationId);
        Assert.Equal(DetectionScanType.Full, active.ScanType);
        Assert.Equal(parent, ctx.Tracker.Get(id)!.ParentOperationId);
        var started = JsonSerializer.SerializeToElement(ctx.Notifications.Events.Single(e => e.Event == SignalREvents.GameDetectionStarted).Value);
        Assert.Equal(parent, started.GetProperty("ParentOperationId").GetGuid());
        var running = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString()));
        Assert.Equal(parent, running.Data!.Value.GetProperty("parentOperationId").GetGuid());
        Assert.False(running.Data.Value.GetProperty("showNotification").GetBoolean());
        Assert.Equal(active.StartTime, running.Data.Value.GetProperty("startedAt").GetDateTime());
        ctx.Tracker.FireTerminal(id, success: false, cancelled: true, error: "Cancelled by user");
        var complete = Assert.IsType<SignalRNotifications.GameDetectionComplete>(ctx.Notifications.Events.Single(e => e.Event == SignalREvents.GameDetectionComplete).Value);
        Assert.Equal(parent, complete.ParentOperationId);
        Assert.True(complete.Cancelled);
        var persisted = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(id.ToString()));
        Assert.Equal("cancelled", persisted.Status);
        Assert.Equal(parent, persisted.Data!.Value.GetProperty("parentOperationId").GetGuid());
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
        var start = ctx.Service.StartDetectionAsync(showNotification: false);
        var oldId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var old = tracker.GetOperation(oldId)!;
        tracker.CompleteOperation(oldId, success: false, error: "Cancelled by user", cancelled: true);
        var persisted = ctx.States.GetAllStates().Single(s => s.Key.EndsWith(oldId.ToString())).Data!.Value.GetRawText();
        release.TrySetResult();
        await start;

        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStart = ctx.Service.StartDetectionAsync();
        var nextId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finalize = typeof(GameCacheDetectionService).GetMethod("FinalizeDetectionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)finalize.Invoke(ctx.Service, [oldId, true, OperationStatus.Completed, "signalr.gameDetect.complete.full", false,
            new Dictionary<string, object?> { ["newGamesCount"] = 999 }, 999, 999])!;
        Assert.Equal(OperationStatus.Cancelled, old.Status);
        Assert.Null(((GameDetectionMetrics)old.Metadata!).CompletionContext);
        Assert.Equal(persisted, ctx.States.GetAllStates().Single(s => s.Key.EndsWith(oldId.ToString())).Data!.Value.GetRawText());
        Assert.Equal(nextId, typeof(GameCacheDetectionService).GetField("_currentTrackerOperationId", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ctx.Service));
        tracker.CompleteOperation(nextId, success: false, cancelled: true);
        release.TrySetResult();
        await nextStart;
    }

    [Fact]
    public async Task SecondStart_DuringActiveRun_DoesNotAlterActiveRunTerminalVisibility()
    {
        using var ctx = new ServiceContext();

        // A silent (scheduled) run claims the operation.
        var firstId = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: false);
        Assert.NotNull(firstId);

        // A visible (manual) start arrives while the silent run is active - it is rejected.
        var secondId = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: true);
        Assert.Null(secondId);

        // The active silent run's terminal must still carry ITS OWN visibility (false), not the
        // visible flag the rejected attempt tried to set.
        ctx.Tracker.FireTerminal(firstId!.Value, success: true, cancelled: false, error: null);

        Assert.True(ctx.Notifications.TryGetCompleteVisibility(firstId.Value, out var showNotification));
        Assert.False(showNotification);
    }

    [Fact]
    public async Task GetActiveOperation_ForSilentRun_ReportsHiddenVisibility()
    {
        using var ctx = new ServiceContext();

        // A silent (scheduled) run claims the operation.
        var id = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: false);
        Assert.NotNull(id);

        // The recovery endpoint (GET /api/games/detect/active) reads this. It must report the run's
        // own hidden flag so a page reload mid-run declines to resurrect a card the silent terminal
        // can never clear.
        var active = ctx.Service.GetActiveOperation();
        Assert.NotNull(active);
        Assert.False(active!.ShowNotification);
    }

    [Fact]
    public async Task GetActiveOperation_ForVisibleRun_ReportsVisible()
    {
        using var ctx = new ServiceContext();

        var id = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: true);
        Assert.NotNull(id);

        var active = ctx.Service.GetActiveOperation();
        Assert.NotNull(active);
        Assert.True(active!.ShowNotification);
    }

    [Fact]
    public async Task StaleCleanupTerminal_CarriesStaleRunFlag_NotNewAttemptFlag()
    {
        using var ctx = new ServiceContext();

        // A silent run is registered, then ages past the 30-minute stale threshold.
        var staleId = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: false);
        Assert.NotNull(staleId);
        ctx.Tracker.BackdateStartedAt(staleId!.Value, TimeSpan.FromMinutes(31));

        // A visible manual start arrives; its stale-cleanup pass completes the aged silent run.
        var newId = await ctx.Service.StartDetectionAsync(incremental: true, showNotification: true);
        Assert.NotNull(newId);

        // The stale run's terminal must carry the stale run's OWN flag (false), never the new
        // attempt's flag (true).
        Assert.True(ctx.Notifications.TryGetCompleteVisibility(staleId.Value, out var showNotification));
        Assert.False(showNotification);
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

            var operationStateService = new OperationStateService(
                NullLogger<OperationStateService>.Instance, configuration, stateService);
            States = operationStateService;
            var datasourceService = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);

            // These lifecycle tests exercise operation races rather than datasource discovery,
            // so provide one unambiguous monolithic log source for the required safety check.
            var datasource = Assert.Single(datasourceService.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

            var capabilityService = new DatasourceCapabilityService(datasourceService);

            Notifications = (RecordingNotificationsProxy)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotificationsProxy>();
            Tracker = (FakeTrackerProxy)DispatchProxy
                .Create<IUnifiedOperationTracker, FakeTrackerProxy>();

            Service = new GameCacheDetectionService(
                NullLogger<GameCacheDetectionService>.Instance,
                pathResolver,
                operationStateService,
                dbContextFactory: null!,
                detectionDataService: null!,
                evictedDetectionPreservationService: null!,
                unknownGameResolutionService: null!,
                rustProcessHelper: null!,
                (ISignalRNotificationService)(object)Notifications,
                datasourceService,
                capabilityService,
                (IUnifiedOperationTracker)(object)Tracker,
                CacheScanGateHarness.Idle());
        }

        public void Dispose()
        {
            Service.Dispose();
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

    /// <summary>
    /// Minimal <see cref="IUnifiedOperationTracker"/> stand-in. <c>RegisterOperation</c> mints an id,
    /// records the run's <c>onTerminalEmit</c> closure and adds an active row; <c>CompleteOperation</c>
    /// (used by the service's stale-cleanup pass) fires that run's own closure; <c>GetOperation</c>
    /// returns null so the background scan exits immediately. Not sealed for DispatchProxy.Create.
    /// </summary>
    private class FakeTrackerProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, Func<OperationTerminalInfo, Task>> _emits = new();
        private readonly List<OperationInfo> _active = new();

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
                    return null;
                default:
                    return DefaultReturnValue(targetMethod);
            }
        }

        internal void BackdateStartedAt(Guid id, TimeSpan age)
        {
            lock (_sync)
            {
                var op = _active.FirstOrDefault(o => o.Id == id);
                if (op != null)
                {
                    op.StartedAt = DateTime.UtcNow - age;
                }
            }
        }

        internal OperationInfo? Get(Guid id) { lock (_sync) return _active.FirstOrDefault(o => o.Id == id); }

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
                _emits.Remove(id, out emit);
            }

            emit?.Invoke(new OperationTerminalInfo(success, cancelled, error));
        }
    }

    /// <summary>
    /// Records the <c>ShowNotification</c> flag of every emitted <c>GameDetectionComplete</c> terminal,
    /// keyed by operation id, across both the plain and failure broadcast choke points. Every other
    /// member returns its type default. Not sealed for DispatchProxy.Create.
    /// </summary>
    private class RecordingNotificationsProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly Dictionary<Guid, bool> _completeVisibility = new();
        internal readonly List<(string Event, object? Value)> Events = [];
        internal Func<string, object?, Task>? OnSend { get; set; }

        internal bool TryGetCompleteVisibility(Guid id, out bool showNotification)
        {
            lock (_sync)
            {
                return _completeVisibility.TryGetValue(id, out showNotification);
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
                var type = payload.GetType();
                if (type.GetProperty("OperationId")?.GetValue(payload) is Guid operationId
                    && type.GetProperty("ShowNotification")?.GetValue(payload) is bool showNotification)
                {
                    lock (_sync)
                    {
                        _completeVisibility[operationId] = showNotification;
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
