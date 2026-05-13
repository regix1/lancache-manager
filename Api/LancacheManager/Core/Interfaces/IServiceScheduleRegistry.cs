using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IServiceScheduleRegistry
{
    IReadOnlyList<ServiceScheduleInfo> GetAll();
    ServiceScheduleInfo? Get(string serviceKey);
    void SetInterval(string serviceKey, double intervalHours);
    void SetRunOnStartup(string serviceKey, bool runOnStartup);
    Task TriggerRunAsync(string serviceKey);

    /// <summary>
    /// Triggers an immediate run of every registered service (both scheduled and configurable),
    /// regardless of their interval or current running state. Fire-and-forget per service -
    /// individual services own their concurrency. Returns the count of services triggered.
    /// </summary>
    Task<int> TriggerAllAsync();

    void ResetToDefaults();

    /// <summary>
    /// Broadcasts the current schedule list to all SignalR clients via <c>SchedulesUpdated</c>.
    /// Call this when a service's visibility changes (e.g. after GC Aggressiveness is flipped)
    /// so the Schedules UI can show/hide conditionally visible cards without a page reload.
    /// Fire-and-forget - matches the existing <c>OnServiceWorkCompletedAsync</c> pattern.
    /// </summary>
    void NotifySchedulesChanged();
}
