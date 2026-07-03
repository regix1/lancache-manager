namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Optional contract for a <c>ConfigurableScheduledService</c> whose "enabled" state depends on
/// nested per-service configuration rather than its own <c>ConfiguredInterval</c>. For example,
/// <c>ScheduledPrefillService</c>'s outer poll cadence is a fixed 1-minute due-check and never
/// reaches zero, even when every one of its 5 per-service configs is disabled. Without this gate,
/// <see cref="LancacheManager.Core.Services.ServiceScheduleRegistry"/> would report that fixed
/// poll cadence as the schedule's own interval/next-run, showing a live countdown on the Schedules
/// page even though the service is fully idle.
/// </summary>
public interface IScheduleEnabledGate
{
    /// <summary>
    /// Returns <c>true</c> when at least one nested service/config is enabled and the schedule
    /// should report its real interval/next-run. Returns <c>false</c> when nothing is enabled, in
    /// which case the registry reports the schedule as paused (interval 0, no next-run) instead of
    /// the outer poll's own cadence.
    /// </summary>
    bool HasAnyServiceEnabled();
}
