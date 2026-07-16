using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
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

        // The terminal visibility must equal the Started visibility (the stuck-card invariant).
        var started = recorder.Events.Single(e => e.EventName == SignalREvents.ScheduledPrefillStarted);
        var completed = recorder.Events.Single(e => e.EventName == SignalREvents.ScheduledPrefillCompleted);
        Assert.Equal(started.ShowNotification, completed.ShowNotification);
        Assert.Equal(expectedVisible, started.ShowNotification);
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

    private sealed record CapturedEvent(string EventName, bool ShowNotification);

    /// <summary>
    /// Records the event name and <c>showNotification</c> field of every <c>NotifyAllAsync</c> payload
    /// the scheduled-prefill orchestrator emits. Every other member returns its type default.
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
                lock (_sync)
                {
                    _events.Add(new CapturedEvent(eventName, showNotification));
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
