using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// The waiting card must tell the user WHICH operation it is parked behind, and keep telling
/// the truth when the blocker changes hands (the first blocker finishes but another conflicting
/// operation is still active). The Schedules page must also report a service as running while
/// its work executes as a tracked background operation, because a fire-and-forget start drops
/// the loop's executing flag back to false for the whole run.
/// </summary>
public sealed class OperationWaitingBlockerTests
{
    [Fact]
    public async Task EnqueueBehindActiveOperation_WaitingEventNamesBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var waitingEvents = new List<OperationWaitingNotification>();
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaiting
                    && args[1] is OperationWaitingNotification waiting)
                {
                    lock (waitingEvents)
                    {
                        waitingEvents.Add(waiting);
                    }
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => Task.FromResult<Guid?>(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(queued.Queued);
        OperationWaitingNotification emitted;
        lock (waitingEvents)
        {
            emitted = Assert.Single(waitingEvents);
        }
        Assert.Equal(queued.OperationId, emitted.OperationId);
        Assert.Equal("Cache File Scan", emitted.BlockedByName);
        Assert.Equal("Cache File Scan", queue.GetWaitingBlockerName(queued.OperationId));
    }

    [Fact]
    public async Task BlockerHandoff_ReemitsWaitingNamingNewBlockerAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var secondBlockerAnnounced = new TaskCompletionSource<OperationWaitingNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaiting
                    && args[1] is OperationWaitingNotification { BlockedByName: "Eviction Scan" } reannounced)
                {
                    secondBlockerAnnounced.TrySetResult(reannounced);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var firstBlockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());

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

        // A second conflicting operation is already active when the first blocker finishes, so
        // the promotion pass must keep the waiter parked and announce who blocks it NOW.
        tracker.RegisterOperation(
            OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.CompleteOperation(firstBlockerId, success: true);

        var reannounced = await secondBlockerAnnounced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, reannounced.OperationId);
        Assert.Equal("Game Detection", reannounced.Name);
        Assert.Equal(0, startCalls);
        Assert.Equal("Eviction Scan", queue.GetWaitingBlockerName(queued.OperationId));
    }

    [Fact]
    public async Task StartThrowsAtPromotion_WaitingCardFailsWithTheRealReasonAsync()
    {
        var tracker = CreateTracker();
        var conflictChecker = new OperationConflictChecker(tracker, NullLogger<OperationConflictChecker>.Instance);
        var terminal = new TaskCompletionSource<OperationWaitingCompleteNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.OperationWaitingComplete
                    && args[1] is OperationWaitingCompleteNotification complete)
                {
                    terminal.TrySetResult(complete);
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        var queue = new OperationQueueService(
            tracker, conflictChecker, notifications, NullLogger<OperationQueueService>.Instance);

        var blockerId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var queued = await queue.EnqueueAsync(
            OperationType.GameDetection,
            ConflictScope.Bulk(),
            "Game Detection",
            () => throw new InvalidOperationException("datasource evidence is ambiguous"),
            CancellationToken.None);
        Assert.True(queued.Queued);

        tracker.CompleteOperation(blockerId, success: true);

        // A thrown start is a permanent refusal for this run: the card must fail promptly with
        // the thrown message, never spin through the transient-gate retry loop first.
        var complete = await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(queued.OperationId, complete.OperationId);
        Assert.False(complete.Promoted);
        Assert.Equal("datasource evidence is ambiguous", complete.Error);
    }

    [Fact]
    public void ScheduledServiceWithBackgroundOperation_ReportsRunning()
    {
        var tracker = CreateTracker();
        var probe = new IdleScanProbeService();
        var registry = CreateRegistry(probe, tracker);

        var idle = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.False(idle.IsRunning);

        // The loop is not executing (fire-and-forget start already returned); only the tracked
        // background operation says the scan is still going.
        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        var running = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.True(running.IsRunning);

        tracker.CompleteOperation(operationId, success: true);
        var finished = registry.GetAll().Single(s => s.Key == "cacheSizeScan");
        Assert.False(finished.IsRunning);
    }

    [Fact]
    public async Task TrackedOperationTerminal_BroadcastsSchedulesAsync()
    {
        var tracker = CreateTracker();
        var schedulesBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notifications = CreateProxy<ISignalRNotificationService>((method, args) =>
        {
            if (method.Name == nameof(ISignalRNotificationService.NotifyAllAsync))
            {
                if ((string)args![0]! == SignalREvents.SchedulesUpdated)
                {
                    schedulesBroadcast.TrySetResult();
                }

                return Task.CompletedTask;
            }

            return DefaultReturn(method.ReturnType);
        });
        _ = CreateRegistry(new IdleScanProbeService(), tracker, notifications);

        // A background run ends with no work-state tick at all; the terminal event is the only
        // signal that can turn the running dot off, so it must drive a schedules broadcast.
        var operationId = tracker.RegisterOperation(
            OperationType.CacheSizeScan, "Cache File Scan", new CancellationTokenSource());
        tracker.CompleteOperation(operationId, success: true);

        await schedulesBroadcast.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private static ServiceScheduleRegistry CreateRegistry(
        ScheduledBackgroundService service,
        UnifiedOperationTracker tracker,
        ISignalRNotificationService? notifications = null)
    {
        notifications ??= CreateProxy<ISignalRNotificationService>((method, _) => DefaultReturn(method.ReturnType));
        var stateService = (IStateService)System.Reflection.DispatchProxy.Create<IStateService, NullReturningProxy>();
        return new ServiceScheduleRegistry(new IHostedService[] { service }, stateService, notifications, tracker);
    }

    /// <summary>
    /// A scheduled service that is registered under the real cacheSizeScan key but whose loop
    /// never runs, mirroring the state a fire-and-forget scan leaves behind: executing flag
    /// false while the tracked operation carries the actual work.
    /// </summary>
    private sealed class IdleScanProbeService : ScheduledBackgroundService
    {
        public IdleScanProbeService()
            : base(NullLogger<IdleScanProbeService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public override string ServiceKey => "cacheSizeScan";
        protected override string ServiceName => "IdleScanProbe";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
            => Task.CompletedTask;
    }

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, ProxyDispatch<T>>();
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

    private class ProxyDispatch<T> : System.Reflection.DispatchProxy where T : class
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => Handler!(targetMethod!, args);
    }
}
