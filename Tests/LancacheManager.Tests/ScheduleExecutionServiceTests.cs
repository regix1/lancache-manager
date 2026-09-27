using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class ScheduleExecutionServiceTests
{
    [Fact]
    public void CaptureKeepsEveryTerminalStatusAcrossNotificationModes()
    {
        var service = ScheduleExecutionTestService.Create();
        foreach (var mode in Enum.GetValues<NotificationMode>())
        {
            foreach (var status in new[]
            {
                OperationStatus.Completed,
                OperationStatus.Failed,
                OperationStatus.Cancelled,
                OperationStatus.Skipped
            })
            {
                var actor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "saved-name");
                var operation = Complete(status, new RunNotice(mode, RunTrigger.Manual, actor));

                var execution = service.Capture(operation, "logRotation");

                Assert.Equal(status, execution.Status);
                Assert.Equal(ScheduleActorKind.Account, execution.ActorKind);
                Assert.Equal(actor.AccountId, execution.AccountId);
                Assert.Equal("saved-name", execution.Username);
                Assert.Equal(RunTrigger.Manual, execution.Trigger);
                Assert.Equal(status == OperationStatus.Completed ? null : "terminal detail", execution.Detail);
            }
        }
    }

    [Fact]
    public void CaptureClassifiesAutomaticAndRestoredRunsWithoutGuessingAUser()
    {
        var service = ScheduleExecutionTestService.Create();
        var scheduled = service.Capture(
            Complete(OperationStatus.Completed, new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled)),
            "cacheSizeScan");
        var restored = service.Capture(
            Complete(OperationStatus.Failed, new RunNotice(
                NotificationMode.All,
                RunTrigger.Manual,
                new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "stale-name"),
                restoredOrigin: true)),
            "scheduledPrefill");

        Assert.Equal(ScheduleActorKind.Server, scheduled.ActorKind);
        Assert.Equal(RunTrigger.Scheduled, scheduled.Trigger);
        Assert.Null(scheduled.AccountId);
        Assert.Equal(ScheduleActorKind.Unknown, restored.ActorKind);
        Assert.Null(restored.Trigger);
        Assert.Null(restored.AccountId);
        Assert.Null(restored.Username);
    }

    [Fact]
    public async Task InsertRejectsOneOperationTwiceAndPagesTiedTimesByNewestId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = new ScheduleExecutionService(
            database.Factory,
            NullLogger<ScheduleExecutionService>.Instance);
        var startedAt = new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Utc);
        var first = Execution(Guid.NewGuid(), startedAt);
        var second = Execution(Guid.NewGuid(), startedAt);

        Assert.True(await service.InsertAsync(first));
        Assert.False(await service.InsertAsync(Execution(first.OperationId, startedAt)));
        Assert.True(await service.InsertAsync(second));

        var page = await service.GetPageAsync(1, 1);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.TotalPages);
        Assert.Equal(second.OperationId, Assert.Single(page.Items).OperationId);

        await using var freshContext = new AppDbContext(database.Options);
        Assert.Equal(2, await freshContext.ScheduleExecutions.CountAsync());
        Assert.Equal(
            new[] { second.OperationId, first.OperationId },
            await freshContext.ScheduleExecutions
                .OrderByDescending(item => item.StartedAt)
                .ThenByDescending(item => item.Id)
                .Select(item => item.OperationId)
                .ToArrayAsync());
    }

    [Fact]
    public async Task InsertContainsWriteFailureWhileActorLookupPropagatesIt()
    {
        var service = new ScheduleExecutionService(
            new FailedContextSource(),
            NullLogger<ScheduleExecutionService>.Instance);

        Assert.False(await service.InsertAsync(Execution(Guid.NewGuid(), DateTime.UtcNow)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ResolveActorAsync(Guid.NewGuid()));
    }

    [Fact]
    public void ScheduleOutcomePredicateIncludesOnlyOwnedTerminalShapes()
    {
        var predicate = typeof(ServiceScheduleRegistry)
            .GetMethod("IsScheduleOutcome", BindingFlags.Static | BindingFlags.NonPublic)!;
        var parentId = Guid.NewGuid();
        var scheduleId = Guid.NewGuid();
        var ordinary = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogRotation,
            Name = "Log Rotation"
        };
        var childPhase = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.LogRotation,
            Name = "Log Rotation child phase",
            ParentOperationId = parentId
        };
        var prefillContainer = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill",
            Metadata = new ScheduledPrefillOperationMetadata()
        };
        var prefillSchedule = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.ScheduledPrefill,
            Name = "Scheduled Prefill - Steam - Nightly",
            ParentOperationId = parentId,
            Metadata = new ScheduledPrefillServiceRunState(
                PrefillPlatform.Steam,
                scheduleId,
                "Nightly",
                new RunNotice(NotificationMode.All, RunTrigger.Scheduled))
        };
        var integrationLogin = new OperationInfo
        {
            Id = Guid.NewGuid(),
            Type = OperationType.XboxMapping,
            Name = "Xbox Mapping Sign-in",
            Metadata = new Dictionary<string, object?>
            {
                ["integrationLogin"] = new IntegrationLogin(
                    Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(),
                    DateTime.UtcNow.AddMinutes(5), Shared: false, Recover: false)
            }
        };

        Assert.True((bool)predicate.Invoke(null, [ordinary, null])!);
        Assert.False((bool)predicate.Invoke(null, [childPhase, null])!);
        Assert.False((bool)predicate.Invoke(null, [prefillContainer, null])!);
        Assert.True((bool)predicate.Invoke(null, [prefillSchedule, scheduleId])!);
        Assert.False((bool)predicate.Invoke(null, [integrationLogin, null])!);
    }

    [Fact]
    public async Task TerminalWriteBroadcastsAgainOnlyAfterTheInsertSucceeds()
    {
        var executions = new DelayedScheduleExecutionService();
        var notifications = (ScheduleNotifications)(object)DispatchProxy
            .Create<ISignalRNotificationService, ScheduleNotifications>();
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var state = DispatchProxy.Create<IStateService, ScheduleStateProxy>();
        _ = new ServiceScheduleRegistry(
            [], state, (ISignalRNotificationService)(object)notifications, executions, tracker);
        var actor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "saved-name");
        var operationId = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: new RunNotice(NotificationMode.All, RunTrigger.Manual, actor));

        tracker.CompleteOperation(operationId, success: true);

        var execution = await executions.Captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await notifications.First.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, notifications.Count);
        Assert.Same(actor, tracker.GetOperation(operationId)!.Notice?.Actor);
        Assert.Equal(actor.AccountId, execution.AccountId);
        executions.Release.TrySetResult(true);
        await notifications.Second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, notifications.Count);
    }

    private static OperationInfo Complete(OperationStatus status, RunNotice notice)
    {
        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance);
        var id = tracker.RegisterOperation(
            OperationType.LogRotation,
            "Log Rotation",
            new CancellationTokenSource(),
            notice: notice);
        tracker.CompleteOperation(
            id,
            success: status is OperationStatus.Completed or OperationStatus.Skipped,
            error: status == OperationStatus.Completed ? null : "terminal detail",
            cancelled: status == OperationStatus.Cancelled,
            skipped: status == OperationStatus.Skipped);
        return tracker.GetOperation(id)!;
    }

    private static ScheduleExecution Execution(Guid operationId, DateTime startedAt) => new()
    {
        OperationId = operationId,
        ServiceKey = "logRotation",
        Status = OperationStatus.Completed,
        Trigger = RunTrigger.Scheduled,
        ActorKind = ScheduleActorKind.Server,
        StartedAt = startedAt,
        CompletedAt = startedAt.AddSeconds(5),
        WorkerStarted = true
    };

    private sealed class FailedContextSource : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("Database unavailable");
    }

    private sealed class DelayedScheduleExecutionService : ScheduleExecutionService
    {
        public DelayedScheduleExecutionService()
            : base(null!, NullLogger<ScheduleExecutionService>.Instance)
        {
        }

        public TaskCompletionSource<ScheduleExecution> Captured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<bool> InsertAsync(
            ScheduleExecution execution,
            CancellationToken cancellationToken = default)
        {
            Captured.TrySetResult(execution);
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private class ScheduleNotifications : DispatchProxy
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public TaskCompletionSource First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Second { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (args is { Length: > 0 }
                && args[0] is string eventName
                && eventName == SignalREvents.SchedulesUpdated)
            {
                var count = Interlocked.Increment(ref _count);
                if (count == 1) First.TrySetResult();
                if (count == 2) Second.TrySetResult();
            }

            return targetMethod?.ReturnType == typeof(Task)
                ? Task.CompletedTask
                : targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }
    }

    private class ScheduleStateProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.ReturnType.IsValueType == true
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
    }
}

internal static class ScheduleExecutionTestService
{
    public static ScheduleExecutionService Create(
        ScheduleActor? actor = null,
        ScheduleExecutionResponse? response = null) => new QuietScheduleExecutionService(actor, response);

    private sealed class QuietScheduleExecutionService : ScheduleExecutionService
    {
        private readonly ScheduleActor _actor;
        private readonly ScheduleExecutionResponse? _response;

        public QuietScheduleExecutionService(
            ScheduleActor? actor,
            ScheduleExecutionResponse? response)
            : base(null!, NullLogger<ScheduleExecutionService>.Instance)
        {
            _actor = actor ?? new ScheduleActor(ScheduleActorKind.Unknown, null, null);
            _response = response;
        }

        public override Task<ScheduleActor> ResolveActorAsync(
            Guid? accountId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_actor);

        public override Task<bool> InsertAsync(
            ScheduleExecution execution,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public override Task<ScheduleExecutionResponse> GetPageAsync(
            int page,
            int pageSize,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_response ?? new ScheduleExecutionResponse
            {
                Page = page,
                PageSize = pageSize
            });
    }
}
