using System.Reflection;
using System.Runtime.CompilerServices;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Tests.CacheScanGateHarness;

namespace LancacheManager.Tests;

/// <summary>
/// The first phase of every eviction scan is a full game detection run under the scan's own
/// operation: started with its card hidden, its percent forwarded onto the scan card, waited on
/// until its terminal, and cancelled along with the scan. The tracker here is a stand-in the test
/// drives, so the detection stays "running" exactly as long as each test wants it to.
/// </summary>
public sealed class CacheScanDetectionPhaseTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RemovalStartsUnderActiveScanAndCancellationKeepsCompetitorsWaiting(bool cancel, bool populated)
    {
        using var ctx = new PhaseContext();
        await using var database = await TestDatabase.CreateAsync();
        await using var context = new AppDbContext(database.Options);
        foreach (var platform in populated ? Enum.GetValues<PrefillPlatform>() : [])
        {
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "123", AppName = "Removed", CachedAtUtc = DateTime.UtcNow
            });
            context.PrefillCachedApps.Add(new PrefillCachedApp
            {
                Platform = platform, AppId = "456", AppName = "Kept", CachedAtUtc = DateTime.UtcNow
            });
            context.Downloads.Add(new Download
            {
                Service = platform.ToService(), ClientIp = "127.0.0.1", Datasource = "Default",
                GameAppId = platform == PrefillPlatform.Steam ? 123 : null,
                EpicAppId = platform == PrefillPlatform.Epic ? "123" : null,
                XboxProductId = platform == PrefillPlatform.Xbox ? "123" : null,
                GameName = "Removed", IsEvicted = true, StartTimeUtc = DateTime.UtcNow, EndTimeUtc = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        PhaseContext.SetField(ctx.Scan, "_operationTracker", tracker);
        var scanId = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        var queue = new OperationQueueService(tracker,
            new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance),
            (ISignalRNotificationService)(object)ctx.Notifications, NullLogger<OperationQueueService>.Instance);
        var promoted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = await queue.EnqueueAsync(OperationType.CacheSizeScan, ConflictScope.Bulk(), "Cache File Scan",
            () => { promoted.TrySetResult(); return Task.FromResult<Guid?>(Guid.NewGuid()); }, CancellationToken.None);
        Assert.True(queued.Queued);
        var terminals = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var childTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanTerminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.OperationTerminal += operation =>
        {
            terminals.Add(operation.Id);
            if (operation.Type == OperationType.EvictionRemoval) childTerminal.TrySetResult();
            if (operation.Id == scanId) scanTerminal.TrySetResult();
        };
        Guid removalId = default;
        ctx.Notifications.OnSent = (eventName, value) =>
        {
            if (eventName != SignalREvents.EvictionRemovalStarted || value is not EvictionRemovalStarted started) return;
            removalId = started.OperationId;
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
            Assert.Equal(OperationStatus.Running, tracker.GetOperation(removalId)!.Status);
            Assert.False(promoted.Task.IsCompleted);
            if (cancel) tracker.CancelOperation(removalId);
        };
        await ctx.Scan.RemoveEvictedRecordsAsync(context, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(Guid.Empty, removalId);
        // Without cancellation removal reaches the fixture's absent summary service after commit.
        Assert.Equal(cancel ? OperationStatus.Cancelled : OperationStatus.Failed, tracker.GetOperation(removalId)!.Status);
        Assert.Equal(populated ? cancel ? 10 : 5 : 0, await context.PrefillCachedApps.CountAsync());
        if (!cancel) Assert.All(await context.PrefillCachedApps.AsNoTracking().ToListAsync(), app => Assert.Equal("456", app.AppId));
        Assert.Equal(OperationStatus.Running, tracker.GetOperation(scanId)!.Status);
        Assert.False(promoted.Task.IsCompleted);
        tracker.CompleteOperation(scanId, success: true);
        await promoted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(childTerminal.Task, scanTerminal.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, terminals.Count(id => id == removalId));
        Assert.Equal(1, terminals.Count(id => id == scanId));
        Assert.Equal(queued.OperationId, Assert.Single(ctx.Notifications.Waiting).OperationId);
    }

    [Fact]
    public async Task ThePhaseStartsAHiddenFullDetectionForwardsItsPercentAndWaitsForItsTerminal()
    {
        using var ctx = new PhaseContext();
        var scanId = Guid.NewGuid();

        var phase = ctx.RunPhaseAsync(scanId, CancellationToken.None);

        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);
        var metrics = Assert.IsType<GameDetectionMetrics>(detection.Metadata);
        Assert.False(metrics.ShowNotification);
        Assert.Equal(DetectionScanType.Full, metrics.ScanType);

        ctx.Tracker.SetPercent(detection.Id, 42);
        var forwarded = await ctx.Notifications.WaitForAsync(
            SignalREvents.EvictionScanProgress,
            payload => payload is EvictionScanProgress { StageKey: "signalr.evictionScan.detectingGames", PercentComplete: 42 });
        Assert.Equal(scanId, Assert.IsType<EvictionScanProgress>(forwarded).OperationId);
        Assert.False(phase.IsCompleted);

        ctx.Tracker.Complete(detection.Id);

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(ctx.Notifications.Waiting);
    }

    [Fact]
    public async Task CancellingTheScanCancelsTheDetectionItIsWaitingOn()
    {
        using var ctx = new PhaseContext();
        using var cts = new CancellationTokenSource();

        var phase = ctx.RunPhaseAsync(Guid.NewGuid(), cts.Token);
        var detection = await ctx.Tracker.WaitForOperationAsync(OperationType.GameDetection);

        cts.Cancel();

        await phase.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(detection.Id, ctx.Tracker.Cancelled);
        Assert.Empty(ctx.Notifications.Waiting);
    }

    /// <summary>
    /// A real <see cref="GameCacheDetectionService"/> so the phase's start call registers a
    /// genuine detection operation, and an uninitialized <see cref="CacheReconciliationService"/>
    /// carrying only the fields the phase reads. The detection's background run fails at once on
    /// its missing database and reports that to the tracker stand-in, which ignores it: the phase
    /// must see the detection as running until the test ends it.
    /// </summary>
    private sealed class PhaseContext : IDisposable
    {
        private readonly string _root;
        private readonly CacheReconciliationService _scan;

        public FakeTracker Tracker { get; }
        public RecordingNotifications Notifications { get; }
        public CacheReconciliationService Scan => _scan;

        public PhaseContext()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-cache-scan-phase", Guid.NewGuid().ToString("N"));
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
            typeof(StateService)
                .GetField("_cachedState", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(stateService, new AppState());

            var operationStateService = new OperationStateService(
                NullLogger<OperationStateService>.Instance, configuration, stateService);
            var datasourceService = new DatasourceService(
                configuration, pathResolver, NullLogger<DatasourceService>.Instance);

            // One unambiguous monolithic log source, so the detection's capability gate lets it start.
            var datasource = Assert.Single(datasourceService.GetDatasources());
            Directory.CreateDirectory(datasource.LogPath);
            File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

            var capabilityService = new DatasourceCapabilityService(datasourceService);

            Notifications = (RecordingNotifications)DispatchProxy
                .Create<ISignalRNotificationService, RecordingNotifications>();
            Tracker = (FakeTracker)DispatchProxy
                .Create<IUnifiedOperationTracker, FakeTracker>();

            var detection = new GameCacheDetectionService(
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
                Idle());

            _scan = (CacheReconciliationService)RuntimeHelpers.GetUninitializedObject(typeof(CacheReconciliationService));
            SetField(_scan, "_logger", NullLogger<CacheReconciliationService>.Instance);
            SetField(_scan, "_operationTracker", (IUnifiedOperationTracker)(object)Tracker);
            SetField(_scan, "_notifications", (ISignalRNotificationService)(object)Notifications);
            SetField(_scan, "_gameCacheDetectionService", detection);
            SetField(_scan, "_capabilityService", capabilityService);
            foreach (var name in new[] { "_silentRemovalOperationIds", "_evictionRemovalTerminalStates" })
            {
                var field = typeof(CacheReconciliationService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
                field.SetValue(_scan, Activator.CreateInstance(field.FieldType));
            }
        }

        public Task RunPhaseAsync(Guid scanOperationId, CancellationToken token)
        {
            var phase = typeof(CacheReconciliationService).GetMethod(
                "RunFullDetectionPhaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return (Task)phase.Invoke(_scan, [scanOperationId, true, token])!;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp tree the OS still holds open is not this test's concern.
            }
        }

        internal static void SetField(object target, string name, object value)
            => typeof(CacheReconciliationService)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(target, value);
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

    /// <summary>
    /// Tracker stand-in the test drives. Registration mints a running row; progress lands on it;
    /// terminal subscriptions are kept so <see cref="Complete"/> and a cancel can fire them. The
    /// service's own CompleteOperation call (its background run failing) is ignored on purpose:
    /// the phase must keep waiting until the test ends the detection. Not sealed for DispatchProxy.
    /// </summary>
    public class FakeTracker : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly List<OperationInfo> _operations = [];
        private readonly List<Action<OperationInfo>> _terminalHandlers = [];

        internal List<Guid> Cancelled { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IUnifiedOperationTracker.RegisterOperation):
                {
                    var operation = new OperationInfo
                    {
                        Id = Guid.NewGuid(),
                        Type = (OperationType)args![0]!,
                        Name = (string)args[1]!,
                        Status = OperationStatus.Running,
                        Metadata = args[3]
                    };
                    lock (_sync)
                    {
                        _operations.Add(operation);
                    }

                    return operation.Id;
                }
                case nameof(IUnifiedOperationTracker.GetOperation):
                    lock (_sync)
                    {
                        return _operations.FirstOrDefault(o => o.Id == (Guid)args![0]!);
                    }
                case nameof(IUnifiedOperationTracker.GetActiveOperations):
                    lock (_sync)
                    {
                        return _operations.Where(o => o.Status == OperationStatus.Running).ToList();
                    }
                case nameof(IUnifiedOperationTracker.UpdateProgress):
                    SetPercent((Guid)args![0]!, (double)args[1]!);
                    return null;
                case nameof(IUnifiedOperationTracker.CancelOperation):
                {
                    var id = (Guid)args![0]!;
                    lock (_sync)
                    {
                        Cancelled.Add(id);
                    }

                    End(id, OperationStatus.Cancelled);
                    return default(OperationCancelResult);
                }
                case "add_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Add((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                case "remove_OperationTerminal":
                    lock (_sync)
                    {
                        _terminalHandlers.Remove((Action<OperationInfo>)args![0]!);
                    }

                    return null;
                default:
                    return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
            }
        }

        internal async Task<OperationInfo> WaitForOperationAsync(OperationType type)
        {
            for (var i = 0; i < 100; i++)
            {
                lock (_sync)
                {
                    var found = _operations.FirstOrDefault(o => o.Type == type);
                    if (found != null)
                    {
                        return found;
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No {type} operation was registered");
        }

        internal void SetPercent(Guid id, double percent)
        {
            lock (_sync)
            {
                var operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation != null)
                {
                    operation.PercentComplete = percent;
                }
            }
        }

        internal void Complete(Guid id) => End(id, OperationStatus.Completed);

        private void End(Guid id, OperationStatus status)
        {
            OperationInfo? operation;
            Action<OperationInfo>[] handlers;
            lock (_sync)
            {
                operation = _operations.FirstOrDefault(o => o.Id == id);
                if (operation == null)
                {
                    return;
                }

                operation.Status = status;
                handlers = _terminalHandlers.ToArray();
            }

            foreach (var handler in handlers)
            {
                handler(operation);
            }
        }
    }

    /// <summary>
    /// Records every broadcast as its event name and payload. Not sealed for DispatchProxy.
    /// </summary>
    public class RecordingNotifications : DispatchProxy
    {
        internal Action<string, object?>? OnSent { get; set; }
        private readonly object _sync = new();
        private readonly List<(string Event, object? Payload)> _sent = [];
        internal IReadOnlyList<OperationWaitingNotification> Waiting
        {
            get { lock (_sync) return _sent.Select(item => item.Payload).OfType<OperationWaitingNotification>().ToList(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is [string eventName, ..])
            {
                OnSent?.Invoke(eventName, args.Length > 1 ? args[1] : null);
                lock (_sync)
                {
                    _sent.Add((eventName, args.Length > 1 ? args[1] : null));
                }
            }

            return targetMethod?.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }

        internal async Task<object?> WaitForAsync(string eventName, Func<object?, bool> matches)
        {
            for (var i = 0; i < 100; i++)
            {
                lock (_sync)
                {
                    var hit = _sent.FirstOrDefault(s => s.Event == eventName && matches(s.Payload));
                    if (hit.Event != null)
                    {
                        return hit.Payload;
                    }
                }

                await Task.Delay(50);
            }

            throw new TimeoutException($"No {eventName} matching the predicate was broadcast");
        }
    }
}
