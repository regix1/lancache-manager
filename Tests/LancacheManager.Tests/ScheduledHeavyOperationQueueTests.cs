using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class ScheduledHeavyOperationQueueTests
{
    [Fact]
    public async Task CacheFileScan_ScheduledRun_EntersUniversalOperationQueueAsync()
    {
        var rustBinaryPath = Path.GetTempFileName();
        try
        {
            var queue = new RecordingOperationQueue();
            var pathResolver = CreateProxy<IPathResolver>((method, _) => method.Name switch
            {
                nameof(IPathResolver.GetRustCacheSizePath) => rustBinaryPath,
                _ => DefaultReturn(method.ReturnType)
            });
            var service = new TestCacheSizeScanScheduledService(
                pathResolver,
                queue,
                CreateStateService());

            await service.InvokeScheduledAsync(CancellationToken.None);

            AssertQueueRequest(queue, OperationType.CacheSizeScan, "Cache File Scan");
        }
        finally
        {
            File.Delete(rustBinaryPath);
        }
    }

    [Fact]
    public async Task GameDetection_ScheduledRun_EntersUniversalOperationQueueAsync()
    {
        var queue = new RecordingOperationQueue();
        var service = new TestGameDetectionService(queue, CreateStateService());

        await service.InvokeScheduledAsync(CancellationToken.None);

        AssertQueueRequest(queue, OperationType.GameDetection, "Game Detection");
    }

    [Fact]
    public async Task EvictionScan_ScheduledRun_EntersUniversalOperationQueueAsync()
    {
        var queue = new RecordingOperationQueue();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"scheduled-eviction-queue-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "127.0.0.1" });
        await context.SaveChangesAsync();

        using var provider = new ServiceCollection()
            .AddSingleton(context)
            .BuildServiceProvider();
        var service = new TestCacheReconciliationService(provider, queue, CreateStateService());

        await service.InvokeScheduledAsync(provider, CancellationToken.None);

        AssertQueueRequest(queue, OperationType.EvictionScan, "Eviction Scan");
    }

    [Fact]
    public async Task EvictionScan_StartupDuplicate_CompletesStartupDependencyWithoutWaitingAsync()
    {
        var activeId = Guid.NewGuid();
        var queue = new RecordingOperationQueue(new QueuedOperationResponse
        {
            OperationId = activeId,
            AlreadyRunning = true,
            Status = "alreadyRunning"
        });
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"startup-eviction-dedupe-{Guid.NewGuid():N}")
            .Options;
        await using var context = new AppDbContext(options);
        context.Downloads.Add(new Download { Service = "steam", ClientIp = "127.0.0.1" });
        await context.SaveChangesAsync();

        using var provider = new ServiceCollection()
            .AddSingleton(context)
            .BuildServiceProvider();
        var service = new TestCacheReconciliationService(provider, queue, CreateStateService());

        await service.InvokeStartupAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        AssertQueueRequest(queue, OperationType.EvictionScan, "Eviction Scan");
        Assert.True(service.FirstStartupScanComplete.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task QueuePromotion_EmitsExplicitPromotedCardHandoffAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(
            tracker,
            NullLogger<OperationConflictChecker>.Instance);
        var handoffReceived = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification handoff)
                {
                    handoffReceived.TrySetResult(handoff);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            notifications,
            NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource());
        var startedId = Guid.NewGuid();
        var queued = await queue.EnqueueAsync(
            OperationType.CacheSizeScan,
            ConflictScope.Bulk(),
            "Cache File Scan",
            () => Task.FromResult<Guid?>(startedId),
            CancellationToken.None);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(blockerId, success: true);

        var handoff = await handoffReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, handoff.OperationId);
        Assert.Equal(OperationType.CacheSizeScan.ToWireString(), handoff.OperationType);
        Assert.True(handoff.Promoted);
        Assert.False(handoff.Cancelled);
        Assert.Null(handoff.Error);
    }

    [Fact]
    public async Task QueuedScheduledOperation_CanBeCancelledBeforePromotionAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(
            tracker,
            NullLogger<OperationConflictChecker>.Instance);
        var cancelledReceived = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification { Cancelled: true } cancelled)
                {
                    cancelledReceived.TrySetResult(cancelled);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            notifications,
            NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource());
        var startCalls = 0;
        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () =>
            {
                Interlocked.Increment(ref startCalls);
                return Task.FromResult<Guid?>(Guid.NewGuid());
            },
            CancellationToken.None);

        Assert.True(queued.Queued);
        Assert.True(tracker.CancelOperation(queued.OperationId));
        var cancelled = await cancelledReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(cancelled.Promoted);

        tracker.CompleteOperation(blockerId, success: true);
        await Task.Delay(200);
        Assert.Equal(0, Volatile.Read(ref startCalls));
    }

    [Fact]
    public async Task PromotionRefusal_RequeuesAndRetriesInsteadOfDroppingScheduledOperationAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(
            tracker,
            NullLogger<OperationConflictChecker>.Instance);
        var handoffReceived = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification { Promoted: true } handoff)
                {
                    handoffReceived.TrySetResult(handoff);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            notifications,
            NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.LogProcessing,
            "Log Processing",
            new CancellationTokenSource());
        var startCalls = 0;
        var startedId = Guid.NewGuid();
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            "Eviction Scan",
            () => Task.FromResult<Guid?>(
                Interlocked.Increment(ref startCalls) == 1 ? null : startedId),
            CancellationToken.None);

        Assert.True(queued.Queued);
        tracker.CompleteOperation(blockerId, success: true);

        var handoff = await handoffReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, handoff.OperationId);
        Assert.Equal(2, Volatile.Read(ref startCalls));
        Assert.True(handoff.Promoted);
    }

    [Fact]
    public async Task ImmediateStartRefusal_ParksAndRetriesInsteadOfDroppingScheduledOperationAsync()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        var tracker = new UnifiedOperationTracker(
            processManager,
            NullLogger<UnifiedOperationTracker>.Instance);
        var conflictChecker = new OperationConflictChecker(
            tracker,
            NullLogger<OperationConflictChecker>.Instance);
        var handoffReceived = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification { Promoted: true } handoff)
                {
                    handoffReceived.TrySetResult(handoff);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker,
            conflictChecker,
            notifications,
            NullLogger<OperationQueueService>.Instance);

        var startCalls = 0;
        var startedId = Guid.NewGuid();
        var queued = await queue.EnqueueAsync(
            OperationType.EvictionScan,
            ConflictScope.Bulk(),
            "Eviction Scan",
            () => Task.FromResult<Guid?>(
                Interlocked.Increment(ref startCalls) == 1 ? null : startedId),
            CancellationToken.None);

        Assert.True(queued.Queued);
        Assert.NotEqual(Guid.Empty, queued.OperationId);

        var handoff = await handoffReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, handoff.OperationId);
        Assert.Equal(2, Volatile.Read(ref startCalls));
        Assert.True(handoff.Promoted);
    }

    private static void AssertQueueRequest(
        RecordingOperationQueue queue,
        OperationType expectedType,
        string expectedName)
    {
        Assert.Equal(expectedType, queue.Type);
        Assert.Equal(ConflictScope.Bulk(), queue.Scope);
        Assert.Equal(expectedName, queue.DisplayName);
        Assert.NotNull(queue.Start);
    }

    private static IStateService CreateStateService()
        => CreateProxy<IStateService>((method, _) => method.Name switch
        {
            nameof(IStateService.GetServiceInterval) => null,
            nameof(IStateService.GetServiceRunOnStartup) => null,
            nameof(IStateService.GetEvictedDataMode) => EvictedDataMode.Show.ToWireString(),
            nameof(IStateService.GetEvictionScanNotifications) => false,
            _ => DefaultReturn(method.ReturnType)
        });

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

    private sealed class RecordingOperationQueue : IOperationQueue
    {
        private readonly QueuedOperationResponse _response;

        public RecordingOperationQueue(QueuedOperationResponse? response = null)
        {
            _response = response ?? new QueuedOperationResponse
            {
                OperationId = Guid.NewGuid(),
                Queued = true,
                Status = "waiting"
            };
        }

        public OperationType? Type { get; private set; }
        public ConflictScope? Scope { get; private set; }
        public string? DisplayName { get; private set; }
        public Func<Task<Guid?>>? Start { get; private set; }

        public Task<QueuedOperationResponse> EnqueueAsync(
            OperationType type,
            ConflictScope scope,
            string displayName,
            Func<Task<Guid?>> start,
            CancellationToken ct)
        {
            Type = type;
            Scope = scope;
            DisplayName = displayName;
            Start = start;
            return Task.FromResult(_response);
        }
    }

    private sealed class TestCacheSizeScanScheduledService : CacheSizeScanScheduledService
    {
        public TestCacheSizeScanScheduledService(
            IPathResolver pathResolver,
            IOperationQueue operationQueue,
            IStateService stateService)
            : base(
                cacheService: null!,
                pathResolver,
                operationQueue,
                stateService,
                NullLogger<CacheSizeScanScheduledService>.Instance,
                new ConfigurationBuilder().Build())
        {
        }

        public Task InvokeScheduledAsync(CancellationToken ct) => base.ExecuteWorkAsync(ct);
    }

    private sealed class TestGameDetectionService : GameDetectionService
    {
        public TestGameDetectionService(IOperationQueue operationQueue, IStateService stateService)
            : base(
                detectionService: null!,
                stateService,
                pathResolver: null!,
                scopeFactory: null!,
                cacheReconciliationService: null!,
                operationQueue,
                NullLogger<GameDetectionService>.Instance,
                new ConfigurationBuilder().Build())
        {
        }

        public Task InvokeScheduledAsync(CancellationToken ct) => base.ExecuteWorkAsync(ct);
    }

    private sealed class TestCacheReconciliationService : CacheReconciliationService
    {
        public TestCacheReconciliationService(
            IServiceProvider serviceProvider,
            IOperationQueue operationQueue,
            IStateService stateService)
            : base(
                serviceProvider,
                NullLogger<CacheReconciliationService>.Instance,
                new ConfigurationBuilder().Build(),
                datasourceService: null!,
                stateService,
                notifications: null!,
                operationTracker: null!,
                rustProcessHelper: null!,
                nginxLogRotationService: null!,
                pathResolver: null!,
                gameCacheDetectionDataService: null!,
                gameCacheDetectionService: null!,
                evictedDetectionPreservationService: null!,
                operationQueue,
                CreateProxy<IHostApplicationLifetime>((method, _) => DefaultReturn(method.ReturnType)),
                capabilityService: null!)
        {
        }

        public Task InvokeScheduledAsync(IServiceProvider scopedServices, CancellationToken ct)
            => base.ExecuteWorkAsync(scopedServices, ct);

        public Task InvokeStartupAsync(CancellationToken ct) => base.OnStartupAsync(ct);
    }
}
