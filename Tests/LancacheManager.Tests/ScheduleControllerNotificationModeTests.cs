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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the ScheduleController notification-mode hardening and the generic run-status recovery
/// endpoint: the mode PUT is admin-only and rejects services that do not support run notifications
/// (including scheduledPrefill), while the run-status route maps a service key to its tracked
/// operation and reports the live percent for card recovery. Runs in the downloads-ended collection
/// because a registry built with a download gate installs the process-wide run gate.
/// </summary>
[Collection(nameof(DownloadsEndedEventCollection))]
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
        Assert.Equal(ScheduleActorKind.Unknown, registry.LastActor?.Kind);
    }

    [Fact]
    public async Task TriggerRunAsync_PassesTheResolvedAccountSnapshotToSchedules()
    {
        var actor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "saved-name");
        var schedules = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "logRotation" }
        };
        var controller = CreateController(schedules, ScheduleExecutionTestService.Create(actor));

        await controller.TriggerRunAsync("logRotation");

        Assert.Same(actor, schedules.LastActor);
    }

    // The route matches a schedule in any casing, but the registry's per-key rules compare the
    // exact key, so the run is asked for under the registry's own spelling.
    [Fact]
    public async Task TriggerRunAsync_PassesTheScheduleKeyWhateverTheUrlCasing()
    {
        var schedules = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "depotMapping" }
        };

        await CreateController(schedules).TriggerRunAsync("DepotMapping");

        Assert.Equal("depotMapping", schedules.LastTriggeredKey);
    }

    // Every route that stores a schedule setting stores it under the schedule's own key, whatever casing
    // the URL used: a setting stored under the URL's casing is never read back.
    [Fact]
    public async Task SettingRoutes_StoreUnderTheScheduleKeyWhateverTheUrlCasing()
    {
        var schedules = new FakeScheduleRegistry
        {
            InfoForGet = new ServiceScheduleInfo { Key = "depotMapping", SupportsNotifications = true }
        };
        var controller = CreateController(schedules);

        await controller.SetIntervalAsync("DepotMapping", new UpdateScheduleIntervalRequest { IntervalHours = 2 });
        await controller.SetCustomScheduleAsync("DepotMapping", new UpdateScheduleCustomScheduleRequest());
        await controller.SetRunOnStartupAsync("DepotMapping", new UpdateScheduleRunOnStartupRequest { RunOnStartup = true });
        await controller.SetNotificationModeAsync("DepotMapping", NotificationMode.All);
        await controller.SetNotificationDisplayModeAsync("DepotMapping", NotificationDisplayMode.Full);
        await controller.ClearNotificationDisplayModeAsync("DepotMapping");
        await controller.SetScanModeAsync("DepotMapping", GameDetectionScanMode.Full);

        Assert.Equal(Enumerable.Repeat("depotMapping", 7), schedules.SettingKeys);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsTheTypedPageAndRejectsInvalidPaging()
    {
        var expected = new ScheduleExecutionResponse
        {
            Page = 2,
            PageSize = 20,
            TotalCount = 21,
            TotalPages = 2
        };
        var controller = CreateController(
            new FakeScheduleRegistry(), ScheduleExecutionTestService.Create(response: expected));

        var ok = Assert.IsType<OkObjectResult>((await controller.GetHistoryAsync(2, 20)).Result);

        Assert.Same(expected, ok.Value);
        await Assert.ThrowsAsync<ValidationException>(() => controller.GetHistoryAsync(0, 20));
        await Assert.ThrowsAsync<ValidationException>(() => controller.GetHistoryAsync(1, 101));
    }

    [Fact]
    public void GetHistoryAction_CarriesAccountHolderPolicy()
    {
        var method = typeof(ScheduleController).GetMethod(nameof(ScheduleController.GetHistoryAsync));

        Assert.Contains(
            method!.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.Policy == "AccountHolder");
    }

    [Fact]
    public async Task TriggerAllAsync_ReportsAlreadyRunningServices()
    {
        var response = Assert.IsType<TriggerAllResponse>(Assert.IsType<AcceptedResult>(
            (await CreateController(new FakeScheduleRegistry()).TriggerAllAsync()).Result).Value);
        Assert.Equal(2, response.AlreadyRunningCount);
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
            var controller = CreateController(new ServiceScheduleRegistry(
                [prefill], state, (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(), tracker));

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
            Assert.Null(RequestedRun(prefill));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Run All with no prefill schedule enabled leaves prefill out of every count instead of reporting
    // it started.
    [Fact]
    public async Task RunAll_LeavesOutAPrefillWithNoScheduleEnabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-run-all-no-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var prefill = new NoPrefillScheduleEnabledProbe();
            var schedules = new ServiceScheduleRegistry(
                [prefill], StateTestMethods.CreateStateService(root),
                (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(), CreateTracker());

            var (triggered, alreadyRunning, skipped, _) = await schedules.TriggerAllAsync();

            Assert.Equal((0, 0, 0), (triggered, alreadyRunning, skipped));
            Assert.Null(RequestedRun(prefill));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A service turned off in configuration has no loop to take a run: Run Now is refused with a key
    // the browser translates, and Run All leaves it out of every count.
    [Fact]
    public async Task AServiceDisabledInConfiguration_IsRefusedByRunNowAndLeftOutOfRunAll()
    {
        var root = Path.Combine(Path.GetTempPath(), "lm-run-now-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new DisabledInConfigurationProbe();
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            var tracker = CreateTracker();
            var schedules = new ServiceScheduleRegistry(
                [service], StateTestMethods.CreateStateService(root),
                (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
                ScheduleExecutionTestService.Create(), tracker);
            var controller = CreateController(schedules);

            var context = new DefaultHttpContext();
            var body = new MemoryStream();
            context.Response.Body = body;
            var middleware = new GlobalExceptionMiddleware(
                async _ => await controller.TriggerRunAsync("logRotation"),
                NullLogger<GlobalExceptionMiddleware>.Instance,
                new HostingEnvironment { EnvironmentName = Environments.Production });
            await middleware.InvokeAsync(context);

            Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
            using var document = JsonDocument.Parse(body.ToArray());
            Assert.Equal("management.schedules.runNowServiceDisabled",
                document.RootElement.GetProperty("stageKey").GetString());
            var (triggered, alreadyRunning, skipped, _) = await schedules.TriggerAllAsync();
            Assert.Equal((0, 0, 0), (triggered, alreadyRunning, skipped));
            Assert.Empty(tracker.GetRuns().Runs);
            Assert.Null(RequestedRun(service));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A scan a download turned away on a service that has no loop is not held on a card nothing takes: it
    // ends with the reason the service cannot run.
    [Fact]
    public async Task ASkippedScanOnAServiceWithNoLoop_EndsWithTheReasonInsteadOfWaiting()
    {
        const string missingProgramKey = "management.schedules.runNowProgramMissing";
        var root = Path.Combine(Path.GetTempPath(), "lm-held-disabled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var service = new DisabledInConfigurationProbe("cacheReconciliation", missingProgramKey);
            await service.StartAsync(CancellationToken.None);
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
            var tracker = CreateTracker();
            var announced = new TaskCompletionSource<ScheduledRunCompleteEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var notifications = CacheScanGateHarness.CreateProxy<ISignalRNotificationService>((_, args) =>
            {
                if (args?.Length > 1 && args[1] is ScheduledRunCompleteEvent terminal) announced.TrySetResult(terminal);
                return Task.CompletedTask;
            });
            _ = new ServiceScheduleRegistry(
                [service], StateTestMethods.CreateStateService(root), notifications,
                ScheduleExecutionTestService.Create(), tracker, activityRegistry: null,
                cacheScanGate: CacheScanGateHarness.Idle());

            var id = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource(),
                notice: new RunNotice(NotificationMode.All, RunTrigger.Manual));
            tracker.CompleteOperation(id, success: true, skipped: true,
                onCompleting: operation => operation.SkippedForDownload = true);

            var terminal = await announced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(missingProgramKey, terminal.StageKey);
            Assert.Empty(tracker.GetWaitingOperations());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // An eviction scan's own detection step is not the detection schedule's run.
    [Fact]
    public void GetRunStatus_IgnoresAnEvictionScansDetectionStep()
    {
        var tracker = CreateTracker();
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            parentOperationId: scan);

        var status = CreateRegistry(tracker).GetRunStatus("gameDetection");

        Assert.NotNull(status);
        Assert.False(status!.IsRunning);
    }

    // A running detection is the detection schedule's run only when it is the scan type the schedule would
    // run (Full here, the default mode); the other type waits and runs its own scan after.
    [Theory]
    [InlineData(DetectionScanType.Incremental, false)]
    [InlineData(DetectionScanType.Full, true)]
    public void ARunningDetection_IsTheSchedulesRunOnlyForTheSameScanType(DetectionScanType running, bool counts)
    {
        var tracker = CreateTracker();
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            detectionScanType: running);

        var status = (ScheduleRunStatus?)typeof(ServiceScheduleRegistry)
            .GetMethod("GetRunThatDoesTheWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(CreateRegistry(tracker), ["gameDetection", null]);

        Assert.Equal(counts, status is not null);
    }

    // A held Games-page request is answered by a running detection of the type the person asked for, whatever
    // the schedule's own mode resolves to (Full here).
    [Theory]
    [InlineData(DetectionScanType.Incremental, DetectionScanType.Incremental, true)]
    [InlineData(DetectionScanType.Full, DetectionScanType.Incremental, false)]
    public void ARunningDetection_AnswersAHeldRequestOnlyForTheScanTypeItAskedFor(
        DetectionScanType running, DetectionScanType requested, bool counts)
    {
        var tracker = CreateTracker();
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            detectionScanType: running);

        var status = (ScheduleRunStatus?)typeof(ServiceScheduleRegistry)
            .GetMethod("GetRunThatDoesTheWork", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(CreateRegistry(tracker), ["gameDetection", requested]);

        Assert.Equal(counts, status is not null);
    }

    // The detection step an eviction scan runs inside is not a detection a page reload may rebuild.
    [Fact]
    public void TheActiveDetectionAnswer_IgnoresAnEvictionScansDetectionStep()
    {
        var tracker = CreateTracker();
        var scan = tracker.RegisterOperation(OperationType.EvictionScan, "Eviction Scan", new CancellationTokenSource());
        tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource(),
            new GameDetectionMetrics { ParentOperationId = scan }, parentOperationId: scan);
        var service = (GameCacheDetectionService)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(GameCacheDetectionService));
        typeof(GameCacheDetectionService).GetField("_operationTracker", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, tracker);

        Assert.Null(service.GetActiveOperation());
    }

    private static RunNotice? RequestedRun(ScheduledServiceBase loop)
        => ((RunNotice?[])typeof(ScheduledServiceBase)
            .GetField("_requested", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(loop)!).FirstOrDefault(request => request is not null);

    private static ScheduleController CreateController(
        IServiceScheduleRegistry registry,
        ScheduleExecutionService? executions = null)
    {
        return new ScheduleController(registry, executions ?? ScheduleExecutionTestService.Create())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private static ServiceScheduleRegistry CreateRegistry(UnifiedOperationTracker tracker)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        return new ServiceScheduleRegistry(
            Array.Empty<IHostedService>(), stateService, notifications,
            ScheduleExecutionTestService.Create(), tracker);
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

    // Log Rotation as a default install has it: its enabling key is false, so its loop stops at start.
    private sealed class DisabledInConfigurationProbe(string serviceKey = "logRotation", string? stageKey = null)
        : ScheduledBackgroundService(
            NullLogger<DisabledInConfigurationProbe>.Instance,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Probe:Enabled"] = "false" })
                .Build())
    {
        public override string ServiceKey => serviceKey;
        public override string DisabledStageKey => stageKey ?? base.DisabledStageKey;
        protected override string ServiceName => serviceKey;
        protected override string? EnabledConfigKey => "Probe:Enabled";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
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
        public ScheduleActor? LastActor { get; private set; }
        public string? LastTriggeredKey { get; private set; }
        public List<string> SettingKeys { get; } = [];

        public IReadOnlyList<ServiceScheduleInfo> GetAll() => Array.Empty<ServiceScheduleInfo>();
        public ServiceScheduleInfo? Get(string serviceKey) => InfoForGet;
        public void SetInterval(string serviceKey, double intervalHours)
        {
            SettingKeys.Add(serviceKey);
        }

        public void SetRunOnStartup(string serviceKey, bool runOnStartup)
        {
            SettingKeys.Add(serviceKey);
        }

        public bool SetCustomSchedule(string serviceKey, CustomSchedule? schedule)
        {
            SettingKeys.Add(serviceKey);
            return true;
        }

        public void SetNotificationMode(string serviceKey, NotificationMode mode)
        {
            SettingKeys.Add(serviceKey);
            SetNotificationModeCalls++;
            LastModeSet = mode;
        }

        public void SetNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode)
        {
            SettingKeys.Add(serviceKey);
            SetNotificationDisplayModeCalls++;
            LastDisplayModeSet = mode;
        }

        public bool SetScanMode(string serviceKey, GameDetectionScanMode mode)
        {
            SettingKeys.Add(serviceKey);
            SetScanModeCalls++;
            LastScanModeSet = mode;
            return ScanModeAccepted;
        }

        public Task<(ScheduleRunStatus Status, string? SkippedReason, bool FollowUpQueued)> TriggerRunAsync(
            string serviceKey,
            ScheduleActor? actor = null)
        {
            LastActor = actor;
            LastTriggeredKey = serviceKey;
            return Task.FromResult<(ScheduleRunStatus, string?, bool)>(
                (RunStatus ?? new ScheduleRunStatus(), null, FollowUpQueued));
        }

        public Task<(int TriggeredCount, int AlreadyRunningCount, int SkippedCount, string? SkippedReason)> TriggerAllAsync(
            ScheduleActor? actor = null)
        {
            LastActor = actor;
            return Task.FromResult<(int, int, int, string?)>((0, 2, 0, null));
        }
        public bool FollowUpQueued { get; set; }
        public void ResetToDefaults() { }
        public void NotifySchedulesChanged() { }
        public Task BroadcastSchedulesAsync() => Task.CompletedTask;
        public ScheduleRunStatus? GetRunStatus(string serviceKey) => RunStatus;
        public void ClearNotificationDisplayMode(string serviceKey)
        {
            SettingKeys.Add(serviceKey);
        }
        public NotificationDisplayMode GetGlobalNotificationDisplayMode() => NotificationDisplayMode.Condensed;
        public Task SetGlobalNotificationDisplayModeAsync(NotificationDisplayMode mode) => Task.CompletedTask;
        public Task PublishGlobalNotificationDisplayModeAsync() => Task.CompletedTask;
    }
}
