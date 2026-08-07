using LancacheManager.Infrastructure.Services.Scheduling;
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

    /// <summary>
    /// Sets the custom schedule that decides when the service runs, or clears it with <c>null</c> so
    /// the service returns to its interval. Returns false when the key names a service whose loop
    /// cannot honour a schedule, in which case nothing is stored: a schedule the UI shows as active
    /// while the service keeps running on its old interval is worse than refusing to save one.
    /// </summary>
    bool SetCustomSchedule(string serviceKey, CustomSchedule? schedule);

    /// <summary>
    /// Triggers an immediate run of the service, bypassing the scheduled interval, and reports the
    /// run state observed immediately before the trigger was armed. When that state already reports
    /// <see cref="ScheduleRunStatus.IsRunning"/> = <c>true</c>, this call could not start a second run
    /// (see <c>ScheduledServiceBase.TriggerImmediateRun</c>'s single pending-run flag) - the caller is
    /// colliding with the run described by the returned status, not starting a new one.
    /// </summary>
    Task<ScheduleRunStatus> TriggerRunAsync(string serviceKey);

    /// <summary>
    /// Returns the live run status for a service by its key, or <c>null</c> when the key maps to no
    /// tracked operation type (an unknown key). When the key is known but no operation is currently
    /// active, the returned status reports <see cref="ScheduleRunStatus.IsRunning"/> = <c>false</c>.
    /// </summary>
    ScheduleRunStatus? GetRunStatus(string serviceKey);

    /// <summary>
    /// Triggers an immediate run of every VISIBLE service (both scheduled and configurable),
    /// regardless of their interval or current running state, using the same visibility gate as
    /// <see cref="GetAll"/> so the counts never exceed the rows the user can see. Fire-and-forget
    /// per service - individual services own their concurrency. Returns how many were not-yet-running
    /// when triggered (a genuine new run) versus already running (which get one follow-up run armed
    /// rather than a second concurrent one, for the same single-pending-run reason as
    /// <see cref="TriggerRunAsync"/>).
    /// </summary>
    Task<(int TriggeredCount, int AlreadyRunningCount)> TriggerAllAsync();

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
