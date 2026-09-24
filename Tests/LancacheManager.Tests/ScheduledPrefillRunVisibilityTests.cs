using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers how a scheduled-prefill run that spans several due platforms with different
/// NotificationModes decides what shows: each platform run is admitted with its own notice (its
/// schedule's mode and the tick's trigger), the run-level container that groups them never becomes a
/// row, and no lifecycle event carries a visibility of its own. The run reaches its per-service
/// events without any daemon registered: an unresolved daemon emits a "skipped" progress event and
/// returns, which is enough to observe every registration and every event.
/// </summary>
public class ScheduledPrefillRunVisibilityTests
{
    [Theory]
    [InlineData(0, true, "signalr.scheduledPrefill.completeAllCached")]
    [InlineData(0, false, "signalr.scheduledPrefill.completeNoBytes")]
    [InlineData(1024, false, "signalr.scheduledPrefill.completeWithBytes")]
    public void CompletionMessage_DistinguishesCachedGamesFromTransferredBytes(long bytes, bool allCached, string expectedKey)
    {
        var method = typeof(ScheduledPrefillService).GetMethod("BuildCompletionMessage", BindingFlags.Static | BindingFlags.NonPublic)!;
        var result = ((string Message, string StageKey, Dictionary<string, object?>? Context))method.Invoke(null, [bytes, allCached])!;
        Assert.Equal(expectedKey, result.StageKey);
        Assert.Equal(bytes > 0, result.Context?.ContainsKey("bytes") == true);
    }

    [Theory]
    [InlineData(NotificationMode.All, NotificationMode.Silent, RunTrigger.Scheduled)]
    [InlineData(NotificationMode.Silent, NotificationMode.All, RunTrigger.Manual)]
    [InlineData(NotificationMode.Manual, NotificationMode.Manual, RunTrigger.Scheduled)]
    [InlineData(NotificationMode.Manual, NotificationMode.Silent, RunTrigger.Manual)]
    [InlineData(NotificationMode.Manual, NotificationMode.Hidden, RunTrigger.RunAll)]
    [InlineData(NotificationMode.Hidden, NotificationMode.All, RunTrigger.Startup)]
    public async Task ExecuteWorkAsync_AdmitsEachPlatformRunWithItsOwnNotice(
        NotificationMode steamMode,
        NotificationMode epicMode,
        RunTrigger trigger)
    {
        var recorder = (RecordingNotificationsProxy)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);

        var tracking = await RunTickAsync(BuildMixedConfig(steamMode, epicMode), lastRun: null, trigger, recorder, tracker);

        var operations = tracking.Registered.Select(id => tracker.GetOperation(id)!).ToList();
        var rows = tracker.GetRuns().Runs;

        // The run-level container groups the tick's platforms; it has no notice and never becomes a row.
        var container = Assert.Single(operations, operation => operation.Metadata is ScheduledPrefillOperationMetadata);
        Assert.Null(container.Notice);
        Assert.DoesNotContain(rows, row => row.OperationId == container.Id);

        foreach (var (platform, mode) in new[] { (PrefillPlatform.Steam, steamMode), (PrefillPlatform.Epic, epicMode) })
        {
            var operation = Assert.Single(operations, candidate =>
                candidate.Metadata is ScheduledPrefillServiceRunState runState && runState.ServiceId == platform);
            var state = (ScheduledPrefillServiceRunState)operation.Metadata!;
            Assert.Same(state.Notice, operation.Notice);
            Assert.Equal(mode, state.Notice.Mode);
            Assert.Equal(trigger, state.Notice.Trigger);
            Assert.Contains(rows, row => row.OperationId == operation.Id);
        }

        // A run emits a Started and a Completed per due service beside its own run-level pair, so each
        // service's card opens and closes on its own timing. None of them carries a visibility: the
        // row alone decides how the run shows.
        Assert.NotEmpty(recorder.Events);
        Assert.All(recorder.Events, captured => Assert.False(captured.CarriesVisibility));
        var started = recorder.Events.Where(captured => captured.EventName == SignalREvents.ScheduledPrefillStarted).ToList();
        var completed = recorder.Events.Where(captured => captured.EventName == SignalREvents.ScheduledPrefillCompleted).ToList();
        Assert.Equal(3, started.Count);
        Assert.Equal(started.Count, completed.Count);
    }

    /// <summary>
    /// Run All starts every enabled prefill schedule, including one that is not due yet, exactly as
    /// Run Now does; an automatic tick leaves a schedule that ran moments ago alone.
    /// </summary>
    [Theory]
    [InlineData(RunTrigger.Scheduled, 0)]
    [InlineData(RunTrigger.Startup, 0)]
    [InlineData(RunTrigger.Manual, 2)]
    [InlineData(RunTrigger.RunAll, 2)]
    public async Task ExecuteWorkAsync_RunAllSkipsTheDueCheckLikeManual(RunTrigger trigger, int expectedPlatformRuns)
    {
        var recorder = (RecordingNotificationsProxy)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);

        var tracking = await RunTickAsync(BuildMixedConfig(NotificationMode.All, NotificationMode.All),
            lastRun: DateTime.UtcNow, trigger, recorder, tracker);

        var platformRuns = tracking.Registered
            .Select(id => tracker.GetOperation(id)!.Metadata)
            .OfType<ScheduledPrefillServiceRunState>()
            .ToList();
        Assert.Equal(expectedPlatformRuns, platformRuns.Count);
        Assert.All(platformRuns, state => Assert.Equal(trigger, state.Notice.Trigger));
    }

    /// <summary>
    /// A Run Now on one schedule is admitted with that schedule's mode and a manual trigger.
    /// </summary>
    [Theory]
    [InlineData(NotificationMode.Manual)]
    [InlineData(NotificationMode.Hidden)]
    public async Task TriggerServiceRun_AdmitsTheRunWithAManualNotice(NotificationMode steamMode)
    {
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var scopeServices = new ServiceCollection();
        scopeServices.AddSingleton((ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>());
        scopeServices.AddSingleton<IUnifiedOperationTracker>(tracker);
        using var root = scopeServices.BuildServiceProvider();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, MixedModeStateServiceProxy>();
        ((MixedModeStateServiceProxy)stateService).Config = BuildMixedConfig(steamMode, NotificationMode.All);
        using var service = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            root.GetRequiredService<IServiceScopeFactory>(),
            stateService);

        var operationId = service.TriggerServiceRun(
            PrefillPlatform.Steam, ScheduledPrefillConfigFactory.GetDefaultScheduleId(PrefillPlatform.Steam));
        await service.StopAsync(CancellationToken.None);

        var operation = Assert.IsType<OperationInfo>(tracker.GetOperation(operationId!.Value));
        var state = Assert.IsType<ScheduledPrefillServiceRunState>(operation.Metadata);
        Assert.Same(state.Notice, operation.Notice);
        Assert.Equal(steamMode, state.Notice.Mode);
        Assert.Equal(RunTrigger.Manual, state.Notice.Trigger);
    }

    /// <summary>
    /// Runs one scheduler tick under <paramref name="trigger"/> against the real tracker and returns
    /// the proxy that recorded every operation the tick registered.
    /// </summary>
    private static async Task<ScheduleTracker> RunTickAsync(
        ScheduledPrefillConfigDto config,
        DateTime? lastRun,
        RunTrigger trigger,
        RecordingNotificationsProxy recorder,
        UnifiedOperationTracker tracker)
    {
        var trackerProxy = DispatchProxy.Create<IUnifiedOperationTracker, ScheduleTracker>();
        var tracking = (ScheduleTracker)(object)trackerProxy;
        tracking.Tracker = tracker;

        var scopeServices = new ServiceCollection();
        scopeServices.AddSingleton((ISignalRNotificationService)recorder);
        scopeServices.AddSingleton(trackerProxy);
        using var scopeProvider = scopeServices.BuildServiceProvider();

        var stateService = (IStateService)DispatchProxy.Create<IStateService, MixedModeStateServiceProxy>();
        ((MixedModeStateServiceProxy)stateService).Config = config;
        ((MixedModeStateServiceProxy)stateService).LastRun = lastRun;

        using var service = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            stateService);
        typeof(ScheduledPrefillService)
            .GetProperty("CurrentRunTrigger", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, trigger);

        var executeWork = typeof(ScheduledPrefillService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)executeWork.Invoke(service, new object[] { CancellationToken.None })!;
        return tracking;
    }

    /// <summary>
    /// A service that did nothing closes its card as skipped, not failed: the terminal carries the
    /// same wire word the tracker uses, and reports success because a missing prerequisite is not an
    /// error. Every other outcome leaves the status off the wire.
    /// </summary>
    [Theory]
    [InlineData(ScheduledPrefillServiceRunResult.Skipped, "skipped", true)]
    [InlineData(ScheduledPrefillServiceRunResult.NeedsLogin, "skipped", true)]
    [InlineData(ScheduledPrefillServiceRunResult.Ran, null, true)]
    [InlineData(ScheduledPrefillServiceRunResult.Failed, null, false)]
    [InlineData(ScheduledPrefillServiceRunResult.Cancelled, null, false)]
    public async Task CompleteServiceRunAsync_NamesASkipOnTheWire(
        ScheduledPrefillServiceRunResult result, string? expectedStatus, bool expectedSuccess)
    {
        var recorder = (RecordingNotificationsProxy)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var tracker = (IUnifiedOperationTracker)DispatchProxy.Create<IUnifiedOperationTracker, NoopTrackerProxy>();
        var serviceConfig = ScheduledPrefillConfigFactory.CreateDefault()
            .GetSchedulesInRunOrder()
            .Single(schedule => schedule.ServiceId == PrefillPlatform.Steam);
        var serviceRun = new ScheduledPrefillServiceRun(
            serviceConfig,
            Guid.NewGuid(),
            "op-1",
            "run-1",
            new ScheduledPrefillServiceRunState(
                serviceConfig.ServiceId,
                serviceConfig.ScheduleId,
                serviceConfig.ScheduleName,
                new RunNotice(NotificationMode.All, RunTrigger.Manual)),
            CancellationToken.None);

        var complete = typeof(ScheduledPrefillService)
            .GetMethod("CompleteServiceRunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        // The trailing null is the thrown-failure message, which only the throwing path supplies.
        await (Task)complete.Invoke(null, new object?[] { serviceRun, tracker, (ISignalRNotificationService)recorder, result, null })!;

        var completed = Assert.Single(recorder.Events);
        Assert.Equal(SignalREvents.ScheduledPrefillCompleted, completed.EventName);
        Assert.Equal(expectedStatus, completed.Status);
        Assert.Equal(expectedSuccess, completed.Success);
    }

    private static ScheduledPrefillConfigDto BuildMixedConfig(NotificationMode steamMode, NotificationMode epicMode)
    {
        var template = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = template.Version,
            MaxServiceRuntime = template.MaxServiceRuntime,
            StallTimeout = template.StallTimeout,
            PersistenceMode = template.PersistenceMode,
            Steam = Reconfigure(template.Steam, enabled: true, steamMode),
            Epic = Reconfigure(template.Epic, enabled: true, epicMode),
            Xbox = Reconfigure(template.Xbox, enabled: false, NotificationMode.All),
            BattleNet = Reconfigure(template.BattleNet, enabled: false, NotificationMode.All),
            Riot = Reconfigure(template.Riot, enabled: false, NotificationMode.All)
        };
    }

    private static ScheduledPrefillServiceConfigDto Reconfigure(
        ScheduledPrefillServiceConfigDto template, bool enabled, NotificationMode mode)
        => new()
        {
            ServiceId = template.ServiceId,
            Schedules = template.Schedules.Select(schedule => new ScheduledPrefillSchedule
            {
                Id = schedule.Id,
                Name = schedule.Name,
                Enabled = enabled,
                IntervalHours = schedule.IntervalHours,
                CustomSchedule = schedule.CustomSchedule,
                Preset = schedule.Preset,
                TopCount = schedule.TopCount,
                SelectedAppIds = schedule.SelectedAppIds,
                OperatingSystems = schedule.OperatingSystems,
                Force = schedule.Force,
                MaxConcurrency = schedule.MaxConcurrency,
                NotificationMode = mode,
                NotificationDisplayMode = schedule.NotificationDisplayMode
            }).ToList(),
            Enabled = enabled,
            NotificationMode = mode,
            IntervalHours = ScheduledPrefillConfigFactory.DefaultIntervalHours,
            Preset = template.Preset,
            TopCount = template.TopCount,
            SelectedAppIds = template.SelectedAppIds,
            OperatingSystems = template.OperatingSystems,
            Force = template.Force,
            MaxConcurrency = template.MaxConcurrency,
            PersistenceMode = template.PersistenceMode
        };

    private sealed record CapturedEvent(
        string EventName,
        bool CarriesVisibility,
        string? Status,
        bool? Success);

    /// <summary>
    /// Records the event name, whether the payload has a visibility field (any name ending in
    /// "Notification"), and the wire <c>status</c> and <c>success</c> (null when the
    /// payload has none) of every <c>NotifyAllAsync</c> payload the scheduled-prefill orchestrator
    /// emits. Every other member returns its type default.
    /// Not sealed: DispatchProxy.Create derives a runtime subclass.
    /// </summary>
    private class RecordingNotificationsProxy : DispatchProxy
    {
        private readonly object _sync = new();
        private readonly List<CapturedEvent> _events = new();

        public IReadOnlyList<CapturedEvent> Events
        {
            get { lock (_sync) return _events.ToArray(); }
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ISignalRNotificationService.NotifyAllAsync)
                && args is { Length: >= 2 }
                && args[0] is string eventName
                && args[1] is { } payload)
            {
                var carriesVisibility = payload.GetType().GetProperties()
                    .Any(property => property.Name.EndsWith("Notification", StringComparison.Ordinal));
                var status = payload.GetType().GetProperty("status")?.GetValue(payload) as string;
                var success = payload.GetType().GetProperty("success")?.GetValue(payload) as bool?;
                lock (_sync)
                {
                    _events.Add(new CapturedEvent(eventName, carriesVisibility, status, success));
                }
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// Minimal tracker stub: <c>RegisterOperation</c> hands back a fresh operation id and never
    /// cancels the adopted CTS; every other member no-ops. Not sealed for DispatchProxy.Create.
    /// </summary>
    private class NoopTrackerProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IUnifiedOperationTracker.RegisterOperation))
            {
                return Guid.NewGuid();
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    /// <summary>
    /// IStateService stub whose <c>GetScheduledPrefillConfig</c> returns the mixed-mode config under
    /// test; the per-service last-run getter returns <see cref="LastRun"/> (null makes every enabled
    /// service due this tick) and every other member returns its type default.
    /// </summary>
    private class MixedModeStateServiceProxy : DispatchProxy
    {
        public ScheduledPrefillConfigDto? Config { get; set; }
        public DateTime? LastRun { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config ?? ScheduledPrefillConfigFactory.CreateDefault();
            }

            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillServiceLastRun))
            {
                return LastRun;
            }

            return DefaultReturnValue(targetMethod);
        }
    }

    private static object? DefaultReturnValue(MethodInfo? targetMethod)
    {
        var returnType = targetMethod?.ReturnType;

        if (returnType is null || returnType == typeof(void))
        {
            return null;
        }

        if (returnType == typeof(Task))
        {
            return Task.CompletedTask;
        }

        if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
        {
            return Activator.CreateInstance(returnType);
        }

        return null;
    }
}
