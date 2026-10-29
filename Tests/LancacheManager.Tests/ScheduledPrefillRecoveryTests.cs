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
                CancellationToken.None, false, null])!;
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, await task);
        var events = capture.Events.ToArray();
        Assert.Equal([SignalREvents.ScheduledPrefillStarted, SignalREvents.ScheduledPrefillProgress,
            SignalREvents.ScheduledPrefillCompleted], events.Select(item => item.Event));
        Assert.Equal([1L, 2L, 3L], events.Select(item => item.Value.GetProperty("eventSequence").GetInt64()));
        Assert.Equal("signalr.scheduledPrefill.starting", events[0].Value.GetProperty("stageKey").GetString());
        Assert.All(events, item =>
        {
            Assert.Equal(run.State.Snapshot.EventEpoch, item.Value.GetProperty("eventEpoch").GetGuid());
            Assert.Equal(run.OperationIdString, item.Value.GetProperty("operationId").GetString());
            Assert.Equal(JsonValueKind.Null, item.Value.GetProperty("daemonInstanceId").ValueKind);
            // The run's row decides how it shows, so no event carries a visibility of its own.
            Assert.DoesNotContain(item.Value.EnumerateObject(),
                property => property.Name.EndsWith("Notification", StringComparison.Ordinal));
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
            DispatchProxy.Create<IStateService, ScheduleState>(), null!, tracker, scheduler,
            ScheduleExecutionTestService.Create());
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
        await (Task)complete.Invoke(null, [run, tracker, notifications, ScheduledPrefillServiceRunResult.Failed, "Download failed"])!;
        var terminal = run.State.Snapshot;
        Assert.Equal(2, terminal.EventSequence);
        Assert.Equal("failed", terminal.Stage);
        Assert.Equal("Download failed", terminal.Message);
        Assert.Null(run.State.Record("running", "Late", null, 99));
        await (Task)complete.Invoke(null, [run, tracker, notifications, ScheduledPrefillServiceRunResult.Ran, null])!;
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
            null, [scheduler, notifications, fixture.Session, run, fixture.Session.Id], null)!;
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
            null, [scheduler, notifications, fixture.Session, run, fixture.Session.Id], null)!;
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

    [Theory]
    [InlineData("starting")]
    [InlineData("recovering")]
    [InlineData("cancelling")]
    public async Task EachWatchedStageLineCarriesTheKeyItIsTranslatedByAsync(string from)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemonRun = await fixture.StartAsync("10");
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var run = MakeRun("10") with { OperationId = daemonRun.PrefillRunId, OperationIdString = daemonRun.PrefillRunId.ToString() };
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        var line = new TaskCompletionSource<(JsonElement Sent, string? Recorded)>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.OnSend = (name, value) =>
        {
            // The relay's replay of the daemon's last tick carries its own key; the watch loop's line
            // is the other one. The recorded line is read as it is sent, before a later tick replaces it.
            if (name == SignalREvents.ScheduledPrefillProgress
                && value.GetProperty("stageKey").GetString() is not ("signalr.scheduledPrefill.runningWithCounts"
                    or "signalr.scheduledPrefill.downloadingGame"))
                line.TrySetResult((value, run.State.Snapshot.StageKey));
            return Task.CompletedTask;
        };
        run.State.Record("starting", "Starting scheduled prefill", "signalr.scheduledPrefill.starting", null);
        // No daemon tick yet, so the watch loop's own line is the run's first progress line.
        daemonRun.LastProgress = null;
        daemonRun.Recovering = from == "recovering";
        fixture.Session.Recovering = from == "recovering";
        daemonRun.CancelRequested = from == "cancelling";
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("WatchRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, daemonRun, run, notifications, ScheduledPrefillConfigFactory.CreateDefault()])!;

        var (sent, recorded) = await line.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stage = sent.GetProperty("stage").GetString();
        Assert.Contains(stage, from == "starting" ? ["preparing", "running"] : new[] { from });
        var expected = stage switch
        {
            "preparing" => "signalr.scheduledPrefill.preparing",
            "recovering" => "signalr.scheduledPrefill.reconnecting",
            "cancelling" => "signalr.scheduledPrefill.stopping",
            _ => "signalr.scheduledPrefill.running"
        };
        Assert.Equal(expected, sent.GetProperty("stageKey").GetString());
        // A browser that reloads rebuilds the card from the recorded line, so it carries the same key.
        Assert.Equal(expected, recorded);

        daemonRun.Recovering = false;
        fixture.Session.Recovering = false;
        fixture.Client.Set(daemonRun, from == "cancelling" ? "cancelled" : "completed", 50,
            from == "cancelling" ? "cancelled" : "success");
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
        object?[] original = [first.ServiceConfig, null];
        Assert.True((bool)claim.Invoke(scheduler, original)!);
        Assert.True((bool)claim.Invoke(scheduler, [second.ServiceConfig, null])!);
        Assert.False((bool)claim.Invoke(scheduler, [first.ServiceConfig, null])!);
        var release = typeof(ScheduledPrefillService).GetMethod("ReleaseRun", BindingFlags.Instance | BindingFlags.NonPublic)!;
        release.Invoke(scheduler, [first.ServiceConfig.ScheduleId, original[1]]);
        Assert.True((bool)claim.Invoke(scheduler, [first.ServiceConfig, null])!);
        release.Invoke(scheduler, [first.ServiceConfig.ScheduleId, original[1]]);
        Assert.False((bool)claim.Invoke(scheduler, [first.ServiceConfig, null])!);
        var method = typeof(ScheduledPrefillService).GetMethod("RunServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task<ScheduledPrefillServiceRunResult> StartService(ScheduledPrefillServiceRun run)
            => (Task<ScheduledPrefillServiceRunResult>)method.Invoke(scheduler,
                [run, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(), null])!;
        var firstTask = StartService(first);
        var secondTask = StartService(second);
        await daemon.RefreshRunsAsync(fixture.Session.Id);
        Assert.Equal(3, fixture.Client.StartCount);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        Assert.Equal(ScheduledPrefillServiceRunResult.Unavailable, await StartService(MakeRun("40", platform: platform)));
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
        Assert.Equal(ScheduledPrefillServiceRunResult.Unavailable, refused);
        Assert.Equal(3, fixture.Client.StartCount);
        Assert.Equal(ScheduledPrefillServiceRunResult.Unavailable,
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

    [Fact]
    public async Task AUserCancelSeenWithTheTimeLimitIsSentAsTheUsersCancelAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        fixture.Client.CompleteCancellation = false;
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        using var cancel = new CancellationTokenSource();
        var run = await fixture.StartAsync("20");
        run.Snapshot = run.Snapshot with { StartedAt = DateTimeOffset.UtcNow.AddDays(-2) };
        var serviceRun = MakeRun("20") with
        {
            OperationId = run.PrefillRunId,
            OperationIdString = run.PrefillRunId.ToString(),
            Token = cancel.Token
        };
        await cancel.CancelAsync();
        var task = Watch(scheduler, fixture, run, serviceRun);
        await WaitUntilAsync(() => run.CancelRequested);
        Assert.Null(run.CancelReason);
        fixture.Client.Set(run, "cancelled", 12, "cancelled");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, await task.WaitAsync(TimeSpan.FromSeconds(5)));
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
                services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(), CancellationToken.None, true, null])!;
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

    /// <summary>
    /// The daemons call a run in which any game failed "failed" (Battle.net with a reason). It completes
    /// amber naming how many games failed when every game ended with its own result and one landed. A run
    /// stopped partway (a lost sign-in, a crash) with a game done is amber naming what stopped it; all
    /// failed, or nothing done, stays red.
    /// </summary>
    [Theory]
    [InlineData(PrefillPlatform.Steam, null, "success,failed", "signalr.scheduledPrefill.failedApps")]
    [InlineData(PrefillPlatform.Steam, null, "already_cached,failed", "signalr.scheduledPrefill.failedApps")]
    [InlineData(PrefillPlatform.BattleNet, "download-failed", "success,failed", "signalr.scheduledPrefill.failedApps")]
    [InlineData(PrefillPlatform.Steam, null, "success,failed,nothing-to-download", "signalr.scheduledPrefill.failedApps")]
    [InlineData(PrefillPlatform.BattleNet, "download-failed", "success,failed,nothing-to-download", "signalr.scheduledPrefill.failedApps")]
    [InlineData(PrefillPlatform.Steam, null, "failed,failed", null)]
    [InlineData(PrefillPlatform.Steam, "auth-lost", "success,failed,skipped", "signalr.scheduledPrefill.partialSignInLost")]
    [InlineData(PrefillPlatform.BattleNet, "download-failed", "success,failed,skipped", "signalr.scheduledPrefill.partialStopped")]
    public async Task ARunWithSomeGamesDoneEndsAmberNamingWhatStoppedTheRestAsync(
        PrefillPlatform platform, string? reason, string results, string? warningKey)
    {
        var ran = warningKey is not null;
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemon = CreateDaemon(fixture, platform);
        var serviceType = platform == PrefillPlatform.Steam ? typeof(SteamDaemonService) : typeof(BattleNetDaemonService);
        using var services = new ServiceCollection().AddSingleton(serviceType, daemon).BuildServiceProvider();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        using var scheduler = CreateScheduler(services, state);
        var games = results.Split(',');
        var serviceRun = MakeRun("20", ScheduledPrefillConfigFactory.GetDefaultScheduleId(platform), platform);
        foreach (var app in new[] { "30", "40" }.Take(games.Length - 1))
            serviceRun.ServiceConfig.SelectedAppIds.Add(app);
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [serviceRun, DispatchProxy.Create<IUnifiedOperationTracker, NullReturningProxy>(),
                services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(), CancellationToken.None, true, null])!;
        await daemon.RefreshRunsAsync(fixture.Session.Id);
        var run = daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;

        FinishFailedRun(fixture, run, reason, games);
        await daemon.RefreshRunsAsync(fixture.Session.Id);

        Assert.Equal(ran ? ScheduledPrefillServiceRunResult.Ran : ScheduledPrefillServiceRunResult.Failed,
            await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ran ? run.CompletedAtUtc : null, ((ScheduleState)(object)state).LastActualRun);
        Assert.Equal(warningKey, serviceRun.State.Warning?.StageKey);
        var failedApps = warningKey == "signalr.scheduledPrefill.failedApps";
        Assert.Equal(failedApps ? 1 : (int?)null, (int?)serviceRun.State.Warning?.Context.GetValueOrDefault("failed"));
        Assert.Equal(ran && !failedApps ? 1 : (int?)null, (int?)serviceRun.State.Warning?.Context.GetValueOrDefault("done"));
        Assert.Equal(ran ? games.Length : (int?)null, (int?)serviceRun.State.Warning?.Context["total"]);
    }

    /// <summary>
    /// A Steam run a drop cut off before any game finished ends red with the prefill text on its card and on its
    /// run row: the app may sign the always-on container in again by itself, which the Steam text does not say.
    /// </summary>
    [Fact]
    public async Task ASteamRunADropEndsBeforeAnyGameReadsAsTheRunRowAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var daemon = CreateDaemon(fixture, PrefillPlatform.Steam);
        using var services = new ServiceCollection().AddSingleton(typeof(SteamDaemonService), daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var serviceRun = MakeRun("20", ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam), PrefillPlatform.Steam);
        serviceRun.ServiceConfig.SelectedAppIds.Add("30");
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(serviceRun.OperationId, OperationType.ScheduledPrefill, serviceRun.State.Name,
            new CancellationTokenSource(), serviceRun.State));
        var task = (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [serviceRun, tracker, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(),
                CancellationToken.None, true, null])!;
        await daemon.RefreshRunsAsync(fixture.Session.Id);
        var run = daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;

        FinishFailedRun(fixture, run, "auth-lost", ["failed", "skipped"]);
        await daemon.RefreshRunsAsync(fixture.Session.Id);

        Assert.Equal(ScheduledPrefillServiceRunResult.Failed, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("errors.steam.signInLost", run.ErrorStageKey);
        Assert.Equal("errors.prefill.signInLost", serviceRun.State.Snapshot.StageKey);
        Assert.Equal("errors.prefill.signInLost",
            tracker.GetRuns().Runs.Single(row => row.OperationId == serviceRun.OperationId).Error);
    }

    /// <summary>
    /// A run that could not start because something it needs is missing ends red with its own reason; a
    /// run that was only put off (the container is busy) ends skipped. "noContainer" is a Docker that answers
    /// and lists no container; "noDocker" is a Docker that cannot be reached.
    /// </summary>
    [Theory]
    [InlineData(PrefillPlatform.Steam, "noDaemon", OperationStatus.Failed, "signalr.scheduledPrefill.skippedNoDaemon")]
    [InlineData(PrefillPlatform.Steam, "noContainer", OperationStatus.Failed, "signalr.scheduledPrefill.needsPersistentContainer")]
    [InlineData(PrefillPlatform.BattleNet, "noContainer", OperationStatus.Failed, "signalr.scheduledPrefill.skippedNoContainer")]
    [InlineData(PrefillPlatform.Steam, "noDocker", OperationStatus.Failed, "errors.prefill.dockerUnavailable")]
    [InlineData(PrefillPlatform.BattleNet, "noDocker", OperationStatus.Failed, "errors.prefill.dockerUnavailable")]
    [InlineData(PrefillPlatform.Steam, "loggedOut", OperationStatus.Failed, "signalr.scheduledPrefill.needsPersistentLogin")]
    public async Task ARunThatCouldNotStartEndsByItsCauseAsync(
        PrefillPlatform platform, string cause, OperationStatus status, string stageKey)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: cause is not ("noContainer" or "noDocker"));
        var serviceType = platform == PrefillPlatform.Steam ? typeof(SteamDaemonService) : typeof(BattleNetDaemonService);
        var registered = new ServiceCollection();
        if (cause != "noDaemon")
        {
            var daemon = CreateDaemon(fixture, platform);
            if (cause == "noContainer")
            {
                typeof(PrefillDaemonServiceBase).GetField("_containerGateway", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(daemon, new RecordingContainerGateway());
            }
            registered.AddSingleton(serviceType, daemon);
        }
        using var services = registered.BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        // The daemon's status says whether the container is signed in.
        fixture.Client.Status = RunClient.Capabilities(fixture.Client.InstanceId);
        fixture.Client.Status.Status = cause == "loggedOut" ? "awaiting-login" : "logged-in";
        var run = MakeRun("20", platform: platform);
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(run.OperationId, OperationType.ScheduledPrefill, run.State.Name,
            new CancellationTokenSource(), run.State));

        await (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [run, tracker, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(),
                CancellationToken.None, true, null])!;

        Assert.Equal(status, tracker.GetOperation(run.OperationId)!.Status);
        Assert.Equal(stageKey, run.State.Snapshot.StageKey);
    }

    // ---------- A Stop that ends the session under a scheduled run ----------
    // A Stop or an image update takes the session out of the daemon before its teardown runs. A run that
    // meets the ended session ends with the no-container reason, not as a failed daemon command.

    [Theory]
    [InlineData(PrefillPlatform.Steam, true)]
    [InlineData(PrefillPlatform.Steam, false)]
    [InlineData(PrefillPlatform.BattleNet, true)]
    [InlineData(PrefillPlatform.BattleNet, false)]
    public async Task ARunDoesNotTakeATickSessionThatAStopEndedAsync(PrefillPlatform platform, bool concurrent)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, platform, out var daemon);
        using var scheduler = CreateScheduler(services);
        if (!concurrent)
        {
            fixture.Session.Capabilities = null;
            fixture.Client.Status = new DaemonStatus { Status = "logged-in" };
        }
        var tickAdopt = Task.FromResult<DaemonSession?>(fixture.Session);
        EndSession(daemon, fixture.Session);
        var run = MakeRun("20", platform: platform);
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(run.OperationId, OperationType.ScheduledPrefill, run.State.Name,
            new CancellationTokenSource(), run.State));

        var result = await (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [run, tracker, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(),
                CancellationToken.None, true, tickAdopt])!;

        AssertNoContainerEnding(platform, result, run);
        Assert.Equal(OperationStatus.Failed, tracker.GetOperation(run.OperationId)!.Status);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AStopDuringARunsStatusReadEndsItWithNoContainerAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.Steam, out var daemon);
        using var scheduler = CreateScheduler(services);
        fixture.Client.StatusRead = () =>
        {
            EndSession(daemon, fixture.Session);
            return Task.FromResult<DaemonStatus?>(null);
        };
        var run = MakeRun("20");

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        AssertNoContainerEnding(PrefillPlatform.Steam, result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStopDuringAnOlderDaemonsStatusReadEndsItWithNoContainerAsync(bool answers)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.BattleNet, out var daemon);
        using var scheduler = CreateScheduler(services);
        fixture.Session.Capabilities = null;
        fixture.Client.StatusRead = () =>
        {
            EndSession(daemon, fixture.Session);
            return Task.FromResult<DaemonStatus?>(answers ? new DaemonStatus { Status = "logged-in" } : null);
        };
        var run = MakeRun("20", platform: PrefillPlatform.BattleNet);

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        AssertNoContainerEnding(PrefillPlatform.BattleNet, result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Fact]
    public async Task AStopAfterTheDaemonAcceptedTheRunEndsItWithNoContainerAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.BattleNet, out var daemon);
        using var scheduler = CreateScheduler(services);
        var client =DispatchProxy.Create<IDaemonClient, StopOnPrefillClient>();
        var stopping = (StopOnPrefillClient)(object)client;
        stopping.InstanceId = fixture.Client.InstanceId;
        stopping.OnPrefill = () => EndSession(daemon, fixture.Session);
        fixture.Session.Client = client;
        var run = MakeRun("20", platform: PrefillPlatform.BattleNet);

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        AssertNoContainerEnding(PrefillPlatform.BattleNet, result, run);
        Assert.Equal(1, stopping.StartCount);
    }

    // A person's Start replaces an errored session without the change gate, so it can land inside the
    // older daemon's status read. The new container is being created, so the run is skipped as busy.
    [Fact]
    public async Task AnOlderDaemonsStatusReadThatAStartReplacedTheSessionDuringIsSkippedAsBusyAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.Steam, out var daemon);
        using var scheduler = CreateScheduler(services);
        fixture.Session.Capabilities = null;
        var reads = 0;
        fixture.Client.StatusRead = () =>
        {
            if (++reads == 2) ReplaceSession(daemon, fixture.Session);
            return Task.FromResult<DaemonStatus?>(null);
        };
        var run = MakeRun("20");

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        Assert.Equal(2, reads);
        AssertBusyEnding(result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    // An image update ends the session and starts the new container itself, so the run that met the
    // ended session is skipped as busy and retried, not told to start a container.
    [Fact]
    public async Task ARunWhoseSessionAnImageUpdateReplacedIsSkippedAsBusyAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.Steam, out var daemon);
        using var scheduler = CreateScheduler(services);
        fixture.Client.StatusRead = () =>
        {
            ReplaceSession(daemon, fixture.Session);
            return Task.FromResult<DaemonStatus?>(RunClient.Capabilities(fixture.Client.InstanceId));
        };
        var run = MakeRun("20");

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        AssertBusyEnding(result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    // A live session whose admission an image update has closed refuses the run before adding it.
    [Fact]
    public async Task ARunRefusedWhileAnImageUpdateHoldsAdmissionIsSkippedAsBusyAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.Steam, out _);
        using var scheduler = CreateScheduler(services);
        fixture.Session.AdmissionClosed = true;
        var run = MakeRun("20");

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        AssertBusyEnding(result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOlderDaemonsSessionThatEndedBeforeTheChangeLeaseEndsWithAKeyAsync(bool replaced)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = DaemonWithNoContainer(fixture, PrefillPlatform.Steam, out var daemon);
        using var scheduler = CreateScheduler(services);
        fixture.Session.Capabilities = null;
        fixture.Client.StatusRead = () =>
        {
            if (replaced) ReplaceSession(daemon, fixture.Session);
            else EndSession(daemon, fixture.Session);
            return Task.FromResult<DaemonStatus?>(new DaemonStatus { Status = "logged-in" });
        };
        var run = MakeRun("20");

        var result = await StartGatedRun(scheduler, services, run, new CancellationTokenSource());

        if (replaced) AssertBusyEnding(result, run);
        else AssertNoContainerEnding(PrefillPlatform.Steam, result, run);
        Assert.Equal(0, fixture.Client.StartCount);
    }

    // ---------- Scheduled Steam run after a dropped sign-in ----------
    // The daemon reports the drop code only after a dropped connection, so a scheduled Steam run signs in
    // with the saved login on that code alone; a log-off or a person's Log out leaves it signed out.

    [Fact]
    public async Task AScheduledSteamRunSignsInWithTheSavedLoginFirstAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Client.Status = SignedOutStatus(fixture, "auth-lost");
        fixture.Client.SignInOnStartLogin = true;
        var serviceRun = MakeRun("20");

        var result = StartGatedRun(scheduler, services, serviceRun, new CancellationTokenSource());
        await WaitUntilAsync(() => fixture.Client.StartCount == 1);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;

        Assert.Equal(1, fixture.Client.StartLoginCalls);
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AScheduledSteamRunWaitsForASignInAlreadyRunningAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Client.Status = SignedOutStatus(fixture, "auth-lost");
        var serviceRun = MakeRun("20");
        // Stands for a retry's sign-in holding the lock.
        await fixture.Session.LoginLock.WaitAsync();

        var result = StartGatedRun(scheduler, services, serviceRun, new CancellationTokenSource());
        await Task.WhenAny(result, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.False(result.IsCompleted);

        // The other sign-in finishes and the run goes on with its result.
        fixture.Session.AuthState = DaemonAuthState.Authenticated;
        fixture.Client.Status = null;
        fixture.Session.LoginLock.Release();
        await WaitUntilAsync(() => fixture.Client.StartCount == 1);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;

        Assert.Equal(0, fixture.Client.StartLoginCalls);
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AScheduledRunCanceledDuringItsSignInEndsCanceledAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Client.Status = SignedOutStatus(fixture, "auth-lost");
        using var cancel = new CancellationTokenSource();
        var serviceRun = MakeRun("20") with { Token = cancel.Token };
        await fixture.Session.LoginLock.WaitAsync();

        var result = StartGatedRun(scheduler, services, serviceRun, cancel);
        // The status read before the wait starts the background retry.
        await WaitUntilAsync(() => fixture.Session.SignInAgainAfterDrop is not null);
        await cancel.CancelAsync();

        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.NotEqual("signalr.scheduledPrefill.needsPersistentLogin", serviceRun.State.Snapshot.StageKey);
        fixture.Session.LoginLock.Release();
    }

    [Fact]
    public async Task AScheduledRunWhoseSignInIsRefusedStillEndsNeedsLoginAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Client.Status = SignedOutStatus(fixture, "auth-lost");
        // A person's cancel of an earlier sign-in is still finishing, so the sign-in is refused.
        fixture.Session.LoginCanceling = true;
        var serviceRun = MakeRun("20");

        var result = StartGatedRun(scheduler, services, serviceRun, new CancellationTokenSource());

        Assert.Equal(ScheduledPrefillServiceRunResult.NeedsLogin, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("signalr.scheduledPrefill.needsPersistentLogin", serviceRun.State.Snapshot.StageKey);
    }

    [Fact]
    public async Task AScheduledRunCanceledWhileItsSignInStepFailsEndsCanceledAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        using var cancel = new CancellationTokenSource();
        var serviceRun = MakeRun("20") with { Token = cancel.Token };
        // The person cancels the run while the daemon status read fails with something other than a cancel.
        fixture.Client.StatusRead = async () =>
        {
            await cancel.CancelAsync();
            throw new IOException("Connection to the daemon was lost");
        };

        var result = StartGatedRun(scheduler, services, serviceRun, cancel);

        Assert.Equal(ScheduledPrefillServiceRunResult.Cancelled, await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AScheduledSteamRunAfterALogOffTriesNoSignInAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        fixture.Client.Status = SignedOutStatus(fixture, null);
        fixture.Client.SignInOnStartLogin = true;
        var serviceRun = MakeRun("20");

        var result = StartGatedRun(scheduler, services, serviceRun, new CancellationTokenSource());

        Assert.Equal(ScheduledPrefillServiceRunResult.NeedsLogin, await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("signalr.scheduledPrefill.needsPersistentLogin", serviceRun.State.Snapshot.StageKey);
        Assert.Equal(0, fixture.Client.StartLoginCalls);
    }

    [Fact]
    public async Task AScheduledSteamRunStartsTheRetryForADropTheAppMissedAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        // The app still reads signed in although the daemon dropped the sign-in.
        fixture.Session.AuthState = DaemonAuthState.Authenticated;
        fixture.Client.Status = SignedOutStatus(fixture, "auth-lost");
        var serviceRun = MakeRun("20");
        await fixture.Session.LoginLock.WaitAsync();

        var result = StartGatedRun(scheduler, services, serviceRun, new CancellationTokenSource());
        await WaitUntilAsync(() => fixture.Session.SignInAgainAfterDrop is not null);
        Assert.Equal(DaemonAuthState.NotAuthenticated, fixture.Session.AuthState);

        fixture.Session.AuthState = DaemonAuthState.Authenticated;
        fixture.Client.Status = null;
        fixture.Session.LoginLock.Release();
        await WaitUntilAsync(() => fixture.Client.StartCount == 1);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();

        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await result.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ARunThatFinishedWhileTheAppWasDownWithSomeFailedGamesStampsItsLastRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        var started = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["20", "30"], scheduleId: id,
            scheduleName: "Named schedule", notificationMode: "silent");
        await fixture.RefreshAsync();
        var daemonRun = fixture.Daemon.GetRun(fixture.Session.Id, started.RunId!.Value)!;
        FinishFailedRun(fixture, daemonRun, null, ["success", "failed"]);
        await fixture.RefreshAsync();
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);

        Restore(scheduler, config);

        Assert.Equal("failed", daemonRun.Snapshot.State);
        Assert.NotNull(daemonRun.CompletedAtUtc);
        Assert.Equal(daemonRun.CompletedAtUtc, ((ScheduleState)(object)state).LastActualRun);
    }

    [Fact]
    public async Task ARunThatEndedInSessionWithSomeFailedGamesStampsItsLastRunAfterARestartAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        var started = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["20", "30"], scheduleId: id,
            scheduleName: "Named schedule", notificationMode: "silent");
        await fixture.RefreshAsync();
        var daemonRun = fixture.Daemon.GetRun(fixture.Session.Id, started.RunId!.Value)!;
        FinishFailedRun(fixture, daemonRun, null, ["success", "failed"]);
        await fixture.RefreshAsync();
        fixture.Session.Runs.Clear();
        await fixture.RefreshAsync();
        var reloaded = fixture.Daemon.GetRun(fixture.Session.Id, started.RunId!.Value)!;
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);

        Restore(scheduler, config);

        Assert.NotNull(reloaded.CompletedAtUtc);
        Assert.Equal(reloaded.CompletedAtUtc, ((ScheduleState)(object)state).LastActualRun);
    }

    [Fact]
    public async Task ARestoredRunThatEndsWithSomeFailedGamesEndsAmberAndStampsItsLastRunAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        var started = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["20", "30"], scheduleId: id,
            scheduleName: "Named schedule", notificationMode: "silent");
        await fixture.RefreshAsync();
        var daemonRun = fixture.Daemon.GetRun(fixture.Session.Id, started.RunId!.Value)!;
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker)
            .AddSingleton(Notifications()).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);
        Restore(scheduler, config);
        var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(daemonRun.PrefillRunId));

        FinishFailedRun(fixture, daemonRun, null, ["success", "failed"]);
        await fixture.RefreshAsync();
        await WaitUntilAsync(() => operation.Status is OperationStatus.Completed or OperationStatus.Failed);

        var row = Assert.Single(tracker.GetRuns().Runs, item => item.OperationId == daemonRun.PrefillRunId);
        Assert.Equal("completed", row.Status);
        Assert.Equal("signalr.scheduledPrefill.failedApps", Assert.Single(row.Warnings).StageKey);
        Assert.Equal(daemonRun.CompletedAtUtc, ((ScheduleState)(object)state).LastActualRun);
    }

    [Fact]
    public async Task RestorationKeepsIdentityStartTimeVisibilityAndCancellationOwnershipAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        var daemonRun = await fixture.StartAsync("20", id);
        var prior = new ScheduledPrefillServiceRunState(PrefillPlatform.Steam, id, "Nightly",
            new RunNotice(NotificationMode.Silent, RunTrigger.Scheduled));
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
        Assert.Equal(NotificationMode.Silent, display.Notice.Mode);
        Assert.True(display.Notice.RestoredOrigin);
        Assert.Null(display.Notice.Actor);
        Assert.Same(display.Notice, operation.Notice);
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
        var run = await fixture.StartAsync("20", ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam));
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

    [Theory]
    [InlineData(NotificationMode.All, RunTrigger.Scheduled, "visible")]
    [InlineData(NotificationMode.Manual, RunTrigger.Manual, "visible")]
    [InlineData(NotificationMode.Manual, RunTrigger.Scheduled, "silent")]
    [InlineData(NotificationMode.Manual, RunTrigger.RunAll, "silent")]
    [InlineData(NotificationMode.Silent, RunTrigger.Manual, "silent")]
    [InlineData(NotificationMode.Hidden, RunTrigger.Manual, "hidden")]
    public async Task StartSavesTheRunNoticeAsTheDaemonModeStringAsync(
        NotificationMode mode, RunTrigger trigger, string expected)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var scheduler = CreateScheduler(services);
        var serviceRun = MakeRun("20");
        serviceRun = serviceRun with
        {
            State = new ScheduledPrefillServiceRunState(PrefillPlatform.Steam, serviceRun.State.ScheduleId, "Nightly",
                new RunNotice(mode, trigger))
        };
        var task = Start(scheduler, fixture, serviceRun);
        await fixture.RefreshAsync();
        var run = fixture.Daemon.GetRun(fixture.Session.Id, serviceRun.OperationId)!;
        Assert.Equal(expected, run.NotificationMode);
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        Assert.Equal(ScheduledPrefillServiceRunResult.Ran, await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Theory]
    [InlineData("visible", NotificationMode.All, RunTrigger.Manual, RunVisibility.Card)]
    [InlineData("silent", NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    [InlineData("hidden", NotificationMode.Hidden, RunTrigger.Scheduled, RunVisibility.Hidden)]
    [InlineData(null, NotificationMode.Silent, RunTrigger.Scheduled, RunVisibility.Background)]
    public async Task RestoreTurnsEachSavedModeBackIntoTheSameRowAsync(
        string? saved, NotificationMode mode, RunTrigger trigger, RunVisibility visibility)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var id = ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam);
        var response = await fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["20"], scheduleId: id,
            scheduleName: "Named schedule", notificationMode: saved);
        await fixture.RefreshAsync();
        var daemonRun = Assert.IsType<DaemonRun>(fixture.Daemon.GetRun(fixture.Session.Id, response.RunId!.Value));
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var state = DispatchProxy.Create<IStateService, ScheduleState>();
        ((ScheduleState)(object)state).Config = config;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker).AddSingleton(Notifications()).BuildServiceProvider();
        using var scheduler = CreateScheduler(services, state);
        try
        {
            Restore(scheduler, config);
            var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(daemonRun.PrefillRunId));
            var display = Assert.IsType<ScheduledPrefillServiceRunState>(operation.Metadata);
            Assert.Equal(mode, display.Notice.Mode);
            Assert.Equal(trigger, display.Notice.Trigger);
            Assert.True(display.Notice.RestoredOrigin);
            Assert.Null(display.Notice.Actor);
            Assert.Same(display.Notice, operation.Notice);
            var row = Assert.Single(tracker.GetRuns().Runs, item => item.OperationId == daemonRun.PrefillRunId);
            Assert.Equal(visibility, row.Visibility);
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
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
            null, [scheduler, Notifications(), fixture.Session, serviceRun, fixture.Session.Id], null)!;
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
            .Invoke(scheduler, [notifications, run, stage, message, null, bytes, "session", percent,
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
            new ScheduledPrefillServiceRunState(platform, id, "Nightly", new RunNotice(NotificationMode.All, RunTrigger.Manual)),
            CancellationToken.None);
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

    // One platform's daemon over the fixture's session, with a Docker that answers and lists no container.
    private static ServiceProvider DaemonWithNoContainer(
        RunFixture fixture, PrefillPlatform platform, out PrefillDaemonServiceBase daemon)
    {
        daemon = CreateDaemon(fixture, platform);
        typeof(PrefillDaemonServiceBase).GetField("_containerGateway", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(daemon, new RecordingContainerGateway());
        var serviceType = platform == PrefillPlatform.Steam ? typeof(SteamDaemonService) : typeof(BattleNetDaemonService);
        return new ServiceCollection().AddSingleton(serviceType, daemon).BuildServiceProvider();
    }

    // A Stop or an image update takes the session out of the daemon before its teardown starts.
    private static void EndSession(PrefillDaemonServiceBase daemon, DaemonSession session)
        => ((ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(daemon)!)
            .TryRemove(session.Id, out _);

    // An image update ends the old session and registers the new container's session.
    private static void ReplaceSession(PrefillDaemonServiceBase daemon, DaemonSession session)
    {
        EndSession(daemon, session);
        var replacement = new DaemonSession
        {
            Id = Guid.NewGuid().ToString(),
            UserId = session.UserId,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        ((ConcurrentDictionary<string, DaemonSession>)typeof(PrefillDaemonServiceBase)
            .GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(daemon)!)[replacement.Id] = replacement;
    }

    private static void AssertBusyEnding(ScheduledPrefillServiceRunResult result, ScheduledPrefillServiceRun run)
    {
        Assert.Equal(ScheduledPrefillServiceRunResult.Skipped, result);
        Assert.Equal("signalr.scheduledPrefill.skippedContainerBusy", run.State.Snapshot.StageKey);
    }

    private static void AssertNoContainerEnding(PrefillPlatform platform, ScheduledPrefillServiceRunResult result,
        ScheduledPrefillServiceRun run)
    {
        Assert.Equal(platform == PrefillPlatform.Steam
            ? ScheduledPrefillServiceRunResult.NeedsLogin : ScheduledPrefillServiceRunResult.Unavailable, result);
        Assert.Equal(platform == PrefillPlatform.Steam
            ? "signalr.scheduledPrefill.needsPersistentContainer" : "signalr.scheduledPrefill.skippedNoContainer",
            run.State.Snapshot.StageKey);
    }

    private static Task<ScheduledPrefillServiceRunResult> Start(ScheduledPrefillService scheduler, RunFixture fixture,
        ScheduledPrefillServiceRun run)
        => (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("StartRunAsync", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, run, Notifications(), ScheduledPrefillConfigFactory.CreateDefault()])!;

    private static Task<ScheduledPrefillServiceRunResult> Watch(ScheduledPrefillService scheduler, RunFixture fixture,
        DaemonRun run, ScheduledPrefillServiceRun serviceRun)
        => (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("WatchRunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [fixture.Daemon, fixture.Session, run, serviceRun, Notifications(), ScheduledPrefillConfigFactory.CreateDefault()])!;

    // Ends a run the way the daemons' shared run library does (RunProgress.CompleteAsync): every game keeps its
    // own result, a game that never ran reads "skipped" with the run's reason, the counters are counted from the
    // games, and a run in which any game failed ends "failed". The token "nothing-to-download" is a game the
    // daemon skipped on its own (Steam: no depots for the chosen OS or language), which keeps a null reason.
    private static void FinishFailedRun(RunFixture fixture, DaemonRun run, string? reason, string[] results)
    {
        var page = fixture.Client.Pages[run.PrefillRunId];
        var sequence = page.Operation.Sequence + 1;
        var items = run.Options.AppIds!.Select((appId, index) => new DaemonRunItem
        {
            AppId = appId,
            State = "completed",
            Result = results[index] == "nothing-to-download" ? "skipped" : results[index],
            Reason = results[index] == "nothing-to-download" ? null
                : results[index] != "skipped" ? null : reason is null ? "notAttempted" : reason,
            BytesTransferred = results[index] == "success" ? 100 : 0,
            TotalBytes = 100,
            Sequence = sequence
        }).ToArray();
        fixture.Client.Pages[run.PrefillRunId] = page with
        {
            Operation = page.Operation with
            {
                State = "failed",
                Reason = reason,
                Sequence = sequence,
                UpdatedAt = DateTimeOffset.UtcNow,
                BytesTransferred = items.Sum(item => item.BytesTransferred),
                CompletedApps = results.Count(result => result == "success"),
                CachedApps = results.Count(result => result == "already_cached"),
                FailedApps = results.Count(result => result == "failed"),
                SkippedApps = results.Count(result => result is "skipped" or "nothing-to-download")
            },
            Items = items
        };
    }

    // The fixture's protocol-2 status for a signed-out Steam container. The code is "auth-lost" after a
    // dropped connection and null after a log-off or a person's Log out.
    private static DaemonStatus SignedOutStatus(RunFixture fixture, string? errorCode)
    {
        var status = RunClient.Capabilities(fixture.Client.InstanceId);
        status.Status = "not-logged-in";
        status.ErrorCode = errorCode;
        return status;
    }

    // Starts one scheduled service run the way the scheduler does, with the operation tracked under
    // the same cancellation source the run carries.
    private static Task<ScheduledPrefillServiceRunResult> StartGatedRun(
        ScheduledPrefillService scheduler, IServiceProvider services, ScheduledPrefillServiceRun run, CancellationTokenSource source)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        Assert.True(tracker.TryRestoreOperation(run.OperationId, OperationType.ScheduledPrefill, run.State.Name, source, run.State));
        return (Task<ScheduledPrefillServiceRunResult>)typeof(ScheduledPrefillService)
            .GetMethod("RunAndStampServiceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [run, tracker, services, Notifications(), ScheduledPrefillConfigFactory.CreateDefault(),
                CancellationToken.None, true, null])!;
    }

    private static void Restore(ScheduledPrefillService scheduler, ScheduledPrefillConfigDto config)
        => typeof(ScheduledPrefillService).GetMethod("RestoreRuns", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(scheduler, [config]);

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }
}

// A daemon that receives the prefill command while a Stop ends its session, then accepts the run.
internal class StopOnPrefillClient : RunClient
{
    public Action OnPrefill { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDaemonClient.PrefillAsync)) OnPrefill();
        return base.Invoke(targetMethod, args);
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
    public ConcurrentQueue<Guid> Registered { get; } = new();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var result = targetMethod!.Invoke(Tracker, args);
        if (targetMethod.Name == nameof(IUnifiedOperationTracker.TryRestoreOperation) && result is false)
            Rejected = (CancellationTokenSource)args![3]!;
        if (targetMethod.Name == nameof(IUnifiedOperationTracker.RegisterOperation))
            Registered.Enqueue((Guid)result!);
        return result;
    }
}
