using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
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
    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, true, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, false, false)]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, true, false)]
    [InlineData(NotificationMode.Silent, RunTrigger.Scheduled, false, false)]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, false, true)]
    public void RunNotice_DistinguishesCardsBackgroundProgressAndHiddenRuns(
        NotificationMode mode,
        RunTrigger trigger,
        bool shown,
        bool hidden)
    {
        var notice = new RunNotice(mode, trigger);

        Assert.Equal(shown, notice.ShowNotification);
        Assert.Equal(hidden, notice.HideNotification);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TriggerRunAsync_ReturnsActualFollowUpDecisionWithoutAnOperation(bool followUpQueued)
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "scheduledPrefill" },
            RunStatus = new ScheduleRunStatus { IsRunning = true },
            FollowUpQueued = followUpQueued
        };
        var response = Assert.IsType<QueuedOperationResponse>(
            Assert.IsType<AcceptedResult>((await CreateController(registry).TriggerRunAsync("scheduledPrefill")).Result).Value);
        Assert.True(response.AlreadyRunning);
        Assert.Equal("alreadyRunning", response.Status);
        Assert.Equal(followUpQueued, response.FollowUpQueued);
        Assert.Equal(Guid.Empty, response.OperationId);
        Assert.False(response.Queued);
    }

    [Fact]
    public async Task TriggerAllAsync_DistinguishesFollowUpsFromAlreadyRunningServices()
    {
        var response = Assert.IsType<TriggerAllResponse>(Assert.IsType<AcceptedResult>(
            (await CreateController(new FakeScheduleRegistry { FollowUpCount = 1 }).TriggerAllAsync()).Result).Value);
        Assert.Equal(2, response.AlreadyRunningCount);
        Assert.Equal(1, response.FollowUpCount);
    }

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
            .SingleOrDefault(a => a.Policy == "AccountHolder");

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
            .SingleOrDefault(a => a.Policy == "AccountHolder");

        Assert.NotNull(authorize);
    }

    [Fact]
    public async Task SetScanModeAsync_UnknownService_ReturnsNotFound()
    {
        var registry = new FakeScheduleRegistry { InfoForGet = null };
        var controller = CreateController(registry);

        var result = await controller.SetScanModeAsync("does-not-exist", GameDetectionScanMode.Hybrid);

        Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(0, registry.SetScanModeCalls);
    }

    // Game detection is the only schedule with a scan mode. A mode stored under any other key would
    // sit in state behind a card that shows no dropdown, so the refusal has to reach the caller.
    [Fact]
    public async Task SetScanModeAsync_ScheduleWithoutAScanMode_ReturnsConflict()
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "depotMapping" },
            ScanModeAccepted = false
        };
        var controller = CreateController(registry);

        var result = await controller.SetScanModeAsync("depotMapping", GameDetectionScanMode.Incremental);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public async Task SetScanModeAsync_GameDetection_ReturnsNoContentAndPersists()
    {
        var registry = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "gameDetection", ScanMode = GameDetectionScanMode.Full }
        };
        var controller = CreateController(registry);

        var result = await controller.SetScanModeAsync("gameDetection", GameDetectionScanMode.Incremental);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, registry.SetScanModeCalls);
        Assert.Equal(GameDetectionScanMode.Incremental, registry.LastScanModeSet);
    }

    [Fact]
    public void SetScanModeAction_CarriesAdminOnlyPolicy()
    {
        var method = typeof(ScheduleController).GetMethod(nameof(ScheduleController.SetScanModeAsync));
        Assert.NotNull(method);

        var authorize = method!
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .SingleOrDefault(a => a.Policy == "AccountHolder");

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
                StageKey = "signalr.scheduledRun.logRotation.running"
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
    }

    [Fact]
    public void RegistryGetRunStatus_ActiveTrackedOperation_ReturnsLatestContext()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var metadata = new Dictionary<string, object?>
        {
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
    }

    // The status reports the run; how it is drawn comes from its row, which reads the notice the
    // run was admitted with.
    [Fact]
    public void GetRunStatus_ActiveHiddenOperation_IsRunningAndItsRowIsHidden()
    {
        var tracker = CreateTracker();
        using var cts = new CancellationTokenSource();
        var notice = new RunNotice(NotificationMode.Hidden, RunTrigger.Scheduled);
        var operationId = tracker.RegisterOperation(OperationType.LogRotation, "logRotation", cts, notice: notice);
        tracker.UpdateProgress(operationId, 20, "signalr.scheduledRun.logRotation.running");

        var status = CreateRegistry(tracker).GetRunStatus("logRotation");

        Assert.NotNull(status);
        Assert.True(status!.IsRunning);
        Assert.Equal(RunVisibility.Hidden, Assert.Single(tracker.GetRuns().Runs).Visibility);
    }

    // Run Now on scheduled prefill with no prefill schedule enabled is refused with a key the
    // browser translates, and nothing is started or left pending. [69]
    [Fact]
    public async Task TriggerRunAsync_ScheduledPrefillWithNoScheduleEnabled_Answers409WithItsKeyAndStartsNothing()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-run-now-no-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var state = StateTestMethods.CreateStateService(root);
            using var prefill = new NoPrefillScheduleEnabledProbe();
            var tracker = CreateTracker();
            var controller = new ScheduleController(new ServiceScheduleRegistry(
                [prefill], state, (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(), tracker));

            var context = new DefaultHttpContext();
            var body = new MemoryStream();
            context.Response.Body = body;
            var middleware = new GlobalExceptionMiddleware(
                async _ => await controller.TriggerRunAsync("scheduledPrefill"),
                NullLogger<GlobalExceptionMiddleware>.Instance,
                new HostingEnvironment { EnvironmentName = Environments.Production });
            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
            using var document = JsonDocument.Parse(body.ToArray());
            Assert.Equal("management.schedules.services.scheduledPrefill.runNowNoSchedule",
                document.RootElement.GetProperty("stageKey").GetString());
            Assert.Empty(tracker.GetRuns().Runs);
            Assert.Null(typeof(ScheduledServiceBase)
                .GetField("_manualNotice", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(prefill));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

    // The scheduled prefill loop as the registry sees it, with every prefill schedule disabled.
    private sealed class NoPrefillScheduleEnabledProbe : ConfigurableScheduledService, IScheduleEnabledGate
    {
        public NoPrefillScheduleEnabledProbe()
            : base(NullLogger<NoPrefillScheduleEnabledProbe>.Instance, TimeSpan.FromMinutes(1))
        {
        }

        public string ScheduleServiceKey => "scheduledPrefill";
        protected override string ServiceName => ScheduleServiceKey;

        public bool HasAnyServiceEnabled() => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class FakeScheduleRegistry : IServiceScheduleRegistry
    {
        public ServiceScheduleInfo? InfoForGet { get; set; }
        public ScheduleRunStatus? RunStatus { get; set; }
        public int SetNotificationModeCalls { get; private set; }
        public NotificationMode? LastModeSet { get; private set; }
        public int SetNotificationDisplayModeCalls { get; private set; }
        public NotificationDisplayMode? LastDisplayModeSet { get; private set; }
        public bool ScanModeAccepted { get; set; } = true;
        public int SetScanModeCalls { get; private set; }
        public GameDetectionScanMode? LastScanModeSet { get; private set; }

        public IReadOnlyList<ServiceScheduleInfo> GetAll() => Array.Empty<ServiceScheduleInfo>();
        public ServiceScheduleInfo? Get(string serviceKey) => InfoForGet;
        public void SetInterval(string serviceKey, double intervalHours) { }
        public void SetRunOnStartup(string serviceKey, bool runOnStartup) { }
        public bool SetCustomSchedule(string serviceKey, CustomSchedule? schedule) => true;

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

        public bool SetScanMode(string serviceKey, GameDetectionScanMode mode)
        {
            SetScanModeCalls++;
            LastScanModeSet = mode;
            return ScanModeAccepted;
        }

        public Task<(ScheduleRunStatus Status, string? SkippedReason, bool FollowUpQueued)> TriggerRunAsync(string serviceKey)
            => Task.FromResult<(ScheduleRunStatus, string?, bool)>((RunStatus ?? new ScheduleRunStatus(), null, FollowUpQueued));
        public Task<(int TriggeredCount, int AlreadyRunningCount, int SkippedCount, string? SkippedReason, int FollowUpCount)> TriggerAllAsync()
            => Task.FromResult<(int, int, int, string?, int)>((0, 2, 0, null, FollowUpCount));
        public bool FollowUpQueued { get; set; }
        public int FollowUpCount { get; set; }
        public void ResetToDefaults() { }
        public void NotifySchedulesChanged() { }
        public Task BroadcastSchedulesAsync() => Task.CompletedTask;
        public ScheduleRunStatus? GetRunStatus(string serviceKey) => RunStatus;
        public void ClearNotificationDisplayMode(string serviceKey) { }
        public NotificationDisplayMode GetGlobalNotificationDisplayMode() => NotificationDisplayMode.Condensed;
        public Task SetGlobalNotificationDisplayModeAsync(NotificationDisplayMode mode) => Task.CompletedTask;
        public Task PublishGlobalNotificationDisplayModeAsync() => Task.CompletedTask;
    }
}
