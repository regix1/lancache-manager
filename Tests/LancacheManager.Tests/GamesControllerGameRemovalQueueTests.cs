using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Configuration;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class GamesControllerGameRemovalQueueTests : IDisposable
{
    private readonly string _root;
    private readonly RecordingOperationQueue _queue;
    private readonly GamesController _controller;

    public GamesControllerGameRemovalQueueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-game-removal-queue-" + Guid.NewGuid().ToString("N"));
        var cachePath = Path.Combine(_root, "cache");
        var logPath = Path.Combine(_root, "logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();

        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;

        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);
        var capabilityService = new DatasourceCapabilityService(datasourceService);

        _queue = new RecordingOperationQueue(new QueuedOperationResponse
        {
            OperationId = Guid.NewGuid(),
            Queued = true,
            Status = "waiting"
        });

        var conflict = CreateProxy<IOperationConflictChecker>((method, _) =>
        {
            if (method.Name == nameof(IOperationConflictChecker.CheckAsync))
            {
                return Task.FromResult<OperationConflictResponse?>(new OperationConflictResponse
                {
                    StageKey = "errors.conflict.overlappingEntity",
                    Error = "blocked"
                });
            }

            return DefaultReturn(method.ReturnType);
        });

        _controller = new GamesController(
            gameCacheDetectionService: CreateCachedDetectionService(),
            gameDetectionService: null!,
            cacheManagementService: null!,
            notifications: CreateProxy<ISignalRNotificationService>((method, _) => DefaultReturn(method.ReturnType)),
            logger: NullLogger<GamesController>.Instance,
            pathResolver: pathResolver,
            operationTracker: CreateProxy<IUnifiedOperationTracker>((method, _) => DefaultReturn(method.ReturnType)),
            conflictChecker: conflict,
            operationQueue: _queue,
            capabilityService: capabilityService,
            cacheScanGate: CacheScanGateHarness.Idle());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveGameFromCache_WhenConflict_EnqueuesSteamGameRemovalAsync()
    {
        var result = await _controller.RemoveGameFromCacheAsync(
            570,
            CancellationToken.None,
            CacheRemovalScope.CacheFiles);

        AssertQueuedGameRemoval(result, ConflictScope.SteamGame(570));
    }

    [Fact]
    public async Task RemoveEpicGameFromCache_WhenConflict_EnqueuesEpicGameRemovalAsync()
    {
        var result = await _controller.RemoveEpicGameFromCacheAsync(
            "Fortnite",
            CancellationToken.None,
            CacheRemovalScope.CacheFiles);

        AssertQueuedGameRemoval(result, ConflictScope.EpicGame("cat-fortnite", "Fortnite"));
    }

    [Fact]
    public async Task RemoveNamedGameFromCache_WhenConflict_EnqueuesNamedGameRemovalAsync()
    {
        var result = await _controller.RemoveNamedGameFromCacheAsync(
            "blizzard",
            "Diablo IV",
            CancellationToken.None,
            CacheRemovalScope.CacheFiles);

        AssertQueuedGameRemoval(result, ConflictScope.NamedGame("blizzard", "Diablo IV"));
    }

    [Fact]
    public void StartRemoval_StartedPayload_UsesPlatformIdentityFields()
    {
        var source = ReadSource("Controllers", "Cache", "GamesController.cs");

        Assert.Contains("GameAppId: isNameKeyed ? null : appId", source, StringComparison.Ordinal);
        Assert.Contains("EpicAppId: isEpic ? epicAppId : null", source, StringComparison.Ordinal);
        Assert.Contains("GameName: displayName", source, StringComparison.Ordinal);
        Assert.Contains("Service: service", source, StringComparison.Ordinal);
        Assert.Contains("StartedEventName: SignalREvents.GameRemovalStarted", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRemovalRepair_RetainsNativeReceiptPath()
    {
        var cachePath = Path.Combine(_root, "receipt-cache");
        var logPath = Path.Combine(_root, "receipt-logs");
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(logPath);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = cachePath,
                ["LanCache:DataSources:0:LogPath"] = logPath,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var pathResolver = DispatchProxy.Create<IPathResolver, PathResolverProxy>();
        ((PathResolverProxy)(object)pathResolver).Root = _root;
        var datasourceService = new DatasourceService(
            configuration,
            pathResolver,
            NullLogger<DatasourceService>.Instance);
        var manager = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(
            typeof(CacheManagementService));
        typeof(CacheManagementService)
            .GetField("_datasourceService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, datasourceService);
        typeof(CacheManagementService)
            .GetField("_capabilityService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, new DatasourceCapabilityService(datasourceService));
        var operationId = Guid.NewGuid();

        var repair = manager.BuildRemovalRepair(
            operationId,
            OperationType.GameRemoval,
            "Game Removal",
            new RemovalMetrics { EntityKey = "570" },
            new CacheRepairTarget { SteamAppId = 570 });

        var source = Assert.Single(repair.Sources);
        Assert.Equal(
            Path.Combine(cachePath, $".lancache-repair-{operationId:N}.json"),
            source.ReceiptPath);
    }

    // After a reload the recovered removal names its service, so a same-named game on another
    // service is not shown busy.
    [Fact]
    public void ActiveRemovals_RecoverTheRemovedGamesService()
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        tracker.RegisterOperation(OperationType.GameRemoval, "Game Removal", new CancellationTokenSource(),
            new RemovalMetrics { EntityKey = "xboxlive:Diablo IV", EntityName = "Diablo IV", EntityKind = "named", Service = "xboxlive" });
        var controller = new CacheController(
            cacheService: null!,
            cacheClearingService: null!,
            corruptionDetectionService: null!,
            logger: NullLogger<CacheController>.Instance,
            pathResolver: null!,
            notifications: null!,
            rustProcessHelper: null!,
            nginxLogRotationService: null!,
            operationTracker: tracker,
            datasourceService: null!,
            dbContextFactory: null!,
            reconciliationService: null!,
            conflictChecker: null!,
            operationQueue: null!,
            capabilityService: null!,
            cacheScanGate: CacheScanGateHarness.Idle(),
            cacheSizeScan: null!,
            operationStateService: null!);

        var body = Assert.IsType<AllActiveRemovalsResponse>(Assert.IsType<OkObjectResult>(controller.GetAllActiveRemovals()).Value);

        var removal = Assert.Single(body.GameRemovals!);
        Assert.Equal("xboxlive", removal.Service);
        Assert.Equal("Diablo IV", removal.GameName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemovalRunner_ExternalTerminalRejectsLateProgressAndWorkerResult(bool cancelled)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tracked = CreateProxy<IUnifiedOperationTracker>((method, args) =>
            method.Invoke(tracker, args));
        var messages = new List<(string Event, object Value)>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name is nameof(ISignalRNotificationService.NotifyAllAsync)
                or nameof(ISignalRNotificationService.NotifyOperationFailedAsync))
            {
                lock (messages) messages.Add(((string)args![0]!, args[1]!));
            }
            return DefaultReturn(method.ReturnType);
        });
        var metrics = new RemovalMetrics { EntityKey = "570" };
        var finalMetricsApplied = 0;
        var config = new RemovalOperationConfig<int>(
            OperationType.GameRemoval, "Game Removal", metrics,
            "started", id => id,
            "progress", "starting", id => id,
            (id, progress) => progress,
            "complete", "finalizing", (id, report) => report,
            (id, report) => new SignalRNotifications.GameRemovalComplete(true, id, 570, null, "complete", FilesDeleted: report),
            id => new SignalRNotifications.GameRemovalComplete(false, id, 570, null, "cancelled", Cancelled: true),
            (id, exception) => exception.Message,
            (id, exception) => new SignalRNotifications.GameRemovalComplete(false, id, 570, null, "failed", Error: exception.Message),
            async (_, _, report) =>
            {
                await report(new(12, "removing", FilesDeleted: 2));
                entered.TrySetResult();
                await release.Task;
                await report(new(90, "late", FilesDeleted: 90));
                return 99;
            },
            BuildRepair: id => new OperationRepair
            {
                Id = id,
                Type = OperationType.GameRemoval,
                Name = "Game Removal",
                StartedAt = DateTime.UtcNow,
                Target = new CacheRepairTarget { SteamAppId = 570 },
                Removal = new RemovalRepair { EntityKey = "570" }
            },
            PrepareRepairAsync: (_, _) => Task.CompletedTask,
            FinishRepairAsync: (_, _, _, _) =>
            {
                finalized.TrySetResult();
                return Task.CompletedTask;
            },
            ApplyProgressMetrics: (current, progress) => current.FilesDeleted = progress.FilesDeleted,
            ApplyFinalMetrics: (current, report) =>
            {
                current.FilesDeleted = report;
                finalMetricsApplied++;
            });

        var operationId = await TrackedRemovalOperationRunner.StartAsync(tracked, notifications, config);
        // A removal belongs to no schedule: it carries no notice and is always a full card.
        Assert.Null(tracker.GetOperation(operationId)!.Notice);
        Assert.Equal(RunVisibility.Card, Assert.Single(tracker.GetRuns().Runs, run => run.OperationId == operationId).Visibility);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        tracker.CompleteOperation(operationId, success: false, error: cancelled ? null : "disk failure", cancelled: cancelled);
        var completedAt = tracker.GetOperation(operationId)!.CompletedAt;
        release.TrySetResult();
        await finalized.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, metrics.FilesDeleted);
        Assert.Equal(0, finalMetricsApplied);
        Assert.Equal(completedAt, tracker.GetOperation(operationId)!.CompletedAt);
        Assert.Equal(cancelled ? OperationStatus.Cancelled : OperationStatus.Failed, tracker.GetOperation(operationId)!.Status);
        lock (messages)
        {
            var terminal = Assert.IsType<SignalRNotifications.GameRemovalComplete>(Assert.Single(messages, item => item.Event == "complete").Value);
            Assert.Equal(operationId, terminal.OperationId);
            Assert.Equal(cancelled, terminal.Cancelled);
            Assert.Equal(cancelled ? null : "disk failure", terminal.Error);
            Assert.DoesNotContain(messages, item => item.Value is RemovalProgressUpdate { StageKey: "late" });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoredRemovalPublishesRetainedMetrics(bool cancelled)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var emitted = new TaskCompletionSource<SignalRNotifications.GameRemovalComplete>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args![1] is SignalRNotifications.GameRemovalComplete complete)
            {
                emitted.TrySetResult(complete);
            }
            return DefaultReturn(method.ReturnType);
        });
        var manager = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(
            typeof(CacheManagementService));
        typeof(CacheManagementService)
            .GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, tracker);
        typeof(CacheManagementService)
            .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, notifications);
        var operationId = Guid.NewGuid();
        var repair = new OperationRepair
        {
            Id = operationId,
            Type = OperationType.GameRemoval,
            Name = "Game Removal: Dota 2",
            StartedAt = DateTime.UtcNow.AddMinutes(-1),
            Outcome = cancelled ? OperationStatus.Cancelled : OperationStatus.Completed,
            Target = new CacheRepairTarget { SteamAppId = 570 },
            Removal = new RemovalRepair
            {
                EntityKey = "570",
                EntityName = "Dota 2",
                EntityKind = "steam",
                FilesDeleted = 4,
                BytesFreed = 2_048,
                LogEntriesRemoved = 7
            }
        };

        await manager.RestoreRepairAsync(repair, CancellationToken.None);
        tracker.CompleteOperation(
            operationId,
            success: !cancelled,
            cancelled: cancelled);
        var complete = await emitted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(operationId, complete.OperationId);
        Assert.Equal(570L, complete.GameAppId);
        Assert.Equal("Dota 2", complete.GameName);
        Assert.Equal(4, complete.FilesDeleted);
        Assert.Equal(2_048L, complete.BytesFreed);
        Assert.Equal(7UL, complete.LogEntriesRemoved);
        Assert.Equal(cancelled, complete.Cancelled);
        Assert.Equal(!cancelled, complete.Success);
    }

    private void AssertQueuedGameRemoval(IActionResult result, ConflictScope expectedScope)
    {
        var accepted = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(202, accepted.StatusCode);
        var body = Assert.IsType<QueuedOperationResponse>(accepted.Value);
        Assert.True(body.Queued);
        Assert.Equal(_queue.Response.OperationId, body.OperationId);
        Assert.Equal(OperationType.GameRemoval, _queue.Type);
        Assert.Equal(expectedScope, _queue.Scope);
        Assert.NotNull(_queue.Start);
    }

    private static GameCacheDetectionService CreateCachedDetectionService()
    {
        var service = (GameCacheDetectionService)RuntimeHelpers.GetUninitializedObject(
            typeof(GameCacheDetectionService));
        typeof(GameCacheDetectionService)
            .GetField("_detectionCacheLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, new SemaphoreSlim(1, 1));
        typeof(GameCacheDetectionService)
            .GetField("_cachedDetectionResponse", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(
                service,
                new GameCacheDetectionService.DetectionOperationResponse
                {
                    Games =
                    [
                        new GameCacheInfo
                        {
                            GameAppId = 570,
                            GameName = "Dota 2",
                            Service = "steam"
                        },
                        new GameCacheInfo
                        {
                            GameAppId = 0,
                            GameName = "Fortnite",
                            Service = "epicgames",
                            EpicAppId = "cat-fortnite"
                        },
                        new GameCacheInfo
                        {
                            GameAppId = 0,
                            GameName = "Diablo IV",
                            Service = "blizzard"
                        }
                    ]
                });
        return service;
    }

    private static string ReadSource(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "lancache-manager.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
        return File.ReadAllText(Path.Combine([root, "Api", "LancacheManager", .. pathSegments]));
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, ProxyDispatch<T>>();
        ((ProxyDispatch<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static object? DefaultReturn(Type returnType)
    {
        if (returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
        {
            var resultType = returnType.GetGenericArguments()[0];
            var fromResult = typeof(Task)
                .GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(resultType);
            return fromResult.Invoke(null, [DefaultValue(resultType)]);
        }

        return DefaultValue(returnType);
    }

    private static object? DefaultValue(Type type)
        => !type.IsValueType || Nullable.GetUnderlyingType(type) != null
            ? null
            : Activator.CreateInstance(type);

    private class ProxyDispatch<T> : DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }

    private sealed class RecordingOperationQueue(QueuedOperationResponse response) : IOperationQueue
    {
        public QueuedOperationResponse Response { get; } = response;
        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct,
            bool reportRefusal = false,
            RunNotice? notice = null)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            return Task.FromResult(Response);
        }
    }
}

internal sealed class RemovalRepairHarness : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly Lifetime _lifetime;
    private readonly string _root;
    private readonly DatasourceService _datasources;
    private readonly DatasourceCapabilityService _capability;
    private readonly StateService _state;
    private readonly Notifications _notificationState;
    private readonly RemovalRustProcessHelper? _rust;

    private RemovalRepairHarness(
        string root,
        ServiceProvider services,
        Lifetime lifetime,
        DatasourceService datasources,
        DatasourceCapabilityService capability,
        StateService state,
        Notifications notificationState,
        RemovalRustProcessHelper? rust,
        CacheManagementService manager,
        OperationStateService owner,
        UnifiedOperationTracker tracker,
        ISignalRNotificationService notifications)
    {
        _root = root;
        _services = services;
        _lifetime = lifetime;
        _datasources = datasources;
        _capability = capability;
        _state = state;
        _notificationState = notificationState;
        _rust = rust;
        Manager = manager;
        Owner = owner;
        Tracker = tracker;
        NotificationService = notifications;
    }

    internal CacheManagementService Manager { get; }
    internal OperationStateService Owner { get; }
    internal UnifiedOperationTracker Tracker { get; }
    internal ISignalRNotificationService NotificationService { get; }

    internal static Task<RemovalRepairHarness> CreateAsync(string root)
    {
        return CreateAsync(root, null);
    }

    internal static Task<RemovalRepairHarness> CreateProducerAsync(
        string root,
        OperationType type)
    {
        return CreateAsync(root, type);
    }

    private static async Task<RemovalRepairHarness> CreateAsync(
        string root,
        OperationType? producerType)
    {
        Directory.CreateDirectory(root);
        var alphaCache = Path.Combine(root, "alpha-cache");
        var alphaLogs = Path.Combine(root, "alpha-logs");
        var betaCache = Path.Combine(root, "beta-cache");
        var betaLogs = Path.Combine(root, "beta-logs");
        foreach (var path in new[] { alphaCache, alphaLogs, betaCache, betaLogs })
        {
            Directory.CreateDirectory(path);
        }
        if (producerType.HasValue)
        {
            File.WriteAllText(Path.Combine(alphaLogs, "access.log"), "GET /alpha HTTP/1.1\n");
            File.WriteAllText(Path.Combine(betaLogs, "access.log"), "GET /beta HTTP/1.1\n");
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LanCache:DataSources:0:Name"] = "alpha",
                ["LanCache:DataSources:0:CachePath"] = alphaCache,
                ["LanCache:DataSources:0:LogPath"] = alphaLogs,
                ["LanCache:DataSources:0:Enabled"] = "true",
                ["LanCache:DataSources:0:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic,
                ["LanCache:DataSources:1:Name"] = "beta",
                ["LanCache:DataSources:1:CachePath"] = betaCache,
                ["LanCache:DataSources:1:LogPath"] = betaLogs,
                ["LanCache:DataSources:1:Enabled"] = "true",
                ["LanCache:DataSources:1:SchemeOverride"] = DatasourceSchemeOverrideValues.Monolithic
            })
            .Build();
        var paths = DispatchProxy.Create<IPathResolver, Paths>();
        ((Paths)(object)paths).Root = root;
        var datasources = new DatasourceService(
            configuration,
            paths,
            NullLogger<DatasourceService>.Instance);
        var capability = new DatasourceCapabilityService(datasources);
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        CacheManagementService? manager = null;
        var notificationService = DispatchProxy.Create<ISignalRNotificationService, Notifications>();
        var notificationState = (Notifications)(object)notificationService;
        var registrations = new ServiceCollection()
            .AddSingleton<CacheManagementService>(_ => manager!)
            .AddSingleton(datasources)
            .AddSingleton(capability);
        if (producerType.HasValue)
        {
            // A log step deletes rows with set-based deletes, which need a relational database.
            // Built through the provider, so disposing the harness drops its schema.
            var database = await TestDatabase.CreateAsync();
            registrations.AddSingleton(_ => database);
        }
        var services = registrations.BuildServiceProvider();
        var state = OperationRepairTests.CreateFailingStateService(Path.Combine(root, "retained"));
        state.SetSetupCompleted(true);
        var lifetime = new Lifetime();
        var owner = new OperationStateService(
            NullLogger<OperationStateService>.Instance,
            configuration,
            state,
            services.GetRequiredService<IServiceScopeFactory>(),
            lifetime,
            processManager,
            tracker);
        RemovalRustProcessHelper? rust = null;
        if (producerType.HasValue)
        {
            File.WriteAllText(paths.GetRustSteamRemoverPath(), string.Empty);
            File.WriteAllText(paths.GetRustServiceRemoverPath(), string.Empty);
            var contexts = services.GetRequiredService<TestDatabase>().Factory;
            rust = new RemovalRustProcessHelper(
                root,
                producerType.Value,
                paths,
                tracker,
                owner,
                state,
                contexts);
            manager = new CacheManagementService(
                configuration,
                NullLogger<CacheManagementService>.Instance,
                paths,
                rust,
                new NginxLogRotationService(
                    NullLogger<NginxLogRotationService>.Instance,
                    configuration,
                    processManager,
                    paths,
                    TimeProvider.System),
                datasources,
                state,
                contexts,
                gameCacheDetectionService: null!,
                tracker,
                notificationService,
                DispatchProxy.Create<ILancacheEnvFileReader, NullReturningProxy>(),
                DispatchProxy.Create<IOperationConflictChecker, NullReturningProxy>(),
                capability,
                CacheScanGateHarness.Idle(),
                owner);
        }
        else
        {
            manager = (CacheManagementService)RuntimeHelpers.GetUninitializedObject(
                typeof(CacheManagementService));
            typeof(CacheManagementService)
                .GetField("_operationStateService", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(manager, owner);
        }
        await owner.StartAsync(CancellationToken.None);
        return new RemovalRepairHarness(
            root,
            services,
            lifetime,
            datasources,
            capability,
            state,
            notificationState,
            rust,
            manager!,
            owner,
            tracker,
            notificationService);
    }

    internal RemovalOperationConfig<(int Files, long Bytes)> CreateConfig(
        OperationType type,
        RemovalMetrics metrics,
        Func<Guid, CancellationToken, Func<RemovalProgressUpdate, Task>,
            Task<(int Files, long Bytes)>> execute)
    {
        return new RemovalOperationConfig<(int Files, long Bytes)>(
            type,
            type == OperationType.ServiceRemoval ? "Service removal" : "Game removal",
            metrics,
            "started",
            id => id,
            "progress",
            "starting",
            id => id,
            (_, update) => update,
            "complete",
            "finalizing",
            (_, report) => report,
            (id, report) => Complete(type, id, true, false, null, report.Files, report.Bytes),
            id => Complete(type, id, false, true, null, metrics.FilesDeleted, metrics.BytesFreed),
            (_, exception) => exception.Message,
            (id, exception) => Complete(
                type,
                id,
                false,
                false,
                exception.Message,
                metrics.FilesDeleted,
                metrics.BytesFreed),
            execute,
            id => BuildRepair(id, type, metrics),
            Owner.PrepareRepairAsync,
            Manager.FinishRemovalRepairAsync,
            ApplyProgressMetrics: (current, update) =>
            {
                current.FilesDeleted = update.FilesDeleted;
                current.BytesFreed = update.BytesFreed;
            },
            ApplyFinalMetrics: (current, report) =>
            {
                current.FilesDeleted = report.Files;
                current.BytesFreed = report.Bytes;
            });
    }

    internal async Task SaveSourceAsync(
        Guid operationId,
        string datasource,
        int filesDeleted,
        long bytesFreed)
    {
        var method = typeof(CacheManagementService).GetMethod(
            "SaveRemovalSourceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(
            Manager,
            [operationId, datasource, filesDeleted, bytesFreed, Array.Empty<uint>()])!;
        // A source's log step runs right after its cache step is accepted, so a source this
        // harness accepts has finished both and owes the repair no log step.
        await Owner.MarkLogRewriteStartedAsync(operationId, datasource);
        await Owner.MarkLogPositionsKeptAsync(operationId, datasource);
    }

    internal OperationRepair ReadRepair(Guid operationId)
    {
        return _state.LoadOperationRepairs().Single(repair => repair.Id == operationId);
    }

    internal async Task<OperationRepair> WaitForCompletedRepairAsync(Guid operationId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var repair = ReadRepair(operationId);
            if (repair.Phase == OperationRepairPhase.Completed)
            {
                return repair;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Removal repair {operationId} did not complete.");
    }

    internal IReadOnlyList<(string Event, object? Value)> ReadMessages()
    {
        return _notificationState.ReadMessages();
    }

    internal string BetaStage => _rust!.BetaStage;
    internal int RawFilesProcessed => _rust!.RawFilesProcessed;
    internal int RustCalls => _rust!.Runs;
    internal RemovalRustProcessHelper Rust => _rust!;

    internal Task WaitForBetaProgressAsync()
    {
        return _rust!.BetaProgress.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal void ReleaseRust()
    {
        _rust!.Release();
    }

    internal Task WaitForRustExitAsync()
    {
        return _rust!.BetaExit.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal async Task<OperationInfo> WaitForTerminalAsync(Guid operationId)
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var operation = Tracker.GetOperation(operationId);
            if (operation?.Status.IsTerminal() == true)
            {
                return operation;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Removal operation {operationId} did not reach a terminal state.");
    }

    internal async Task<object?> WaitForCompleteMessageAsync()
    {
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var complete = ReadMessages()
                .Where(message => message.Event == "complete")
                .Select(message => message.Value)
                .ToList();
            if (complete.Count == 1)
            {
                return complete[0];
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("Removal completion notification was not emitted once.");
    }

    internal async Task CompleteAnotherAsync(OperationType type)
    {
        var operationId = await PrepareAnotherAsync(type);
        await Owner.FinishRepairAsync(operationId, true, false, null);
    }

    internal async Task<Guid> PrepareAnotherAsync(OperationType type)
    {
        var operationId = Guid.NewGuid();
        var metrics = new RemovalMetrics
        {
            EntityKey = operationId.ToString("N"),
            EntityName = "next removal",
            EntityKind = type == OperationType.ServiceRemoval ? "service" : "steam"
        };
        await Owner.PrepareRepairAsync(BuildRepair(operationId, type, metrics), CancellationToken.None);
        return operationId;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.StopApplication();
        await Owner.StopAsync(CancellationToken.None);
        await _services.DisposeAsync();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private OperationRepair BuildRepair(Guid operationId, OperationType type, RemovalMetrics metrics)
    {
        return new OperationRepair
        {
            Id = operationId,
            Type = type,
            Name = type == OperationType.ServiceRemoval ? "Service removal" : "Game removal",
            StartedAt = DateTime.UtcNow,
            Target = type == OperationType.ServiceRemoval
                ? new CacheRepairTarget { Service = "steam" }
                : new CacheRepairTarget { SteamAppId = 570 },
            Removal = new RemovalRepair
            {
                EntityKey = metrics.EntityKey,
                EntityName = metrics.EntityName,
                EntityKind = metrics.EntityKind,
                Service = metrics.Service,
                EpicAppId = metrics.EpicAppId
            },
            Sources = _datasources.GetDatasources()
                .Select(datasource => new OperationRepairSource
                {
                    Datasource = datasource.Name,
                    LogRoot = datasource.LogPath,
                    CacheRoot = datasource.CachePath,
                    KeyScheme = _capability.GetKeySchemeWireValue(datasource)
                })
                .ToList()
        };
    }

    private static IOperationComplete Complete(
        OperationType type,
        Guid operationId,
        bool success,
        bool cancelled,
        string? error,
        int filesDeleted,
        long bytesFreed)
    {
        if (type == OperationType.ServiceRemoval)
        {
            return new SignalRNotifications.ServiceRemovalComplete(
                success,
                "steam",
                operationId,
                success ? "complete" : cancelled ? "cancelled" : "failed",
                filesDeleted,
                bytesFreed,
                Error: error,
                Cancelled: cancelled);
        }
        return new SignalRNotifications.GameRemovalComplete(
            success,
            operationId,
            570,
            null,
            success ? "complete" : cancelled ? "cancelled" : "failed",
            FilesDeleted: filesDeleted,
            BytesFreed: bytesFreed,
            Error: error,
            Cancelled: cancelled);
    }

    /// <summary>Whether a log step holds the logs when a child launches, given 200 ms to show it.</summary>
    internal static async Task<bool> StepHeldAsync(OperationStateService owner)
    {
        using var probe = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        try
        {
            await owner.WaitForLogStepAsync(active: true, probe.Token);
            return true;
        }
        catch (OperationCanceledException) when (probe.IsCancellationRequested)
        {
            return false;
        }
    }

    internal sealed class RemovalRustProcessHelper : RustProcessHelper
    {
        private readonly string _root;
        private readonly string _alphaLogs;
        private readonly string _betaLogs;
        private readonly string _expectedBinary;
        private readonly string _expectedLabel;
        private readonly string _purgeBinary;
        private readonly OperationType _type;
        private readonly OperationStateService _owner;
        private readonly TaskCompletionSource _betaProgress =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _betaExit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;
        private int _purges;

        internal RemovalRustProcessHelper(
            string root,
            OperationType type,
            IPathResolver paths,
            IUnifiedOperationTracker tracker,
            OperationStateService owner,
            OperationRepairTests.FailingStateService state,
            TestDbContextFactory contexts)
            : base(
                NullLogger<RustProcessHelper>.Instance,
                new ProcessManager(NullLogger<ProcessManager>.Instance),
                paths,
                tracker)
        {
            _root = Path.GetFullPath(root);
            _alphaLogs = Path.GetFullPath(Path.Combine(root, "alpha-logs"));
            _betaLogs = Path.GetFullPath(Path.Combine(root, "beta-logs"));
            _expectedBinary = type == OperationType.GameRemoval
                ? paths.GetRustSteamRemoverPath()
                : paths.GetRustServiceRemoverPath();
            _expectedLabel = type == OperationType.GameRemoval
                ? "game_cache_remover"
                : "service_remover";
            _purgeBinary = paths.GetRustLogPurgePath();
            _type = type;
            _owner = owner;
            State = state;
            Contexts = contexts;
            BetaStage = type == OperationType.GameRemoval
                ? "tests.gameRemove.betaRaw"
                : "tests.serviceRemove.betaRaw";
        }

        internal string BetaStage { get; }
        internal int RawFilesProcessed { get; private set; }
        internal int Runs => Volatile.Read(ref _runs);
        internal Task BetaProgress => _betaProgress.Task;
        internal Task BetaExit => _betaExit.Task;
        internal OperationRepairTests.FailingStateService State { get; }
        internal TestDbContextFactory Contexts { get; }

        /// <summary>Beta's cache step succeeds instead of reporting progress and failing on release.</summary>
        internal bool BetaSucceeds { get; set; }

        /// <summary>Runs inside each remover launch, after its arguments are checked.</summary>
        internal Func<int, Task>? OnRemoverRun { get; set; }

        internal List<string> ReportUrls { get; } = [];
        internal List<uint> ReportDepotIds { get; } = [];

        /// <summary>The first this many purge launches exit with a failure.</summary>
        internal int FailingPurges { get; set; }

        /// <summary>The first this many purge launches run until the cancel and publish nothing, as a killed child does.</summary>
        internal int HeldPurges { get; set; }
        internal long PurgeLinesRemoved { get; set; }

        /// <summary>
        /// When set, a purge that publishes removes the first line of its datasource's access.log,
        /// reports it per source as the binary does, then runs this with the datasource and the lines
        /// it removed below the stored position.
        /// </summary>
        internal Action<string, long>? OnLinePurged { get; set; }
        internal System.Collections.Concurrent.ConcurrentQueue<RemovalLaunch> Launches { get; } = new();

        internal void Release()
        {
            _release.TrySetResult();
        }

        public override async Task<ProcessExecutionResult> ExecuteTrackedProcessWithProgressEventsAsync(
            ProcessStartInfo start,
            Guid? operationId,
            CancellationToken cancellationToken,
            Func<RustProgressEvent, Task>? onProgressEvent,
            string processLabel = "rust")
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(operationId);
            var stepHeld = await StepHeldAsync(_owner);
            if (start.FileName == _purgeBinary)
            {
                Assert.Equal("cache_purge_log_entries", processLabel);
                return await PurgeAsync(start, stepHeld, cancellationToken);
            }

            Assert.Equal(_expectedBinary, start.FileName);
            AssertOwnedPath(start.FileName);
            Assert.Equal(_expectedLabel, processLabel);
            // The cache step neither rewrites a log nor publishes a replaced one.
            Assert.DoesNotContain("--stem-positions", start.Arguments, StringComparison.Ordinal);
            Assert.False(start.Environment.ContainsKey("LANCACHE_LOG_CHECK"));
            Launches.Enqueue(new RemovalLaunch(Purge: false, stepHeld, PurgeInput: null));
            var run = Interlocked.Increment(ref _runs);
            Assert.InRange(run, 1, 2);
            try
            {
                var quoted = Regex.Matches(start.Arguments, "\"([^\"]*)\"")
                    .Select(match => match.Groups[1].Value)
                    .ToArray();
                Assert.True(quoted.Length >= 5, start.Arguments);
                foreach (var path in new[] { quoted[0], quoted[1], quoted[3], quoted[4] })
                {
                    AssertOwnedPath(path);
                }

                var logRoot = Path.GetFullPath(quoted[0]);
                Assert.Equal(run == 1 ? _alphaLogs : _betaLogs, logRoot);
                Assert.Equal(
                    _type == OperationType.GameRemoval ? "570" : "steam",
                    quoted[2]);
                if (OnRemoverRun != null)
                {
                    await OnRemoverRun(run);
                }

                if (run == 1 || BetaSucceeds)
                {
                    await WriteReportAsync(quoted[3], 9, 200, cancellationToken);
                    return new ProcessExecutionResult { ExitCode = 0 };
                }

                const int rawFilesProcessed = 1;
                await File.WriteAllTextAsync(
                    quoted[4],
                    JsonSerializer.Serialize(new
                    {
                        status = "running",
                        message = "beta raw progress",
                        stageKey = BetaStage,
                        context = new Dictionary<string, object?> { ["datasource"] = "beta" },
                        percentComplete = 25.0,
                        filesProcessed = rawFilesProcessed,
                        totalFiles = 2
                    }),
                    cancellationToken);
                using (var progressFile = JsonDocument.Parse(
                           await File.ReadAllTextAsync(quoted[4], cancellationToken)))
                {
                    RawFilesProcessed = progressFile.RootElement
                        .GetProperty("filesProcessed")
                        .GetInt32();
                }
                var progressEvent = Assert.IsType<Func<RustProgressEvent, Task>>(onProgressEvent);
                await progressEvent(new RustProgressEvent
                {
                    Event = "progress",
                    OperationId = operationId.Value.ToString(),
                    PercentComplete = 25.0,
                    Status = "running",
                    StageKey = BetaStage
                });
                _betaProgress.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
                await WriteReportAsync(quoted[3], 0, 0, cancellationToken);
                return new ProcessExecutionResult
                {
                    ExitCode = 17,
                    Error = "Injected beta removal failure."
                };
            }
            finally
            {
                if (run == 2)
                {
                    _betaExit.TrySetResult();
                }
            }
        }

        private async Task<ProcessExecutionResult> PurgeAsync(
            ProcessStartInfo start,
            bool stepHeld,
            CancellationToken cancellationToken)
        {
            var quoted = Regex.Matches(start.Arguments, "\"([^\"]*)\"")
                .Select(match => match.Groups[1].Value)
                .ToArray();
            // A fifth path is the stored positions file, which the state service writes to the system temp folder.
            foreach (var path in quoted.Take(4))
            {
                AssertOwnedPath(path);
            }
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(quoted[1], cancellationToken));
            Launches.Enqueue(new RemovalLaunch(Purge: true, stepHeld, input.RootElement.Clone()));
            var purge = Interlocked.Increment(ref _purges);
            if (purge <= HeldPurges)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            // The binary returns before it publishes anything when it has nothing to match.
            if (input.RootElement.GetProperty("urls").GetArrayLength() == 0
                && input.RootElement.GetProperty("depot_ids").GetArrayLength() == 0)
            {
                await File.WriteAllTextAsync(
                    quoted[2],
                    JsonSerializer.Serialize(new { success = true, lines_removed = 0 }),
                    cancellationToken);
                return new ProcessExecutionResult { ExitCode = 0 };
            }
            await WritePublicationAsync(start, replaced: null, cancellationToken);
            if (purge <= FailingPurges)
            {
                return new ProcessExecutionResult { ExitCode = 3, Error = "Injected purge failure." };
            }
            if (OnLinePurged is not { } linePurged)
            {
                await File.WriteAllTextAsync(
                    quoted[2],
                    JsonSerializer.Serialize(new { success = true, lines_removed = PurgeLinesRemoved }),
                    cancellationToken);
                return new ProcessExecutionResult { ExitCode = 0 };
            }

            // The binary counts a removed line as read when it sits below the stored position, and
            // counts every removed line as read when it was given no positions.
            var accessLog = Path.Combine(quoted[0], "access.log");
            var text = await File.ReadAllTextAsync(accessLog, cancellationToken);
            long removed = text.Length > 0 ? 1 : 0;
            var removedBefore = removed;
            if (quoted.Length > 4)
            {
                var positions = Assert.IsType<Dictionary<string, long>>(
                    JsonSerializer.Deserialize<Dictionary<string, long>>(
                        await File.ReadAllTextAsync(quoted[4], cancellationToken)));
                removedBefore = positions["access.log"] > 0 ? removed : 0;
            }
            // Written outside the log folder, so a replacement a cancel leaves behind is never read as a log.
            var replacement = Path.Combine(_root, "access.log.purged");
            await File.WriteAllTextAsync(replacement, text[(text.IndexOf('\n') + 1)..], cancellationToken);
            await WritePublicationAsync(start, NginxWriterProbe.ReadIdentity(replacement), cancellationToken);
            await File.WriteAllTextAsync(
                quoted[2],
                JsonSerializer.Serialize(new
                {
                    success = true,
                    lines_removed = removed,
                    log_lines_removed_by_source = new Dictionary<string, long> { ["access.log"] = removed },
                    log_lines_removed_before_position_by_source =
                        new Dictionary<string, long> { ["access.log"] = removedBefore }
                }),
                cancellationToken);
            // Published after every write a cancel can stop, so a cancel leaves access.log as it was.
            // The held no-writer proof shares delete but not write, so the old file is deleted first.
            File.Delete(accessLog);
            File.Move(replacement, accessLog);
            linePurged(Path.GetFullPath(quoted[0]) == _alphaLogs ? "alpha" : "beta", removedBefore);
            return new ProcessExecutionResult { ExitCode = 0 };
        }

        private void AssertOwnedPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var rootPrefix = Path.EndsInDirectorySeparator(_root)
                ? _root
                : _root + Path.DirectorySeparatorChar;
            Assert.True(fullPath.StartsWith(rootPrefix, comparison), fullPath);
        }

        // A replaced identity names the one log the harness's log folders hold.
        private async Task WritePublicationAsync(
            ProcessStartInfo start,
            NginxFileIdentity? replaced,
            CancellationToken cancellationToken)
        {
            var checkPath = Assert.IsType<string>(start.Environment["LANCACHE_LOG_CHECK"]);
            var resultPath = Assert.IsType<string>(start.Environment["LANCACHE_LOG_RESULT"]);
            AssertOwnedPath(checkPath);
            AssertOwnedPath(resultPath);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var check = Assert.IsType<NginxPublicationCheckFile>(
                JsonSerializer.Deserialize<NginxPublicationCheckFile>(
                    await File.ReadAllTextAsync(checkPath, cancellationToken),
                    options));
            Assert.True(check.Valid);
            Assert.NotEmpty(check.Files);
            foreach (var expected in check.Files)
            {
                AssertOwnedPath(expected.TargetPath);
            }
            var result = new NginxPublicationResult(
                true,
                check.Files.Select(expected => new NginxPublicationRecord(
                    expected.TargetPath,
                    expected.OriginalIdentity,
                    TemporaryIdentity: replaced,
                    PublishedIdentity: replaced ?? expected.OriginalIdentity,
                    Changed: replaced is not null,
                    Deleted: false)).ToList());
            await File.WriteAllTextAsync(
                resultPath,
                JsonSerializer.Serialize(result, options),
                cancellationToken);
        }

        private async Task WriteReportAsync(
            string path,
            int filesDeleted,
            long bytesFreed,
            CancellationToken cancellationToken)
        {
            string contents;
            if (_type == OperationType.GameRemoval)
            {
                contents = JsonSerializer.Serialize(new CacheManagementService.GameCacheRemovalReport
                {
                    GameAppId = 570,
                    GameName = "Dota 2",
                    CacheFilesDeleted = filesDeleted,
                    TotalBytesFreed = checked((ulong)bytesFreed),
                    PurgeUrls = ReportUrls,
                    PurgeDepotIds = ReportDepotIds
                });
            }
            else
            {
                contents = JsonSerializer.Serialize(new CacheManagementService.ServiceCacheRemovalReport
                {
                    ServiceName = "steam",
                    CacheFilesDeleted = filesDeleted,
                    TotalBytesFreed = checked((ulong)bytesFreed),
                    PurgeUrls = ReportUrls
                });
            }
            await File.WriteAllTextAsync(path, contents, cancellationToken);
        }

        /// <summary>One child launch: the purge or the remover, and whether a log step held the logs.</summary>
        internal sealed record RemovalLaunch(bool Purge, bool StepHeld, JsonElement? PurgeInput);
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            _stopping.Cancel();
        }
    }

    private class Paths : DispatchProxy
    {
        internal string Root { get; set; } = string.Empty;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name == nameof(IPathResolver.ResolvePath))
            {
                var path = Assert.IsType<string>(args![0]);
                return Path.IsPathRooted(path) ? path : Path.Combine(Root, path);
            }
            if (targetMethod.Name == nameof(IPathResolver.NormalizePath))
            {
                return Assert.IsType<string>(args![0]);
            }
            if (targetMethod.Name == nameof(IPathResolver.IsDirectoryWritable))
            {
                return true;
            }
            if (targetMethod.Name == nameof(IPathResolver.IsDockerSocketAvailable))
            {
                return false;
            }
            if (targetMethod.ReturnType == typeof(string))
            {
                return Path.Combine(Root, targetMethod.Name);
            }
            if (targetMethod.ReturnType == typeof(bool))
            {
                return false;
            }
            return null;
        }
    }

    private class Notifications : DispatchProxy
    {
        private readonly List<(string Event, object? Value)> _messages = [];

        internal IReadOnlyList<(string Event, object? Value)> ReadMessages()
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            if (targetMethod.Name is nameof(ISignalRNotificationService.NotifyAllAsync)
                or nameof(ISignalRNotificationService.NotifyOperationFailedAsync))
            {
                lock (_messages)
                {
                    _messages.Add(((string)args![0]!, args[1]));
                }
            }
            return targetMethod.ReturnType == typeof(Task) ? Task.CompletedTask : null;
        }
    }
}
