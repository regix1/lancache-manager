using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A game detection is never ended by its age. A new start completes an active detection as failed
/// only when nothing will ever finish it: no worker is recorded for it, or its worker has ended
/// without completing it. A detection whose worker still runs keeps running however long it takes.
/// Each case sets the worker the service recorded, or fails the start before one is recorded, so no
/// test waits on a clock.
/// </summary>
public sealed class GameDetectionStaleWorkerTests : IDisposable
{
    private readonly string _root;
    private readonly List<(OperationStateService Owner, ServiceProvider Services)> _owners = [];

    public GameDetectionStaleWorkerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-detect-stale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var (owner, services) in _owners)
        {
            owner.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            services.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp tree the OS still holds open is not this test's concern.
        }
    }

    [Fact]
    public async Task ALongDetectionWhoseWorkerStillRunsIsNotEnded()
    {
        var (detection, tracker, _) = CreateDetection();
        var runningId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            startedAt: DateTime.UtcNow.AddMinutes(-31));
        var worker = new TaskCompletionSource();
        RecordWorker(detection, runningId, worker.Task);

        Assert.Null(await detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual)));

        Assert.Equal(OperationStatus.Running, tracker.GetOperation(runningId)!.Status);
        Assert.Equal(runningId, Assert.Single(tracker.GetActiveOperations(OperationType.GameDetection)).Id);
    }

    [Fact]
    public async Task ADetectionWhoseStartThrewBeforeItsWorkerIsEndedByTheNextStart()
    {
        var (detection, tracker, notifications) = CreateDetection();
        notifications.FailStart = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual)));
        Assert.Empty(tracker.GetActiveOperations(OperationType.GameDetection));
        var orphanId = Assert.Single(tracker.GetRuns().Runs).OperationId;

        notifications.FailStart = false;
        var nextId = await detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));

        Assert.NotNull(nextId);
        Assert.NotEqual(orphanId, nextId);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(orphanId)!.Status);
    }

    [Fact]
    public async Task ADetectionWhoseWorkerEndedWithoutCompletingItIsEndedByTheNextStart()
    {
        var (detection, tracker, _) = CreateDetection();
        var abandonedId = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        RecordWorker(detection, abandonedId, Task.CompletedTask);

        var nextId = await detection.StartDetectionAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));

        Assert.NotNull(nextId);
        Assert.NotEqual(abandonedId, nextId);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(abandonedId)!.Status);
    }

    private static void RecordWorker(GameCacheDetectionService detection, Guid operationId, Task worker)
        => typeof(GameCacheDetectionService)
            .GetField("_currentDetectionTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(detection, ((Guid, Task)?)(operationId, worker));

    /// <summary>
    /// A real detection service over one monolithic log source, so its capability gate lets a start
    /// through, and a real tracker. The background run a successful start launches fails at once on
    /// its missing database and completes its own operation, which none of these tests read.
    /// </summary>
    private (GameCacheDetectionService Detection, UnifiedOperationTracker Tracker, StartFailingNotifications Notifications) CreateDetection()
    {
        var pathResolver = new TempDirPathResolver(_root);
        var configuration = new ConfigurationBuilder().Build();
        var stateService = StateTestMethods.CreateStateService(_root);
        var datasourceService = new DatasourceService(configuration, pathResolver, NullLogger<DatasourceService>.Instance);
        var datasource = Assert.Single(datasourceService.GetDatasources());
        Directory.CreateDirectory(datasource.LogPath);
        File.WriteAllText(Path.Combine(datasource.LogPath, "access.log"), string.Empty);

        var notifications = (StartFailingNotifications)(object)DispatchProxy
            .Create<ISignalRNotificationService, StartFailingNotifications>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        var capabilityService = new DatasourceCapabilityService(datasourceService);

        GameCacheDetectionService? repairOwner = null;
        var services = new ServiceCollection()
            .AddSingleton(datasourceService)
            .AddSingleton(capabilityService)
            .AddSingleton<GameCacheDetectionService>(_ => repairOwner
                ?? throw new InvalidOperationException("The game detection service is not initialized."))
            .BuildServiceProvider();
        var operationStateService = new OperationStateService(
            NullLogger<OperationStateService>.Instance,
            configuration,
            stateService,
            services.GetRequiredService<IServiceScopeFactory>(),
            new HostLifetime(),
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            tracker);

        var detection = new GameCacheDetectionService(
            NullLogger<GameCacheDetectionService>.Instance,
            pathResolver,
            operationStateService,
            dbContextFactory: null!,
            detectionDataService: null!,
            evictedDetectionPreservationService: null!,
            unknownGameResolutionService: null!,
            rustProcessHelper: null!,
            (ISignalRNotificationService)(object)notifications,
            datasourceService,
            capabilityService,
            tracker,
            CacheScanGateHarness.Idle());
        repairOwner = detection;
        services.GetRequiredService<GameCacheDetectionService>();
        operationStateService.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        _owners.Add((operationStateService, services));
        return (detection, tracker, notifications);
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
    /// Fails the detection's Started announcement while <see cref="FailStart"/> is set, which is the
    /// one step between registering a detection and starting its worker that can throw. Not sealed
    /// for DispatchProxy.
    /// </summary>
    private class StartFailingNotifications : NullReturningProxy
    {
        public bool FailStart { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (FailStart
                && targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args?[0] as string == SignalREvents.GameDetectionStarted)
            {
                return Task.FromException(new InvalidOperationException("The hub refused the Started announcement."));
            }

            return base.Invoke(targetMethod, args);
        }
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
