using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Core.Services;

public class ServiceScheduleRegistry : IServiceScheduleRegistry
{
    private static readonly HashSet<string> _allowedServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "cacheReconciliation",
        "gameDetection",
        "gameImageFetch",
        "steamService",
        "cacheSnapshot",
        "operationHistoryCleanup",
        "logRotation"
    };

    private readonly Dictionary<string, ScheduledBackgroundService> _scheduledServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ConfigurableScheduledService> _configurableServices = new(StringComparer.OrdinalIgnoreCase);
    private readonly IStateService _stateService;
    private readonly IHubContext<DownloadHub> _hubContext;

    public ServiceScheduleRegistry(IEnumerable<IHostedService> hostedServices, IStateService stateService, IHubContext<DownloadHub> hubContext)
    {
        _stateService = stateService;
        _hubContext = hubContext;
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
                var key = GetConfigurableServiceKey(configurableService);
                _configurableServices[key] = configurableService;
            }
        }

        ScheduledBackgroundService.ServiceWorkCompleted += OnServiceWorkCompletedAsync;
        ConfigurableScheduledService.ServiceWorkCompleted += OnServiceWorkCompletedAsync;
    }

    private async void OnServiceWorkCompletedAsync(string serviceKey)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("SchedulesUpdated", GetAll());
        }
        catch
        {
            // Non-fatal — SignalR broadcast failure should not affect service execution
        }
    }

    public void NotifySchedulesChanged()
    {
        // Re-use the same fire-and-forget SignalR broadcast path as work-completed ticks
        // so conditional-visibility changes (e.g. GC Aggressiveness flip) propagate to the
        // Schedules UI without a page reload. Any error is swallowed — matches existing pattern.
        _ = BroadcastSchedulesUpdatedAsync();
    }

    private async Task BroadcastSchedulesUpdatedAsync()
    {
        try
        {
            await _hubContext.Clients.All.SendAsync("SchedulesUpdated", GetAll());
        }
        catch
        {
            // Non-fatal — SignalR broadcast failure should not affect service execution
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
            InvokeUpdateInterval(configurable, TimeSpan.FromHours(intervalHours));
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

    private static ServiceScheduleInfo MapConfigurableService(ConfigurableScheduledService service)
    {
        return new ServiceScheduleInfo
        {
            Key = GetConfigurableServiceKey(service),
            IntervalHours = service.ConfiguredInterval.TotalHours,
            RunOnStartup = service.RunOnStartup,
            IsRunning = service.IsCurrentlyExecuting,
            LastRunUtc = service.LastRunUtc,
            NextRunUtc = service.NextRunUtc,
        };
    }

    private static string GetConfigurableServiceKey(ConfigurableScheduledService service)
    {
        var serviceType = service.GetType();
        return GetStringProperty(serviceType, service, "ScheduleServiceKey") ?? serviceType.Name;
    }

    private static string? GetStringProperty(Type type, object instance, string propertyName)
    {
        var property = type.GetProperty(propertyName);
        if (property == null || property.PropertyType != typeof(string))
        {
            return null;
        }
        return property.GetValue(instance) as string;
    }

    private static void InvokeUpdateInterval(ConfigurableScheduledService service, TimeSpan interval)
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
