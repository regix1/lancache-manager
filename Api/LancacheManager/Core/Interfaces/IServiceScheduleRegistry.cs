using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

public interface IServiceScheduleRegistry
{
    IReadOnlyList<ServiceScheduleInfo> GetAll();
    ServiceScheduleInfo? Get(string serviceKey);
    void SetInterval(string serviceKey, double intervalHours);
    void SetRunOnStartup(string serviceKey, bool runOnStartup);
    void SetNotificationMode(string serviceKey, NotificationMode mode);

    /// <summary>
    /// Sets how the service's run notifications render in the notification bar (full card vs a
    /// condensed status line). Pure UI display state - no live service instance reads it, unlike
    /// <see cref="SetNotificationMode"/>.
    /// </summary>
    void SetNotificationDisplayMode(string serviceKey, NotificationDisplayMode mode);

    Task TriggerRunAsync(string serviceKey);

    /// <summary>
    /// Returns the live run status for a service by its key, or <c>null</c> when the key maps to no
    /// tracked operation type (an unknown key). When the key is known but no operation is currently
    /// active, the returned status reports <see cref="ScheduleRunStatus.IsRunning"/> = <c>false</c>.
    /// </summary>
    ScheduleRunStatus? GetRunStatus(string serviceKey);

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
    /// Fire-and-forget - matches the existing <c>OnServiceExecutionStateChangedAsync</c> pattern.
    /// </summary>
    void NotifySchedulesChanged();

    /// <summary>
    /// Broadcasts the current schedule list to all SignalR clients via <c>SchedulesUpdated</c>, awaiting
    /// the send. Serialized so it never interleaves with a concurrent run start/end broadcast: the
    /// snapshot is taken at send time and only one send is in flight at once, so the last delivered
    /// payload is always current. Every SchedulesUpdated emitter (controllers included) must route
    /// through here rather than calling the notification service directly, or an out-of-order stale
    /// snapshot could leave a finished service stuck showing "running".
    /// </summary>
    Task BroadcastSchedulesAsync();
}
