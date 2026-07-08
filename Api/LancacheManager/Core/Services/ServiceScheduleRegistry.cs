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
        "steamService",
        "cacheSnapshot",
        "operationHistoryCleanup",
        "logRotation",
        "dashboardCacheWarmer"
    };

    private readonly Dictionary<string, ScheduledBackgroundService> _scheduledServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigurableScheduledService> _configurableServices = new(StringComparer.OrdinalIgnoreCase);

    // ConfigurableScheduledService fires its static ServiceWorkCompleted event using the protected
    // ServiceName (see ConfigurableScheduledService.cs's ExecuteAsync loop and SteamService's identical,
    // documented dependency on that exact contract), NOT the ScheduleServiceKey that _configurableServices
    // above is keyed by. Track each tracked configurable service's ServiceName here too so
    // OnServiceWorkCompletedAsync's tracked-service guard recognizes the event when it arrives.
    private readonly HashSet<string> _configurableServiceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;

    public ServiceScheduleRegistry(IEnumerable<IHostedService> hostedServices, IStateService stateService, ISignalRNotificationService notifications)
    {
        _stateService = stateService;
        _notifications = notifications;
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

                // Also index by the protected ServiceName used by the ServiceWorkCompleted event
                // (see _configurableServiceNames above) so the completion guard can recognize it.
                var serviceName = GetStringProperty(configurableService.GetType(), configurableService, "ServiceName");
                if (!string.IsNullOrEmpty(serviceName))
                {
                    _configurableServiceNames.Add(serviceName);
                }
            }
        }

        ScheduledBackgroundService.ServiceWorkCompleted += OnServiceWorkCompletedAsync;
        ConfigurableScheduledService.ServiceWorkCompleted += OnServiceWorkCompletedAsync;
    }

    private async void OnServiceWorkCompletedAsync(string serviceKey)
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
            await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, GetAll());
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
        }
    }

    public void NotifySchedulesChanged()
    {
        // Re-use the same fire-and-forget SignalR broadcast path as work-completed ticks
        // so conditional-visibility changes (e.g. GC Aggressiveness flip) propagate to the
        // Schedules UI without a page reload. Any error is swallowed - matches existing pattern.
        _ = NotifySchedulesAsync();
    }

    private async Task NotifySchedulesAsync()
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, GetAll());
        }
        catch
        {
            // Non-fatal - SignalR broadcast failure should not affect service execution
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

    public void ResetToDefaults()
    {
        foreach (var (key, service) in _scheduledServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
        }

        foreach (var (key, service) in _configurableServices)
        {
            service.ResetInterval();
            service.SetRunOnStartup(null);
            _stateService.ClearServiceInterval(key);
            _stateService.ClearServiceRunOnStartup(key);
        }

        // Scheduled prefill keeps its cadence per-service in the config DTO + durable last-run map
        // (not in ServiceIntervals). Clearing that map alone would make the next poll treat every enabled
        // service as never-run and instant-run it, so ClearScheduledPrefillServiceLastRun also re-anchors
        // the currently-enabled services to now — a reset returns to a "wait one full interval" baseline
        // rather than leaving stale next-run times or triggering an immediate run.
        _stateService.ClearScheduledPrefillServiceLastRun();
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

    private static ServiceScheduleInfo MapScheduledService(ScheduledBackgroundService service)
    {
        return new ServiceScheduleInfo
        {
            Key = service.ServiceKey,
            IntervalHours = service.EffectiveInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
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
                    IsRunning = service.IsCurrentlyExecuting,
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
                    IsRunning = service.IsCurrentlyExecuting,
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
                IsRunning = service.IsCurrentlyExecuting,
                LastRunUtc = latestLastRun,
                NextRunUtc = soonestNextRun,
            };
        }

        return new ServiceScheduleInfo
        {
            Key = key,
            IntervalHours = service.ConfiguredInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            IsRunning = service.IsCurrentlyExecuting,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
        };
    }

    private static string GetServiceKey(ConfigurableScheduledService service)
    {
        var serviceType = service.GetType();
        return GetStringProperty(serviceType, service, "ScheduleServiceKey") ?? serviceType.Name;
    }

    private static string? GetStringProperty(Type type, object instance, string propertyName)
    {
        // Public | NonPublic | Instance: ScheduleServiceKey is public, but ServiceName (used by the
        // ServiceWorkCompleted-event lookup above) is declared protected on ConfigurableScheduledService.
        var property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || property.PropertyType != typeof(string))
        {
            return null;
        }
        return property.GetValue(instance) as string;
    }

    private static void ApplyInterval(ConfigurableScheduledService service, TimeSpan interval)
    {
        // UpdateInterval is protected in ConfigurableScheduledService.
        // Services may expose a public wrapper (e.g., UpdateInterval(TimeSpan)) added by Worker 3.
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
