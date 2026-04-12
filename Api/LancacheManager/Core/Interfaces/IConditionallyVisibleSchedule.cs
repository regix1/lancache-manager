namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Optional contract implemented by background schedule services whose visibility on the
/// unified Schedules page depends on runtime configuration. When a service implements this
/// interface and returns <c>false</c> from <see cref="IsScheduleVisible"/>, the
/// <see cref="IServiceScheduleRegistry"/> hides it from <c>GetAll()</c> / <c>Get(serviceKey)</c>.
/// </summary>
public interface IConditionallyVisibleSchedule
{
    /// <summary>
    /// Returns <c>true</c> when the schedule card should be surfaced in the Schedules UI,
    /// <c>false</c> when it should be hidden. Implementations MUST be thread-safe and fast —
    /// this is called on every registry <c>GetAll()</c> / <c>Get()</c> and every SignalR
    /// <c>SchedulesUpdated</c> broadcast.
    /// </summary>
    bool IsScheduleVisible();
}
