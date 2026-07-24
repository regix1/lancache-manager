using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public class ServiceScheduleRegistry : IServiceScheduleRegistry
{
    private static readonly HashSet<string> _allowedServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "cacheReconciliation",
        "cacheSizeScan",
        "gameDetection",
        "gameImageFetch",
        "cacheSnapshot",
        "operationHistoryCleanup",
        "logRotation",
        "dashboardCacheWarmer"
    };

    // Maps a schedule service key to the operation type the run-status endpoint queries on the tracker.
    // Covers both the pipeline-less maintenance services (each owns its own operation type) and the
    // existing pipelines (eviction/cache-size/detection/depot/epic/xbox/prefill) so a single generic
    // recovery route can rehydrate any card's in-progress bar after a refresh.
    private static readonly Dictionary<string, OperationType> _runStatusOperationTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["logRotation"] = OperationType.LogRotation,
        ["gameImageFetch"] = OperationType.GameImageFetch,
        ["cacheSnapshot"] = OperationType.CacheSnapshot,
        ["operationHistoryCleanup"] = OperationType.OperationHistoryCleanup,
        ["performanceOptimization"] = OperationType.PerformanceOptimization,
        ["dashboardCacheWarmer"] = OperationType.DashboardCacheWarmer,
        ["cacheReconciliation"] = OperationType.EvictionScan,
        ["cacheSizeScan"] = OperationType.CacheSizeScan,
        ["gameDetection"] = OperationType.GameDetection,
        ["depotMapping"] = OperationType.DepotMapping,
        ["epicMapping"] = OperationType.EpicMapping,
        ["xboxMapping"] = OperationType.XboxMapping,
        ["scheduledPrefill"] = OperationType.ScheduledPrefill,
    };

    private readonly Dictionary<string, ScheduledBackgroundService> _scheduledServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigurableScheduledService> _configurableServices = new(StringComparer.OrdinalIgnoreCase);

    // ConfigurableScheduledService fires its static ServiceExecutionStateChanged event using the
    // protected ServiceName (see ConfigurableScheduledService.cs's ExecuteAsync loop), NOT the
    // ScheduleServiceKey that _configurableServices above is keyed by. Track each tracked configurable
    // service's ServiceName here too so OnServiceExecutionStateChangedAsync's tracked-service guard
    // recognizes the event when it arrives.
    private readonly HashSet<string> _configurableServiceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker? _tracker;

    // Optional (like _tracker) so unit tests that construct the registry directly keep compiling; at
    // runtime DI always supplies it. Every schedule broadcast mirrors the running set into the unified
    // activity registry so the Schedules status dots read the one ActivityUpdated event.
    private readonly IActivityRegistry? _activityRegistry;

    // Serializes every SchedulesUpdated broadcast (see BroadcastSchedulesAsync). Run start/end events
    // fire from many independent service-loop threads; without serialization two full-list snapshots
    // can be sent concurrently and delivered out of order, so an older snapshot arriving last would
    // leave a finished service stuck "running" (green dot) indefinitely. One in-flight broadcast at a
    // time, with the snapshot taken inside the lock, guarantees the last delivered payload is current.
    private readonly SemaphoreSlim _broadcastLock = new(1, 1);

    // The tracker is optional so existing unit tests that construct the registry without one keep
    // compiling; at runtime the DI container always supplies the registered singleton. GetRunStatus
    // reports "not running" when it is absent.
    public ServiceScheduleRegistry(IEnumerable<IHostedService> hostedServices, IStateService stateService, ISignalRNotificationService notifications, IUnifiedOperationTracker? tracker = null, IActivityRegistry? activityRegistry = null)
    {
        _stateService = stateService;
        _notifications = notifications;
        _tracker = tracker;
        _activityRegistry = activityRegistry;
        foreach (var service in hostedServices)
        {
            if (service is ScheduledBackgroundService scheduledService)
            {
                // Only include explicitly allowed user-configurable services.
                // Infrastructure services are excluded via the allowlist.
                if (_allowedServiceKeys.Contains(scheduledService.ServiceKey))
                {
                    _scheduledServices[scheduledService.ServiceKey] = scheduledService;
                }
            }
            else if (service is ConfigurableScheduledService configurableService)
            {
                var key = GetServiceKey(configurableService);
                _configurableServices[key] = configurableService;

                // Also index by the protected ServiceName used by the ServiceExecutionStateChanged event
                // (see _configurableServiceNames above) so the state-change guard can recognize it.
                var serviceName = (string?)GetPropertyValue(configurableService.GetType(), configurableService, "ServiceName", typeof(string));
                if (!string.IsNullOrEmpty(serviceName))
                {
                    _configurableServiceNames.Add(serviceName);
                }
            }
        }

        ScheduledBackgroundService.ServiceExecutionStateChanged += OnServiceExecutionStateChangedAsync;
        ConfigurableScheduledService.ServiceExecutionStateChanged += OnServiceExecutionStateChangedAsync;
    }

    private async void OnServiceExecutionStateChangedAsync(string serviceKey)
    {
        // Mirror the same allowlist gate applied when populating _scheduledServices/_configurableServices:
        // a service this registry doesn't track (excluded from _allowedServiceKeys, or an infrastructure
        // service like PersistentSessionExpiryService) must not trigger a Schedules broadcast either.
        // Without this check, ANY ScheduledBackgroundService/ConfigurableScheduledService subclass firing
        // this static event - tracked or not - would still spam every connected client on every tick.
        //
        // ConfigurableScheduledService fires this event keyed by ServiceName, not ScheduleServiceKey, so
        // _configurableServiceNames (populated alongside _configurableServices in the constructor) must
        // be checked too - otherwise every tracked configurable service's broadcast would be dropped here.
        if (!_scheduledServices.ContainsKey(serviceKey) &&
            !_configurableServices.ContainsKey(serviceKey) &&
            !_configurableServiceNames.Contains(serviceKey))
        {
            return;
        }

        try
        {
            await BroadcastSchedulesAsync();
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
        }
    }

    public void NotifySchedulesChanged()
    {
        // Re-use the same fire-and-forget SignalR broadcast path as work-state ticks so
        // conditional-visibility changes (e.g. GC Aggressiveness flip) propagate to the
        // Schedules UI without a page reload. Any error is swallowed - matches existing pattern.
        _ = NotifySchedulesAsync();
    }

    private async Task NotifySchedulesAsync()
    {
        try
        {
            await BroadcastSchedulesAsync();
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
        }
    }

    // The single serialized broadcast path. WaitAsync ensures only one SchedulesUpdated send is in
    // flight at a time; GetAll() is snapshotted inside the lock so the payload is the freshest committed
    // state at the moment it is sent, and sends are delivered in order. Public so the controllers route
    // their config/reset broadcasts through the same lock (see IServiceScheduleRegistry).
    public async Task BroadcastSchedulesAsync()
    {
        await _broadcastLock.WaitAsync();
        try
        {
            var all = GetAll();
            await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, all);

            // Mirror the running set into the unified activity registry so the Schedules status dots read
            // the one ActivityUpdated event. ReplaceAsync sets exactly the running services active and
            // clears the rest, and only broadcasts on an actual change.
            if (_activityRegistry is not null)
            {
                var running = all
                    .Where(s => s.IsRunning)
                    .ToDictionary(s => s.Key, _ => 1, StringComparer.Ordinal);
                await _activityRegistry.ReplaceAsync(ActivityDomains.Schedule, ActivityAspects.Running, running);
            }
        }
        finally
        {
            _broadcastLock.Release();
        }
    }

    public IReadOnlyList<ServiceScheduleInfo> GetAll()
    {
        var results = new List<ServiceScheduleInfo>();

        foreach (var service in _scheduledServices.Values)
        {
            if (service is IConditionallyVisibleSchedule scheduledVisibility && !scheduledVisibility.IsScheduleVisible())
            {
                continue;
            }

            results.Add(MapScheduledService(service));
        }

        foreach (var service in _configurableServices.Values)
        {
            if (service is IConditionallyVisibleSchedule configurableVisibility && !configurableVisibility.IsScheduleVisible())
            {
                continue;
            }

            results.Add(MapConfigurableService(service));
        }

        return results;
    }

    public ServiceScheduleInfo? Get(string serviceKey)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            if (scheduled is IConditionallyVisibleSchedule scheduledVisibility && !scheduledVisibility.IsScheduleVisible())
            {
                return null;
            }

            return MapScheduledService(scheduled);
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            if (configurable is IConditionallyVisibleSchedule configurableVisibility && !configurableVisibility.IsScheduleVisible())
            {
                return null;
            }

            return MapConfigurableService(configurable);
        }

        return null;
    }

    public void SetInterval(string serviceKey, double intervalHours)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetInterval(TimeSpan.FromHours(intervalHours));
            _stateService.SetServiceInterval(serviceKey, intervalHours);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            ApplyInterval(configurable, TimeSpan.FromHours(intervalHours));
            _stateService.SetServiceInterval(serviceKey, intervalHours);
            return;
        }
    }

    public void SetRunOnStartup(string serviceKey, bool runOnStartup)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetRunOnStartup(runOnStartup);
            _stateService.SetServiceRunOnStartup(serviceKey, runOnStartup);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.SetRunOnStartup(runOnStartup);
            _stateService.SetServiceRunOnStartup(serviceKey, runOnStartup);
        }
    }

    public void SetNotificationMode(string serviceKey, NotificationMode mode)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.SetNotificationMode(mode);
            _stateService.SetServiceNotificationMode(serviceKey, mode);
            return;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.SetNotificationMode(mode);
            _stateService.SetServiceNotificationMode(serviceKey, mode);
        }
    }

    public void SetNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode)
    {
        if (!_scheduledServices.ContainsKey(serviceKey) && !_configurableServices.ContainsKey(serviceKey))
        {
            return;
        }

        // Unlike SetNotificationMode, no live service instance reads this value (MapScheduledService/
        // MapConfigurableService resolve it straight from state at read time), so persistence here is
        // the entire write path.
        _stateService.SetServiceNotificationDisplayMode(serviceKey, mode);
    }

    public void ResetToDefaults()
    {
        foreach (var (key, service) in _scheduledServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            service.SetNotificationMode(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
            _stateService.ClearServiceNotificationMode(key);
            _stateService.ClearServiceNotificationDisplayMode(key);
        }

        foreach (var (key, service) in _configurableServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            service.SetNotificationMode(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
            _stateService.ClearServiceNotificationMode(key);
            _stateService.ClearServiceNotificationDisplayMode(key);
        }

        // Scheduled prefill keeps its cadence per-service in the config DTO + durable last-run map
        // (not in ServiceIntervals). Clearing that map alone would make the next poll treat every enabled
        // service as never-run and instant-run it, so ClearScheduledPrefillServiceLastRun also re-anchors
        // the currently-enabled services to now — a reset returns to a "wait one full interval" baseline
        // rather than leaving stale next-run times or triggering an immediate run.
        _stateService.ClearScheduledPrefillServiceLastRun();

        // Scheduled prefill's notification mode lives per-platform in the config DTO, not in the
        // base-class override the loop above already reset (that reset is a no-op for this service -
        // ScheduledPrefillService never reads EffectiveNotificationMode). Reset each platform's mode
        // explicitly or a platform left on Manual/Silent survives "Reset to Defaults" unchanged.
        var prefillConfig = _stateService.GetScheduledPrefillConfig();
        _stateService.SetScheduledPrefillConfig(ScheduledPrefillConfigFactory.ResetNotificationModes(prefillConfig));
    }

    public Task TriggerRunAsync(string serviceKey)
    {
        if (_scheduledServices.TryGetValue(serviceKey, out var scheduled))
        {
            scheduled.TriggerImmediateRun();
            return Task.CompletedTask;
        }

        if (_configurableServices.TryGetValue(serviceKey, out var configurable))
        {
            configurable.TriggerImmediateRun();
        }

        return Task.CompletedTask;
    }

    public ScheduleRunStatus? GetRunStatus(string serviceKey)
    {
        if (!_runStatusOperationTypes.TryGetValue(serviceKey, out var operationType))
        {
            // Unknown key: the caller (controller) turns this into a 404.
            return null;
        }

        var active = _tracker?.GetActiveOperations(operationType).FirstOrDefault();
        if (active == null)
        {
            // An idle service is visible by default: recovery must stale-complete a persisted running
            // card on reconnect after a missed terminal, not delete it. Only an ACTIVE silent run
            // (ShowNotification=false below) is a legitimate skip.
            return new ScheduleRunStatus { IsRunning = false, ShowNotification = true };
        }

        // The run reporter carries the stage key as the operation's Message and persists the run's
        // immutable display flag into the operation metadata under "showNotification". A run without
        // that key (an operation registered by a non-reporter caller) is treated as visible so
        // recovery never drops a legitimately visible card.
        return new ScheduleRunStatus
        {
            IsRunning = true,
            OperationId = active.Id.ToString(),
            PercentComplete = active.PercentComplete,
            StageKey = string.IsNullOrEmpty(active.Message) ? null : active.Message,
            Context = ReadContext(active.Metadata),
            ShowNotification = ReadShowNotification(active.Metadata),
        };
    }

    private static bool ReadShowNotification(object? metadata)
    {
        var value = metadata switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("showNotification", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("showNotification", out var v) => v,
            _ => null,
        };

        return value is not bool show || show;
    }

    // The reporter mirrors each run's latest interpolation context into the operation metadata under
    // "context" so a mid-run page refresh can rehydrate the card with its {{processed}}/{{total}}
    // values instead of rendering a bare stage key.
    private static IReadOnlyDictionary<string, object?>? ReadContext(object? metadata)
    {
        var value = metadata switch
        {
            IReadOnlyDictionary<string, object?> readOnly when readOnly.TryGetValue("context", out var v) => v,
            IDictionary<string, object> mutable when mutable.TryGetValue("context", out var v) => v,
            _ => null,
        };

        return value as IReadOnlyDictionary<string, object?>;
    }

    public Task<int> TriggerAllAsync()
    {
        var count = 0;

        foreach (var (_, service) in _scheduledServices)
        {
            service.TriggerImmediateRun();
            count++;
        }

        foreach (var (_, service) in _configurableServices)
        {
            service.TriggerImmediateRun();
            count++;
        }

        return Task.FromResult(count);
    }

    private ServiceScheduleInfo MapScheduledService(ScheduledBackgroundService service)
    {
        return new ServiceScheduleInfo
        {
            Key = service.ServiceKey,
            IntervalHours = service.EffectiveInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(service.ServiceKey) ?? service.DefaultNotificationDisplayMode,
            SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
            IsRunning = service.IsCurrentlyExecuting,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
        };
    }

    private ServiceScheduleInfo MapConfigurableService(ConfigurableScheduledService service)
    {
        var key = GetServiceKey(service);

        // A service like ScheduledPrefillService implements IScheduleEnabledGate because its own
        // ConfiguredInterval is a fixed outer poll cadence (a 1-minute due-check) that re-stamps
        // LastRun/NextRun every tick and never reflects the real per-service schedule. For such a service,
        // derive the outer card's timing from the per-service reality instead of leaking the poll cadence.
        if (service is IScheduleEnabledGate gate)
        {
            // The outer card's timing is derived entirely from per-service reality, so scan the per-service
            // configs ONCE up front and reuse the result in every branch below:
            //   latestLastRun  = MAX per-service GENUINE last-run over ALL services (the honest "Last run").
            //                    This reads the actual-run map, NOT the schedule-basis map: the basis is
            //                    stamped by first-run anchoring and advanced on every skipped attempt, so it
            //                    holds a time before a service has ever truly run (and service.LastRunUtc is
            //                    even worse - the 1-minute poll re-stamps it every no-op tick). Null when no
            //                    service has ever genuinely run -> UI shows "Never run".
            //   soonestNextRun = MIN per-service next-run over ENABLED recurring services, computed from the
            //                    schedule BASIS (reusing ScheduledPrefillRunGates.ComputeNextRunUtc so the
            //                    outer card and the per-service detail agree). Null when nothing enabled is
            //                    recurring.
            var config = _stateService.GetScheduledPrefillConfig();
            DateTime? soonestNextRun = null;
            DateTime? latestLastRun = null;

            foreach (var perService in config.GetServicesInRunOrder())
            {
                var actualLastRun = _stateService.GetScheduledPrefillServiceLastActualRun(perService.ServiceId.ToString());
                if (actualLastRun is not null && (latestLastRun is null || actualLastRun.Value > latestLastRun.Value))
                {
                    latestLastRun = actualLastRun;
                }

                if (!perService.Enabled)
                {
                    continue;
                }

                var scheduleBasis = _stateService.GetScheduledPrefillServiceLastRun(perService.ServiceId.ToString());
                var nextRun = ScheduledPrefillRunGates.ComputeNextRunUtc(perService.IntervalHours, scheduleBasis);
                if (nextRun is not null && (soonestNextRun is null || nextRun.Value < soonestNextRun.Value))
                {
                    soonestNextRun = nextRun;
                }
            }

            // The 1-minute outer poll briefly flips IsCurrentlyExecuting on EVERY tick, including no-op
            // ticks with nothing due, so it is not a trustworthy "a prefill is actually running" signal
            // (an unrelated broadcast landing in that ms window would otherwise flash the card green).
            // Derive the running state from the tracked ScheduledPrefill operation instead - the same
            // source GetRunStatus uses - so only a genuine run lights the dot. Fall back to the base flag
            // when no tracker is wired (unit tests construct the registry without one).
            var isRunning = _tracker is not null
                ? _tracker.GetActiveOperations(OperationType.ScheduledPrefill).Any()
                : service.IsCurrentlyExecuting;

            // Nothing enabled: report paused (interval 0, no next-run) — the same representation the frontend
            // already renders for any interval-0 service (dimmed card, "Disabled" label, disabled Run Now).
            // LastRunUtc is the per-service MAX (null if nothing ever ran), never the poll stamp.
            if (!gate.HasAnyServiceEnabled())
            {
                return new ServiceScheduleInfo
                {
                    Key = key,
                    IntervalHours = 0,
                    RunOnStartup = service.RunOnStartup,
                    NotificationMode = service.EffectiveNotificationMode,
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
                    SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                    IsRunning = isRunning,
                    LastRunUtc = latestLastRun,
                    NextRunUtc = null,
                };
            }

            // Enabled, but no enabled service has a recurring next-run (every enabled one is startup-only -1
            // or paused 0, so ComputeNextRunUtc returned null for all). Report IntervalHours = -1, NOT 0:
            // interval 0 both dims the card AND disables Run Now (SchedulesSection.tsx: isDimmed :190,
            // Run Now disabled={isDisabled || isDimmed} :313) — but a service IS enabled here, so the user
            // must still be able to Run Now. -1 is the only value that keeps Run Now enabled without inventing
            // a countdown: CountdownDisplay short-circuits -1 to the "Startup only" label (:48-53) before the
            // countdown block, so a null NextRunUtc never renders a fake "Soon" (a positive interval would,
            // since useCountdownTimer(null) => 0 => "soon"). -1 is exactly truthful for the common
            // all-startup-only case; for a mixed startup-only/paused set it slightly over-states "startup
            // only", but that is the least-wrong of the three renderings the frontend offers (0 kills Run Now,
            // positive fabricates a countdown). NextRunUtc stays null — there is genuinely no scheduled run.
            if (soonestNextRun is null)
            {
                return new ServiceScheduleInfo
                {
                    Key = key,
                    IntervalHours = -1d,
                    RunOnStartup = service.RunOnStartup,
                    NotificationMode = service.EffectiveNotificationMode,
                    NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
                    SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                    IsRunning = isRunning,
                    LastRunUtc = latestLastRun,
                    NextRunUtc = null,
                };
            }

            // Enabled with a real recurring next-run: surface the soonest per-service next-run and the most
            // recent per-service last-run instead of the outer poll cadence.
            return new ServiceScheduleInfo
            {
                Key = key,
                IntervalHours = service.ConfiguredInterval.TotalHours,
                RunOnStartup = service.RunOnStartup,
                NotificationMode = service.EffectiveNotificationMode,
                NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
                SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
                IsRunning = isRunning,
                LastRunUtc = latestLastRun,
                NextRunUtc = soonestNextRun,
            };
        }

        // A manual REST trigger (the depot "rebuild now" endpoint -> SteamKit2Service.TryStartRebuild)
        // starts work WITHOUT going through the base loop that flips IsCurrentlyExecuting, so also treat
        // the service as running when it has an active tracked operation of its mapped type. This covers
        // both the scheduled crawl and a manual rebuild with one source of truth.
        var configurableIsRunning = service.IsCurrentlyExecuting;
        if (!configurableIsRunning && _tracker is not null && _runStatusOperationTypes.TryGetValue(key, out var runningOpType))
        {
            configurableIsRunning = _tracker.GetActiveOperations(runningOpType).Any();
        }

        return new ServiceScheduleInfo
        {
            Key = key,
            IntervalHours = service.ConfiguredInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            NotificationMode = service.EffectiveNotificationMode,
            NotificationDisplayMode = _stateService.GetServiceNotificationDisplayMode(key) ?? service.DefaultNotificationDisplayMode,
            SupportsNotifications = (bool?)GetPropertyValue(service.GetType(), service, "SupportsNotifications", typeof(bool)) ?? false,
            IsRunning = configurableIsRunning,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
        };
    }

    private static string GetServiceKey(ConfigurableScheduledService service)
    {
        var serviceType = service.GetType();
        return (string?)GetPropertyValue(serviceType, service, "ScheduleServiceKey", typeof(string)) ?? serviceType.Name;
    }

    /// <summary>
    /// Reads a property of type <paramref name="expectedType"/> by name, including protected
    /// declarations (ScheduleServiceKey/ServiceName are public/protected respectively;
    /// SupportsNotifications is protected and only present on leaf types that override it). Returns
    /// null if the property is absent, wrong-typed, or not overridden - callers cast to their
    /// expected nullable type and apply their own default for "absent" (e.g. the base class's own
    /// default value). Returns `object?` rather than a generic `T?` because an unconstrained `T?`
    /// does not reliably resolve to `Nullable<T>` for a value-type `T` (observed: `bool` call sites
    /// got a non-nullable `bool` return, breaking `?? false`) - callers cast explicitly instead.
    /// </summary>
    private static object? GetPropertyValue(Type type, object instance, string propertyName, Type expectedType)
    {
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || property.PropertyType != expectedType)
        {
            return null;
        }
        return property.GetValue(instance);
    }

    private static void ApplyInterval(ConfigurableScheduledService service, TimeSpan interval)
    {
        // UpdateInterval is protected in ConfigurableScheduledService.
        // Services may expose a public wrapper (e.g., UpdateInterval(TimeSpan)).
        // Fall back to reflection on the protected method.
        var serviceType = service.GetType();

        var publicMethod = serviceType.GetMethod("UpdateInterval",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            new[] { typeof(TimeSpan) });

        if (publicMethod != null)
        {
            publicMethod.Invoke(service, new object[] { interval });
            return;
        }

        var protectedMethod = serviceType.GetMethod("UpdateInterval",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            new[] { typeof(TimeSpan) });

        protectedMethod?.Invoke(service, new object[] { interval });
    }
}
