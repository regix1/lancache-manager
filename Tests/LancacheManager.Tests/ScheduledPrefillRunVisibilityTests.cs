using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers run-level notification visibility for a scheduled-prefill run that spans several due
/// platforms with different per-platform NotificationModes. The visibility flag is computed ONCE for
/// the whole run (an OR across the due platforms' modes for the current trigger) and stamped verbatim
/// into the Started event, every per-platform progress event, and the terminal event - so a silent
/// child can never remove a visible sibling's card, and the terminal never disagrees with the Started
/// visibility (the stuck-visible-card defect). The run reaches its per-service progress events without
/// any daemon registered: an unresolved daemon emits a "skipped" progress event and returns, which is
/// enough to observe the stamped flag on every lifecycle event.
/// </summary>
public class ScheduledPrefillRunVisibilityTests
{
    public static IEnumerable<object[]> VisibilityCases()
    {
        // One visible + one silent -> the run's card is visible (OR is true), regardless of order.
        yield return new object[] { NotificationMode.All, NotificationMode.Silent, true };
        yield return new object[] { NotificationMode.Silent, NotificationMode.All, true };
        // Every due platform silent -> the whole run is silent.
        yield return new object[] { NotificationMode.Silent, NotificationMode.Silent, false };
    }

    [Theory]
    [MemberData(nameof(VisibilityCases))]
    public async Task ExecuteWorkAsync_StampsRunLevelVisibilityOnEveryLifecycleEvent(
        NotificationMode steamMode, NotificationMode epicMode, bool expectedVisible)
    {
        var recorder = (RecordingNotificationsProxy)DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationsProxy>();
        var tracker = (NoopTrackerProxy)DispatchProxy.Create<IUnifiedOperationTracker, NoopTrackerProxy>();

        var scopeServices = new ServiceCollection();
        scopeServices.AddSingleton((ISignalRNotificationService)recorder);
        scopeServices.AddSingleton((IUnifiedOperationTracker)tracker);
        using var scopeProvider = scopeServices.BuildServiceProvider();

        var stateService = (IStateService)DispatchProxy.Create<IStateService, MixedModeStateServiceProxy>();
        ((MixedModeStateServiceProxy)stateService).Config = BuildMixedConfig(steamMode, epicMode);

        var service = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            stateService);

        var executeWork = typeof(ScheduledPrefillService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)executeWork.Invoke(service, new object[] { CancellationToken.None })!;
        service.Dispose();

        // Every lifecycle event (Started, per-platform progress, Completed) carries the same
        // run-level flag - no per-platform churn.
        Assert.NotEmpty(recorder.Events);
        Assert.All(recorder.Events, e => Assert.Equal(expectedVisible, e.ShowNotification));

        // A run emits a Started and a Completed per due service beside its own run-level pair, so each
        // service's card opens and closes on its own timing. Every one of them carries the run-level
        // flag, and the counts match: a card that opened and never closed is the stuck-card defect.
        var started = recorder.Events.Where(e => e.EventName == SignalREvents.ScheduledPrefillStarted).ToList();
        var completed = recorder.Events.Where(e => e.EventName == SignalREvents.ScheduledPrefillCompleted).ToList();
        Assert.Equal(3, started.Count);
        Assert.Equal(started.Count, completed.Count);
        Assert.All(started, e => Assert.Equal(expectedVisible, e.ShowNotification));
        Assert.All(completed, e => Assert.Equal(expectedVisible, e.ShowNotification));
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
        var serviceRun = new ScheduledPrefillServiceRun(
            ScheduledPrefillConfigFactory.CreateDefault().Steam,
            Guid.NewGuid(),
            "op-1",
            "run-1",
            new ScheduledPrefillServiceRunState(PrefillPlatform.Steam),
            CancellationToken.None);

        var complete = typeof(ScheduledPrefillService)
            .GetMethod("CompleteServiceRunAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        // The trailing null is the thrown-failure message, which only the throwing path supplies.
        await (Task)complete.Invoke(null, new object?[] { serviceRun, tracker, (ISignalRNotificationService)recorder, result, true, null })!;

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

    private sealed record CapturedEvent(string EventName, bool ShowNotification, string? Status, bool? Success);

    /// <summary>
    /// Records the event name, the <c>showNotification</c> field, and the wire <c>status</c> and
    /// <c>success</c> (null when the payload has none) of every <c>NotifyAllAsync</c> payload the
    /// scheduled-prefill orchestrator emits. Every other member returns its type default.
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
                && args[1] is { } payload
                && payload.GetType().GetProperty("showNotification")?.GetValue(payload) is bool showNotification)
            {
                var status = payload.GetType().GetProperty("status")?.GetValue(payload) as string;
                var success = payload.GetType().GetProperty("success")?.GetValue(payload) as bool?;
                lock (_sync)
                {
                    _events.Add(new CapturedEvent(eventName, showNotification, status, success));
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
    /// test; per-service last-run getters return null (so every enabled service is due this tick) and
    /// every other member returns its type default.
    /// </summary>
    private class MixedModeStateServiceProxy : DispatchProxy
    {
        public ScheduledPrefillConfigDto? Config { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config ?? ScheduledPrefillConfigFactory.CreateDefault();
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
