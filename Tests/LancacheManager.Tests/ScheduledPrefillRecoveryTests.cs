using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Tests;

public sealed class ScheduledPrefillRecoveryTests
{
    [Fact]
    public async Task ServiceEventsShareEpochAndCommittedSequenceAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10");
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        capture.Release.SetResult();
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(run.OperationId, OperationType.ScheduledPrefill, run.State.Name,
            new CancellationTokenSource(), run.State));
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [run, tracker, services, notifications, ScheduledPrefillConfigFactory.CreateDefault(),
                true, CancellationToken.None, false])!;
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, await task);
        var events = capture.Events.ToArray();
        Assert.Equal([SignalREvents.ScheduledPrefillStarted, SignalREvents.ScheduledPrefillProgress,
            SignalREvents.ScheduledPrefillCompleted], events.Select(item => item.Event));
        Assert.Equal([1L, 2L, 3L], events.Select(item => item.Value.GetProperty("eventSequence").GetInt64()));
        Assert.All(events, item =>
        {
            Assert.Equal(run.State.Snapshot.EventEpoch, item.Value.GetProperty("eventEpoch").GetGuid());
            Assert.Equal(run.OperationIdString, item.Value.GetProperty("operationId").GetString());
            Assert.Equal(JsonValueKind.Null, item.Value.GetProperty("daemonInstanceId").ValueKind);
            Assert.True(item.Value.GetProperty("showNotification").GetBoolean());
        });
        Assert.Equal(OperationStatus.Skipped, tracker.GetOperation(run.OperationId)!.Status);
    }

    [Fact]
    public async Task RecoveryReadsOneCommittedDisplayWhileOlderSendWaitsAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10");
        var sibling = MakeRun("20");
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        foreach (var item in new[] { run, sibling })
            Assert.True(tracker.TryRestoreOperation(item.OperationId, OperationType.ScheduledPrefill, item.State.Name,
                new CancellationTokenSource(), item.State));
        sibling.State.Record("running", "Sibling", null, 12);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.OnSend = (_, value) =>
        {
            if (value.GetProperty("eventSequence").GetInt64() != 1) return Task.CompletedTask;
            entered.SetResult();
            return release.Task;
        };
        var context = new Dictionary<string, object?> { ["game"] = "First" };
        var first = Report(scheduler, notifications, run, "running", "Downloading First", 25,
            context, bytes: 25, totalBytes: 100);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        context["game"] = "Mutated";
        await Report(scheduler, notifications, run, "running", "Downloading Second", 50,
            new Dictionary<string, object?> { ["game"] = "Second" }, bytes: 50, totalBytes: 100);
        var controller = new ScheduledPrefillConfigController(
            DispatchProxy.Create<IStateService, ScheduleState>(), null!, tracker, scheduler);
        for (var index = 0; index < 3; index++)
        {
            var response = Assert.IsType<ScheduledPrefillRunStatusDto>(Assert.IsType<OkObjectResult>(controller.GetRunStatus().Result).Value);
            var current = Assert.Single(response.Services, entry => entry.OperationId == run.OperationIdString);
            Assert.Equal(2, current.EventSequence);
            Assert.Equal("Downloading Second", current.Message);
            Assert.Equal("Second", current.StageContext!["game"]);
            Assert.Equal(50, current.PercentComplete);
            Assert.Equal(50, current.BytesDownloaded);
            Assert.Equal(100, current.TotalBytes);
            Assert.Equal("session", current.DownloadSessionId);
            Assert.Equal(run.State.Snapshot.EventEpoch, current.EventEpoch);
            var wire = JsonSerializer.SerializeToElement(current, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(2, wire.GetProperty("eventSequence").GetInt64());
            Assert.Equal(run.State.Snapshot.EventEpoch, wire.GetProperty("eventEpoch").GetGuid());
        }
        Assert.Equal(1, sibling.State.Snapshot.EventSequence);
        Assert.NotEqual(run.State.Snapshot.EventEpoch, sibling.State.Snapshot.EventEpoch);
        release.SetResult();
        var old = await first;
        Assert.Equal(1, old!.EventSequence);
        Assert.Equal("First", old.StageContext!["game"]);
        await Report(scheduler, notifications, run, "recovering", "Waiting", null);
        Assert.Equal(50, run.State.PercentComplete);
        await Report(scheduler, notifications, run, "running", "Unknown total", null, clearPercent: true);
        Assert.Null(run.State.PercentComplete);
        Assert.Equal([1L, 2L, 3L, 4L], capture.Events.Select(item => item.Value.GetProperty("eventSequence").GetInt64()));
    }

    [Fact]
    public async Task TerminalSealsDisplayBeforeOlderSendFinishesAsync()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10");
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.OnSend = (name, _) =>
        {
            if (name != SignalREvents.ScheduledPrefillProgress) return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        var pending = Report(scheduler, notifications, run, "running", "Downloading", 40);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(run.OperationId, OperationType.ScheduledPrefill, run.State.Name,
            new CancellationTokenSource(), run.State));
        var complete = typeof(ScheduledPrefillService).GetMethod("CompleteServiceRunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        await (Task)complete.Invoke(null, [run, tracker, notifications, ScheduledPrefillServiceRunResult.Failed, true, "Download failed"])!;
        var terminal = run.State.Snapshot;
        Assert.Equal(2, terminal.EventSequence);
        Assert.Equal("failed", terminal.Stage);
        Assert.Equal("Download failed", terminal.Message);
        Assert.Null(run.State.Record("running", "Late", null, 99));
        await (Task)complete.Invoke(null, [run, tracker, notifications, ScheduledPrefillServiceRunResult.Ran, true, null])!;
        Assert.Same(terminal, run.State.Snapshot);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(run.OperationId)!.Status);
        release.SetResult();
        Assert.Equal(1, (await pending)!.EventSequence);
        Assert.Equal(2, capture.Events.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LifecycleWinsAgainstRelayWaitingToCommitAsync(bool cancelling)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemonRun = await fixture.StartAsync("10");
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10") with { OperationId = daemonRun.PrefillRunId, OperationIdString = daemonRun.PrefillRunId.ToString() };
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        var relayType = typeof(ScheduledPrefillService).GetNestedType("ScheduledPrefillProgressRelay", BindingFlags.NonPublic)!;
        var relay = Activator.CreateInstance(relayType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [scheduler, notifications, fixture.Session, run, fixture.Session.Id, true], null)!;
        relayType.GetMethod("Arm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(relay, null);
        var send = relayType.GetMethod("OnProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var progress = new PrefillProgress
        {
            OperationId = run.OperationIdString,
            DaemonInstanceId = daemonRun.DaemonInstanceId,
            State = "downloading",
            TotalApps = 1,
            CurrentAppName = "Example",
            TotalBytes = 100,
            BytesDownloaded = 50,
            Sequence = 10
        };
        run.State.Record("running", "Downloading previous game", null, 25);
        var gate = typeof(ScheduledPrefillServiceRunState).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run.State)!;
        Task pending;
        lock (gate)
        {
            pending = Task.Run(() => (Task)send.Invoke(relay, [fixture.Session, progress, 10L])!);
            var revision = relayType.GetField("_revision", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Assert.True(SpinWait.SpinUntil(() => (long)revision.GetValue(relay)! > 0, TimeSpan.FromSeconds(5)));
            run.State.Record(cancelling ? "cancelling" : "recovering", cancelling ? "Stopping" : "Waiting", null, null);
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(capture.Events);
        Assert.Equal(cancelling ? "cancelling" : "recovering", run.State.Stage);
        Assert.Equal(25, run.State.PercentComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RelayPreservesLifecycleUntilWatcherResumesAsync(bool cancelling)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemonRun = await fixture.StartAsync("10");
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10") with { OperationId = daemonRun.PrefillRunId, OperationIdString = daemonRun.PrefillRunId.ToString() };
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        var lifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.OnSend = (_, value) =>
        {
            if (value.GetProperty("stage").GetString() == (cancelling ? "cancelling" : "recovering")) lifecycle.TrySetResult();
            if (value.GetProperty("message").GetString()?.Contains("Example", StringComparison.Ordinal) == true) resumed.TrySetResult();
            return Task.CompletedTask;
        };
        run.State.Record("running", "Downloading previous game", null, 25);
        daemonRun.Recovering = !cancelling;
        fixture.Session.Recovering = !cancelling;
        daemonRun.CancelRequested = cancelling;
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("WatchRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, daemonRun, run, notifications, ScheduledPrefillConfigFactory.CreateDefault()])!;
        await lifecycle.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sequence = run.State.Snapshot.EventSequence;
        var relayType = typeof(ScheduledPrefillService).GetNestedType("ScheduledPrefillProgressRelay", BindingFlags.NonPublic)!;
        var relay = Activator.CreateInstance(relayType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [scheduler, notifications, fixture.Session, run, fixture.Session.Id, true], null)!;
        relayType.GetMethod("Arm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(relay, null);
        var send = relayType.GetMethod("OnProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (var index = 10; index < 13; index++)
        {
            var progress = new PrefillProgress
            {
                OperationId = run.OperationIdString,
                DaemonInstanceId = daemonRun.DaemonInstanceId,
                State = "downloading",
                CurrentAppName = $"Example {index}",
                TotalApps = 1,
                TotalBytes = 100,
                BytesDownloaded = 50,
                Sequence = index
            };
            daemonRun.LastProgress = progress;
            await (Task)send.Invoke(relay, [fixture.Session, progress, (long)index])!;
        }
        Assert.Equal(sequence, run.State.Snapshot.EventSequence);
        Assert.Equal(25, run.State.PercentComplete);
        Assert.DoesNotContain(capture.Events, item => item.Value.GetProperty("stage").GetString() == "running");
        Assert.Equal(daemonRun.DaemonInstanceId, run.State.Snapshot.DaemonInstanceId);
        if (!cancelling)
        {
            daemonRun.Recovering = false;
            fixture.Session.Recovering = false;
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("Example", run.State.Message, StringComparison.Ordinal);
            Assert.False(run.State.Snapshot.Recovering);
        }
        fixture.Client.Set(daemonRun, cancelling ? "cancelled" : "completed", 50, cancelling ? "cancelled" : "success");
        await fixture.RefreshAsync();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LaterTicksUseFreeSlotsWhileEarlierSchedulesRemainActiveAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        config.Steam.Schedules.Clear();
        config.Epic.Schedules.Clear();
        config.Xbox.Schedules.Clear();
        config.BattleNet.Schedules.Clear();
        config.Riot.Schedules.Clear();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker).AddSingleton(Notifications()).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);
        var tick = typeof(ScheduledPrefillService).GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            for (var index = 1; index <= 4; index++)
            {
                config.Steam.Schedules.Add(new ScheduledPrefillSchedule
                {
                    Id = Guid.NewGuid(),
                    Name = $"Schedule {index}",
                    Enabled = true,
                    IntervalHours = -1,
                    SelectedAppIds = [(index * 10).ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    NotificationMode = index == 2 ? NotificationMode.Silent : NotificationMode.All,
                    MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
                });
                await ((Task)tick.Invoke(scheduler, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(5));
                if (index <= 3) await WaitUntilAsync(() => fixture.Client.StartCount == index);
            }
            await fixture.RefreshAsync();
            var active = fixture.Session.Runs.Values.Where(run => !run.Completion.Task.IsCompleted).ToArray();
            Assert.Equal(3, active.Length);
            Assert.Equal(3, fixture.Client.StartCount);
            var second = Assert.Single(active, run => run.PrefillScheduleId == config.Steam.Schedules[1].Id);
            Assert.Equal("silent", second.NotificationMode);
            tracker.CancelOperation(second.PrefillRunId);
            await second.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal([second.PrefillRunId], fixture.Client.Cancelled);
            Assert.Equal(3, fixture.Client.StartCount);
            Guid? resumedId = null;
            await WaitUntilAsync(() => (resumedId = scheduler.TriggerServiceRun(
                PrefillPlatform.Steam, config.Steam.Schedules[1].Id)).HasValue);
            await WaitUntilAsync(() => fixture.Client.StartCount == 4);
            foreach (var run in active.Where(run => run != second))
                fixture.Client.Set(run, "completed", 100, "success");
            await fixture.RefreshAsync();
            await WaitUntilAsync(() => tracker.GetActiveOperations(OperationType.ScheduledPrefill).Count() == 1);
            Assert.Null(scheduler.TriggerServiceRun(PrefillPlatform.Steam, config.Steam.Schedules[1].Id));
            var resumed = fixture.Daemon.GetRun(fixture.Session.Id, resumedId!.Value)!;
            fixture.Client.Set(resumed, "completed", 100, "success");
            await fixture.RefreshAsync();
            await WaitUntilAsync(() => !tracker.GetActiveOperations(OperationType.ScheduledPrefill).Any());
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public async Task EveryPlatformAdmitsIndependentSchedulesBesideManualRunAsync(PrefillPlatform platform)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemon = CreateDaemon(fixture, platform);
        var serviceType = platform switch
        {
            PrefillPlatform.Steam => typeof(SteamDaemonService),
            PrefillPlatform.Epic => typeof(EpicPrefillDaemonService),
            PrefillPlatform.Xbox => typeof(XboxPrefillDaemonService),
            PrefillPlatform.BattleNet => typeof(BattleNetDaemonService),
            _ => typeof(RiotDaemonService)
        };
        using var services = new ServiceCollection().AddSingleton(serviceType, daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var manual = await daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]);
        var first = MakeRun("20", platform: platform);
        var second = MakeRun("30", platform: platform);
        var claim = typeof(ScheduledPrefillService).GetMethod("TryClaimRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] original = [first.ServiceConfig, services, null];
        Assert.True((bool)claim.Invoke(scheduler, original)!);
        Assert.True((bool)claim.Invoke(scheduler, [second.ServiceConfig, services, null])!);
        Assert.False((bool)claim.Invoke(scheduler, [first.ServiceConfig, services, null])!);
        var release = typeof(ScheduledPrefillService).GetMethod("ReleaseRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        release.Invoke(scheduler, [first.ServiceConfig.ScheduleId, original[2], false]);
        Assert.True((bool)claim.Invoke(scheduler, [first.ServiceConfig, services, null])!);
        release.Invoke(scheduler, [first.ServiceConfig.ScheduleId, original[2], false]);
        Assert.False((bool)claim.Invoke(scheduler, [first.ServiceConfig, services, null])!);
        var method = typeof(ScheduledPrefillService).GetMethod("RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<ScheduledPrefillServiceRunResult> StartService(ScheduledPrefillServiceRun run)
            => (Task<ScheduledPrefillServiceRunResult>)method.Invoke(scheduler,
                [run, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(), true])!;
        var firstTask = StartService(first);
        var secondTask = StartService(second);
        await daemon.RefreshRunsAsync(fixture.Session.Id);
        Assert.Equal(3, fixture.Client.StartCount);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, await StartService(MakeRun("40", platform: platform)));
        var firstRun = daemon.GetRun(fixture.Session.Id, first.OperationId)!;
        var secondRun = daemon.GetRun(fixture.Session.Id, second.OperationId)!;
        fixture.Client.Set(firstRun, "completed", 100, "success");
        fixture.Client.Set(secondRun, "completed", 200, "success");
        await daemon.RefreshRunsAsync(fixture.Session.Id);
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await firstTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(daemon.GetRun(fixture.Session.Id, manual.RunId!.Value)!.Completion.Task.IsCompleted);
        Assert.Empty(fixture.Client.Cancelled);
    }

    [Fact]
    public async Task OptionalDaemonLookupPreservesAbsentAndRegisteredServicesAsync()
    {
        using var empty = new ServiceCollection().BuildServiceProvider();
        foreach (var platform in Enum.GetValues<PrefillPlatform>())
            Assert.Null(PrefillDaemonServiceBase.ResolveDaemon(empty, platform));
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        Assert.Same(fixture.Daemon, PrefillDaemonServiceBase.ResolveDaemon(services, PrefillPlatform.Steam));
    }

    [Fact]
    public async Task ThreeSchedulesShareCapacityWithManualWorkAndRefuseExcessAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var manual = await fixture.StartAsync("10");
        var first = MakeRun("20");
        var second = MakeRun("30");
        var firstTask = Start(scheduler, fixture, first);
        var secondTask = Start(scheduler, fixture, second);
        await fixture.RefreshAsync();
        Assert.Equal(3, fixture.Client.StartCount);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        var refused = await Start(scheduler, fixture, MakeRun("40"));
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, refused);
        Assert.Equal(3, fixture.Client.StartCount);
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped,
            await Start(scheduler, fixture, MakeRun("50", first.ServiceConfig.ScheduleId)));
        var firstRun = fixture.Daemon.GetRun(fixture.Session.Id, first.OperationId)!;
        var secondRun = fixture.Daemon.GetRun(fixture.Session.Id, second.OperationId)!;
        Assert.Equal("visible", firstRun.NotificationMode);
        fixture.Client.Set(firstRun, "completed", 100, "success");
        fixture.Client.Set(secondRun, "completed", 200, "success");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await firstTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await secondTask.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(manual.Completion.Task.IsCompleted);
        Assert.Equal(firstRun.CompletedAtUtc, first.State.CompletedAtUtc);
    }

    [Fact]
    public async Task CancelledWatcherWaitsForItsOwnDrainAndLeavesSiblingRunningAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        fixture.Client.CompleteCancellation = false;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        using var cancel = new CancellationTokenSource();
        var serviceRun = MakeRun("20") with { Token = cancel.Token };
        var sibling = await fixture.StartAsync("10");
        var task = Start(scheduler, fixture, serviceRun);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;
        await cancel.CancelAsync();
        await WaitUntilAsync(() => run.CancelRequested);
        Assert.False(task.IsCompleted);
        Assert.Equal([serviceRun.OperationId], fixture.Client.Cancelled);
        Assert.False(sibling.CancelRequested);
        fixture.Client.Set(run, "cancelled", 12, "cancelled");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(sibling.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task DisconnectedCancellationStaysPendingUntilSameInstanceReturnsAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        using var cancel = new CancellationTokenSource();
        var serviceRun = MakeRun("20") with { Token = cancel.Token };
        var task = Start(scheduler, fixture, serviceRun);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;
        fixture.Client.Offline = true;
        await fixture.RefreshAsync();
        await cancel.CancelAsync();
        await WaitUntilAsync(() => run.CancelRequested);
        Assert.True(fixture.Session.Recovering);
        Assert.False(task.IsCompleted);
        Assert.Empty(fixture.Client.Cancelled);
        fixture.Client.Offline = false;
        await fixture.RefreshAsync();
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, fixture.Client.StartCount);
        Assert.Equal([serviceRun.OperationId], fixture.Client.Cancelled);
    }

    [Fact]
    public async Task ShutdownDetachesPersistentWatcherWithoutCancellingDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var serviceRun = MakeRun("20");
        var task = Start(scheduler, fixture, serviceRun);
        await fixture.RefreshAsync();
        await scheduler.StopAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(serviceRun.State.Detached);
        Assert.Empty(fixture.Client.Cancelled);
        Assert.False(fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task RuntimeDeadlineTargetsOriginalRunAndWaitsForTerminalAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        fixture.Client.CompleteCancellation = false;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = await fixture.StartAsync("20");
        var sibling = await fixture.StartAsync("10");
        run.Snapshot = run.Snapshot with { StartedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var serviceRun = MakeRun("20") with { OperationId = run.PrefillRunId, OperationIdString = run.PrefillRunId.ToString() };
        var task = Watch(scheduler, fixture, run, serviceRun);
        await WaitUntilAsync(() => run.CancelRequested);
        Assert.Equal("runtime-exceeded", run.CancelReason);
        Assert.False(task.IsCompleted);
        Assert.False(sibling.CancelRequested);
        fixture.Client.Set(run, "cancelled", 12, "cancelled");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlapOnlySkipsWhileMixedSuccessCountsAsActualRunAsync(bool succeeded)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        using var scheduler = CreateScheduler(services, state);
        var serviceRun = MakeRun("20", ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam));
        if (succeeded) serviceRun.ServiceConfig.SelectedAppIds.Add("30");
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [serviceRun, DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>(),
                services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(), true, CancellationToken.None, true])!;
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;
        fixture.Client.Set(run, "completed", succeeded ? 100 : 0, succeeded ? "success" : "skipped");
        var page = fixture.Client.Pages[run.PrefillRunId];
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Operation = page.Operation with
            { SkippedApps = 1, TotalApps = succeeded ? 2 : 1, Reason = succeeded ? null : "skippedOverlap" },
            Items = succeeded ? [page.Items[0], new DaemonRunItem { AppId = "30", State = "skipped",
                Result = "skipped", Reason = "skippedOverlap", Sequence = page.Operation.Sequence }] : page.Items
        };
        await fixture.RefreshAsync();
        Assert.Equal(succeeded ? ScheduledPrefillServiceRunResult.Ran : ScheduledPrefillServiceRunResult.Skipped,
            await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(succeeded ? run.CompletedAtUtc : null, ((ScheduleState)(object)state).LastActualRun);
    }

    [Fact]
    public async Task RestorationKeepsIdentityStartTimeVisibilityAndCancellationOwnershipAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = Guid.NewGuid();
        var daemonRun = await fixture.StartAsync("20", id);
        var prior = new ScheduledPrefillServiceRunState(PrefillPlatform.Steam, id, "Nightly", false);
        prior.Record("recovering", "Waiting", null, 30, run: daemonRun);
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var events = (ScheduleNotifications)(object)notifications;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker)
            .AddSingleton(notifications).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);
        Restore(scheduler, config);
        var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(daemonRun.PrefillRunId));
        Assert.Equal(daemonRun.Snapshot.StartedAt.UtcDateTime, operation.StartedAt);
        var display = Assert.IsType<ScheduledPrefillServiceRunState>(operation.Metadata);
        Assert.Equal(id, display.ScheduleId);
        Assert.False(display.ShowNotification);
        var cts = operation.CancellationTokenSource;
        Restore(scheduler, config);
        Assert.Same(cts, tracker.GetOperation(daemonRun.PrefillRunId)!.CancellationTokenSource);
        Assert.Equal(1, fixture.Client.StartCount);
        await events.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var started = Assert.Single(events.Events, item => item.Event == SignalREvents.ScheduledPrefillStarted).Value;
        Assert.NotEqual(prior.Snapshot.EventEpoch, started.GetProperty("eventEpoch").GetGuid());
        Assert.Equal(1, started.GetProperty("eventSequence").GetInt64());
        Assert.Equal(daemonRun.DaemonInstanceId, started.GetProperty("daemonInstanceId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(started.GetProperty("message").GetString()));
        tracker.CancelOperation(daemonRun.PrefillRunId);
        events.Release.TrySetResult();
        await WaitUntilAsync(() => operation.Status == OperationStatus.Cancelled);
        Assert.Equal([daemonRun.PrefillRunId], fixture.Client.Cancelled);
        var completed = Assert.Single(events.Events, item => item.Event == SignalREvents.ScheduledPrefillCompleted).Value;
        Assert.Equal(daemonRun.DaemonInstanceId, completed.GetProperty("daemonInstanceId").GetString());
        Assert.Equal(started.GetProperty("eventEpoch").GetGuid(), completed.GetProperty("eventEpoch").GetGuid());
        Assert.True(completed.GetProperty("eventSequence").GetInt64() > started.GetProperty("eventSequence").GetInt64());
    }

    [Fact]
    public async Task RejectedRestorationDisposesOnlyTheUnadoptedCancellationSourceAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var run = await fixture.StartAsync("20", Guid.NewGuid());
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var cts = new CancellationTokenSource();
        Assert.True(tracker.TryRestoreOperation(run.PrefillRunId, OperationType.ScheduledPrefill, "Existing", cts));
        var tracking = DispatchProxy.Create<IUnifiedOperationTracker, ScheduleTracker>();
        var calls = (ScheduleTracker)(object)tracking;
        calls.Tracker = tracker;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).AddSingleton(tracking)
            .AddSingleton(Notifications()).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        Restore(scheduler, ScheduledPrefillConfigFactory.CreateDefault());
        Assert.Throws<ObjectDisposedException>(() => calls.Rejected!.Token);
        Assert.False(cts.Token.IsCancellationRequested);
        Assert.Same(cts, tracker.GetOperation(run.PrefillRunId)!.CancellationTokenSource);
        tracker.CompleteOperation(run.PrefillRunId, true);
    }

    [Fact]
    public async Task RelayRejectsSiblingAndStaleProgressBeforeChangingCountersAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var response = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10", "20", "30"]);
        await fixture.RefreshAsync();
        var first = fixture.Daemon.GetRun(fixture.Session.Id, response.RunId!.Value)!;
        var sibling = await fixture.StartAsync("40");
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var serviceRun = MakeRun("10") with { OperationId = first.PrefillRunId, OperationIdString = first.PrefillRunId.ToString() };
        var relayType = typeof(ScheduledPrefillService).GetNestedType("ScheduledPrefillProgressRelay", BindingFlags.NonPublic)!;
        var relay = Activator.CreateInstance(relayType, BindingFlags.Instance | BindingFlags.NonPublic,
            null, [scheduler, Notifications(), fixture.Session, serviceRun, fixture.Session.Id, true], null)!;
        relayType.GetMethod("Arm", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(relay, null);
        var progress = new PrefillProgress
        {
            OperationId = first.PrefillRunId.ToString(),
            DaemonInstanceId = first.DaemonInstanceId,
            State = "downloading",
            TotalApps = 3,
            UpdatedApps = 1,
            SkippedApps = 1
        };
        var method = relayType.GetMethod("OnProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(relay, [fixture.Session, progress, 2L])!;
        Assert.InRange(serviceRun.State.PercentComplete!.Value, 66, 67);
        Assert.Contains("2 of 3 games", serviceRun.State.Message, StringComparison.Ordinal);
        progress.FailedApps = 7;
        await (Task)method.Invoke(relay, [fixture.Session, progress, 1L])!;
        progress.OperationId = sibling.PrefillRunId.ToString();
        await (Task)method.Invoke(relay, [fixture.Session, progress, 3L])!;
        progress.OperationId = first.PrefillRunId.ToString();
        progress.DaemonInstanceId = Guid.NewGuid().ToString();
        await (Task)method.Invoke(relay, [fixture.Session, progress, 4L])!;
        Assert.Equal(0, relayType.GetProperty("FailedApps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(relay));
    }

    private static ScheduledPrefillService CreateScheduler(ServiceProvider services, IStateService? state = null)
        => new(NullLogger<ScheduledPrefillService>.Instance, services.GetRequiredService<IServiceScopeFactory>(),
            state ?? DispatchProxy.Create<IStateService, ScheduleState>());

    private static Task<ScheduledPrefillSnapshot?> Report(ScheduledPrefillService scheduler,
        ISignalRNotificationService notifications, ScheduledPrefillServiceRun run, string stage, string message,
        double? percent, Dictionary<string, object?>? context = null, long? bytes = null,
        long? totalBytes = null, bool clearPercent = false)
        => (Task<ScheduledPrefillSnapshot?>)typeof(ScheduledPrefillService)
            .GetMethod("ReportProgressAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [notifications, run, stage, message, true, null, bytes, "session", percent,
                totalBytes, "signalr.scheduledPrefill.downloadingGame", context, clearPercent, null, null, false, false])!;

    private static ISignalRNotificationService Notifications()
        => DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();

    private static ScheduledPrefillServiceRun MakeRun(string app, Guid? scheduleId = null,
        PrefillPlatform platform = PrefillPlatform.Steam)
    {
        var id = scheduleId ?? Guid.NewGuid();
        var operation = Guid.NewGuid();
        var config = new ScheduledPrefillServiceConfigDto
        {
            ServiceId = platform,
            ScheduleId = id,
            ScheduleName = "Nightly",
            Enabled = true,
            NotificationMode = NotificationMode.All,
            IntervalHours = 24,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = [app],
            OperatingSystems = [],
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
        return new ScheduledPrefillServiceRun(config, operation, operation.ToString(), operation.ToString(),
            new ScheduledPrefillServiceRunState(platform, id, "Nightly", true), CancellationToken.None);
    }

    private static PrefillDaemonServiceBase CreateDaemon(RunFixture fixture, PrefillPlatform platform)
    {
        if (platform == PrefillPlatform.Steam) return fixture.Daemon;
        var configuration = new ConfigurationBuilder().Build();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        var cache = new PrefillCacheService(new TestDbContextFactory(fixture.Options), NullLogger<PrefillCacheService>.Instance);
        PrefillDaemonServiceBase daemon = platform switch
        {
            PrefillPlatform.Epic => new EpicPrefillDaemonService(NullLogger<EpicPrefillDaemonService>.Instance,
                Notifications(), configuration, null!, state, fixture.History, cache, null!, null!,
                new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory()),
            PrefillPlatform.Xbox => new XboxPrefillDaemonService(NullLogger<XboxPrefillDaemonService>.Instance,
                Notifications(), configuration, null!, state, fixture.History, cache, null!, null!,
                new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory()),
            PrefillPlatform.BattleNet => new BattleNetDaemonService(NullLogger<BattleNetDaemonService>.Instance,
                Notifications(), configuration, null!, state, fixture.History, cache, null!,
                new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory()),
            _ => new RiotDaemonService(NullLogger<RiotDaemonService>.Instance,
                Notifications(), configuration, null!, state, fixture.History, cache, null!,
                new TestLancacheServerLocator(), new UnavailableContainerGatewayFactory())
        };
        var sessions = (ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(daemon)!;
        sessions[fixture.Session.Id] = fixture.Session;
        return daemon;
    }

    private static Task<ScheduledPrefillServiceRunResult> Start(ScheduledPrefillService scheduler, RunFixture fixture,
        ScheduledPrefillServiceRun run)
        => (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("StartRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, run, Notifications(), ScheduledPrefillConfigFactory.CreateDefault()])!;

    private static Task<ScheduledPrefillServiceRunResult> Watch(ScheduledPrefillService scheduler, RunFixture fixture,
        DaemonRun run, ScheduledPrefillServiceRun serviceRun)
        => (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("WatchRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, run, serviceRun, Notifications(), ScheduledPrefillConfigFactory.CreateDefault()])!;

    private static void Restore(ScheduledPrefillService scheduler, ScheduledPrefillConfigDto config)
        => typeof(ScheduledPrefillService).GetMethod("RestoreRuns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [config]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}

internal class ScheduleState : NullReturningProxy
{
    public ScheduledPrefillConfigDto Config { get; set; } = ScheduledPrefillConfigFactory.CreateDefault();
    public DateTime? LastActualRun { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig)) return Config;
        if (targetMethod?.Name == nameof(IStateService.SetScheduledPrefillServiceLastActualRun))
            LastActualRun = (DateTime)args![1]!;
        return base.Invoke(targetMethod, args);
    }
}

internal class ScheduleNotifications : NullReturningProxy
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ConcurrentQueue<(string Event, JsonElement Value)> Events { get; } = new();
    public Func<string, JsonElement, Task>? OnSend { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync) && args?[0] is string name)
        {
            var value = JsonSerializer.SerializeToElement(args[1]);
            Events.Enqueue((name, value));
            if (OnSend is not null) return OnSend(name, value);
            if (name == SignalREvents.ScheduledPrefillStarted)
            {
                Started.TrySetResult();
                return Release.Task;
            }
        }
        return base.Invoke(targetMethod, args);
    }
}

internal class ScheduleTracker : DispatchProxy
{
    public IUnifiedOperationTracker Tracker { get; set; } = null!;
    public CancellationTokenSource? Rejected { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = targetMethod!.Invoke(Tracker, args);
        if (targetMethod.Name == nameof(IUnifiedOperationTracker.TryRestoreOperation) && result is false)
            Rejected = (CancellationTokenSource)args![3]!;
        return result;
    }
}
