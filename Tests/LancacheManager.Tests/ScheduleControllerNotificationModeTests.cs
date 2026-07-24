using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the ScheduleController notification-mode hardening and the generic run-status recovery
/// endpoint: the mode PUT is admin-only and rejects services that do not support run notifications
/// (including scheduledPrefill), while the run-status route maps a service key to its tracked
/// operation and reports the live percent for card recovery.
/// </summary>
public class ScheduleControllerNotificationModeTests
{
    [Fact]
    public async Task SetNotificationModeAsync_UnsupportedService_ReturnsConflictAndDoesNotPersist()
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "logRotation", SupportsNotifications = false }
        };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationModeAsync("logRotation", NotificationMode.Silent);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(0, registry.SetNotificationModeCalls);
    }

    [Fact]
    public async Task SetNotificationModeAsync_ScheduledPrefill_ReturnsConflict()
    {
        // ScheduledPrefillService never opts into SupportsNotifications (its mode is per-platform), so the
        // registry reports it as unsupported and the generic PUT must reject it rather than no-op.
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "scheduledPrefill", SupportsNotifications = false }
        };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationModeAsync("scheduledPrefill", NotificationMode.Manual);

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(0, registry.SetNotificationModeCalls);
    }

    [Fact]
    public async Task SetNotificationModeAsync_UnknownService_ReturnsNotFound()
    {
        var registry = new FakeScheduleRegistry { InfoForGet = null };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationModeAsync("does-not-exist", NotificationMode.All);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, registry.SetNotificationModeCalls);
    }

    [Fact]
    public async Task SetNotificationModeAsync_SupportedService_ReturnsNoContentAndPersists()
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "cacheReconciliation", SupportsNotifications = true }
        };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationModeAsync("cacheReconciliation", NotificationMode.Silent);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, registry.SetNotificationModeCalls);
        Assert.Equal(NotificationMode.Silent, registry.LastModeSet);
    }

    [Fact]
    public void SetNotificationModeAction_CarriesAdminOnlyPolicy()
    {
        var method = typeof(ScheduleController).GetMethod(nameof(ScheduleController.SetNotificationModeAsync));
        Assert.NotNull(method);

        var authorize = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == "AdminOnly");

        Assert.NotNull(authorize);
    }

    [Fact]
    public async Task SetNotificationDisplayModeAsync_UnknownService_ReturnsNotFound()
    {
        var registry = new FakeScheduleRegistry { InfoForGet = null };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationDisplayModeAsync("does-not-exist", NotificationDisplayMode.Condensed);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, registry.SetNotificationDisplayModeCalls);
    }

    [Fact]
    public async Task SetNotificationDisplayModeAsync_KnownService_ReturnsNoContentAndPersists()
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "cacheReconciliation", SupportsNotifications = true }
        };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationDisplayModeAsync("cacheReconciliation", NotificationDisplayMode.Condensed);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, registry.SetNotificationDisplayModeCalls);
        Assert.Equal(NotificationDisplayMode.Condensed, registry.LastDisplayModeSet);
    }

    [Fact]
    public async Task SetNotificationDisplayModeAsync_ScheduledPrefill_ReturnsNoContent()
    {
        // Unlike SetNotificationModeAsync, display mode is card-level (not per-platform) and carries no
        // SupportsNotifications gate, so scheduledPrefill must be accepted rather than rejected.
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "scheduledPrefill", SupportsNotifications = false }
        };
        var controller = CreateController(registry);

        var result = await controller.SetNotificationDisplayModeAsync("scheduledPrefill", NotificationDisplayMode.Full);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, registry.SetNotificationDisplayModeCalls);
    }

    [Fact]
    public void SetNotificationDisplayModeAction_CarriesAdminOnlyPolicy()
    {
        var method = typeof(ScheduleController).GetMethod(nameof(ScheduleController.SetNotificationDisplayModeAsync));
        Assert.NotNull(method);

        var authorize = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == "AdminOnly");

        Assert.NotNull(authorize);
    }

    [Fact]
    public void GetRunStatus_UnknownServiceKey_ReturnsNotFound()
    {
        var registry = new FakeScheduleRegistry { RunStatus = null };
        var controller = CreateController(registry);

        var action = controller.GetRunStatus("does-not-exist");

        Assert.IsType<NotFoundObjectResult>(action.Result);
    }

    [Fact]
    public void GetRunStatus_ActiveStatus_ReturnsOkPayload()
    {
        var registry = new FakeScheduleRegistry
        {
            RunStatus = new ScheduleRunStatus
            {
                IsRunning = true,
                OperationId = "op-1",
                PercentComplete = 42,
                StageKey = "signalr.scheduledRun.logRotation.running",
                ShowNotification = true
            }
        };
        var controller = CreateController(registry);

        var action = controller.GetRunStatus("logRotation");

        var ok = Assert.IsType<OkObjectResult>(action.Result);
        var payload = Assert.IsType<ScheduleRunStatus>(ok.Value);
        Assert.True(payload.IsRunning);
        Assert.Equal(42, payload.PercentComplete);
    }

    [Fact]
    public void RegistryGetRunStatus_UnknownKey_ReturnsNull()
    {
        var registry = CreateRegistry(CreateTracker());

        Assert.Null(registry.GetRunStatus("not-a-real-key"));
    }

    [Fact]
    public void RegistryGetRunStatus_KnownKeyWithNoActiveOperation_ReportsNotRunning()
    {
        var registry = CreateRegistry(CreateTracker());

        var status = registry.GetRunStatus("logRotation");

        Assert.NotNull(status);
        Assert.False(status!.IsRunning);
        Assert.Null(status.OperationId);
        // An idle service is visible by default so recovery stale-completes a persisted running card
        // on reconnect after a missed terminal instead of deleting it.
        Assert.True(status.ShowNotification);
    }

    [Fact]
    public void RegistryGetRunStatus_ActiveTrackedOperation_ReturnsLatestContext()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var metadata = new Dictionary<string, object?>
        {
            ["showNotification"] = true,
            ["context"] = new Dictionary<string, object?> { ["processed"] = 3, ["total"] = 9 },
        };
        var operationId = tracker.RegisterOperation(OperationType.LogRotation, "logRotation", cts, metadata);
        tracker.UpdateProgress(operationId, 33, "signalr.scheduledRun.logRotation.running");

        var registry = CreateRegistry(tracker);
        var status = registry.GetRunStatus("logRotation");

        Assert.NotNull(status);
        Assert.True(status!.IsRunning);
        Assert.NotNull(status.Context);
        Assert.Equal(3, status.Context!["processed"]);
        Assert.Equal(9, status.Context["total"]);
    }

    [Fact]
    public void RegistryGetRunStatus_ActiveTrackedOperation_ReturnsOperationIdAndPercent()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var operationId = tracker.RegisterOperation(OperationType.LogRotation, "logRotation", cts);
        tracker.UpdateProgress(operationId, 45, "signalr.scheduledRun.logRotation.running");

        var registry = CreateRegistry(tracker);
        var status = registry.GetRunStatus("logRotation");

        Assert.NotNull(status);
        Assert.True(status!.IsRunning);
        Assert.Equal(operationId.ToString(), status.OperationId);
        Assert.Equal(45, status.PercentComplete);
        Assert.Equal("signalr.scheduledRun.logRotation.running", status.StageKey);
        // No showNotification metadata key (non-reporter registration): treated as visible.
        Assert.True(status.ShowNotification);
    }

    [Fact]
    public void RegistryGetRunStatus_ActiveOperationWithSilentDisplayFlag_ReportsRunningButNotShown()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var metadata = new Dictionary<string, object?> { ["showNotification"] = false };
        var operationId = tracker.RegisterOperation(OperationType.LogRotation, "logRotation", cts, metadata);
        tracker.UpdateProgress(operationId, 20, "signalr.scheduledRun.logRotation.running");

        var registry = CreateRegistry(tracker);
        var status = registry.GetRunStatus("logRotation");

        Assert.NotNull(status);
        Assert.True(status!.IsRunning);
        Assert.False(status.ShowNotification);
    }

    private static ScheduleController CreateController(IServiceScheduleRegistry registry)
    {
        return new ScheduleController(registry);
    }

    private static ServiceScheduleRegistry CreateRegistry(UnifiedOperationTracker tracker)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        return new ServiceScheduleRegistry(Array.Empty<IHostedService>(), stateService, notifications, tracker);
    }

    private static UnifiedOperationTracker CreateTracker()
    {
        var processManager = new ProcessManager(NullLogger<ProcessManager>.Instance);
        return new UnifiedOperationTracker(processManager, NullLogger<UnifiedOperationTracker>.Instance);
    }

    private sealed class FakeScheduleRegistry : IServiceScheduleRegistry
    {
        public ServiceScheduleInfo? InfoForGet { get; set; }
        public ScheduleRunStatus? RunStatus { get; set; }
        public int SetNotificationModeCalls { get; private set; }
        public NotificationMode? LastModeSet { get; private set; }
        public int SetNotificationDisplayModeCalls { get; private set; }
        public NotificationDisplayMode? LastDisplayModeSet { get; private set; }

        public IReadOnlyList<ServiceScheduleInfo> GetAll() => Array.Empty<ServiceScheduleInfo>();
        public ServiceScheduleInfo? Get(string serviceKey) => InfoForGet;
        public void SetInterval(string serviceKey, double intervalHours) { }
        public void SetRunOnStartup(string serviceKey, bool runOnStartup) { }

        public void SetNotificationMode(string serviceKey, NotificationMode mode)
        {
            SetNotificationModeCalls++;
            LastModeSet = mode;
        }

        public void SetNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode)
        {
            SetNotificationDisplayModeCalls++;
            LastDisplayModeSet = mode;
        }

        public Task TriggerRunAsync(string serviceKey) => Task.CompletedTask;
        public Task<int> TriggerAllAsync() => Task.FromResult(0);
        public void ResetToDefaults() { }
        public void NotifySchedulesChanged() { }
        public Task BroadcastSchedulesAsync() => Task.CompletedTask;
        public ScheduleRunStatus? GetRunStatus(string serviceKey) => RunStatus;
    }

    // Returns the default value for every member; Task-returning members yield a completed task so
    // awaited controller/registry calls do not fault. Mirrors the null-proxy pattern used elsewhere
    // in this suite for interfaces whose behavior is irrelevant to the test.
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType is { IsGenericType: true } && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = returnType.GetGenericArguments()[0];
                var value = inner.IsValueType ? Activator.CreateInstance(inner) : null;
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(inner)
                    .Invoke(null, new[] { value });
            }

            if (returnType is { IsValueType: true } && returnType != typeof(void))
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
