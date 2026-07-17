using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/system/schedules")]
[Authorize]
public class ScheduleController : ControllerBase
{
    private readonly IServiceScheduleRegistry _registry;
    private readonly ISignalRNotificationService _notifications;

    public ScheduleController(IServiceScheduleRegistry registry, ISignalRNotificationService notifications)
    {
        _registry = registry;
        _notifications = notifications;
    }

    /// <summary>
    /// Returns all registered service schedules.
    /// </summary>
    [HttpGet("")]
    public ActionResult<IReadOnlyList<ServiceScheduleInfo>> GetAll()
    {
        return Ok(_registry.GetAll());
    }

    /// <summary>
    /// Returns a single service schedule by its key.
    /// </summary>
    [HttpGet("{serviceKey}")]
    public ActionResult<ServiceScheduleInfo> GetByKey(string serviceKey)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }
        return Ok(info);
    }

    /// <summary>
    /// Updates the interval for a service schedule.
    /// </summary>
    [HttpPut("{serviceKey}")]
    public async Task<ActionResult> SetIntervalAsync(string serviceKey, [FromBody] UpdateScheduleIntervalRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        _registry.SetInterval(serviceKey, request.IntervalHours);
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Updates whether the service runs at app startup.
    /// </summary>
    [HttpPut("{serviceKey}/runOnStartup")]
    public async Task<ActionResult> SetRunOnStartupAsync(string serviceKey, [FromBody] UpdateScheduleRunOnStartupRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        _registry.SetRunOnStartup(serviceKey, request.RunOnStartup);
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Updates how the service surfaces its run notifications.
    /// </summary>
    [HttpPut("{serviceKey}/notificationMode")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult> SetNotificationModeAsync(string serviceKey, [FromBody] NotificationMode mode)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        // A service that does not surface run notifications has no generic notification mode to set.
        // Persisting one would be dead state, and scheduledPrefill (whose mode is per-platform, so it
        // never opts in here) must be rejected rather than silently no-op'd.
        if (!info.SupportsNotifications)
        {
            return Conflict(ApiResponse.Conflict("This schedule does not support run notifications."));
        }

        _registry.SetNotificationMode(serviceKey, mode);
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Updates how the service's run notifications render in the notification bar (full card vs a
    /// condensed status line). Unlike notification mode this is pure UI display state that no service
    /// behavior reads, so it carries no SupportsNotifications-style capability gate - every known
    /// service key is accepted, including scheduledPrefill (whose notification MODE is per-platform
    /// and therefore rejected above, but whose display mode is card-level).
    /// </summary>
    [HttpPut("{serviceKey}/notificationDisplayMode")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<ActionResult> SetNotificationDisplayModeAsync(string serviceKey, [FromBody] NotificationDisplayMode mode)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        _registry.SetNotificationDisplayMode(serviceKey, mode);
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }

    /// <summary>
    /// Returns the live run status for a service, used by the notification recovery pipeline to
    /// rehydrate an in-progress card after a page refresh. Read-only, so it stays <see cref="AuthorizeAttribute"/>
    /// (guest-readable) rather than AdminOnly, matching the other recovery status endpoints.
    /// </summary>
    [HttpGet("{serviceKey}/run-status")]
    public ActionResult<ScheduleRunStatus> GetRunStatus(string serviceKey)
    {
        var status = _registry.GetRunStatus(serviceKey);
        if (status == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        return Ok(status);
    }

    /// <summary>
    /// Triggers an immediate run of the service, bypassing the scheduled interval.
    /// </summary>
    [HttpPost("{serviceKey}/run")]
    public async Task<ActionResult> TriggerRunAsync(string serviceKey)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        await _registry.TriggerRunAsync(serviceKey);
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return Accepted();
    }

    /// <summary>
    /// Resets all service schedules to their hardcoded defaults.
    /// </summary>
    [HttpPost("reset")]
    public async Task<ActionResult> ResetToDefaultsAsync()
    {
        _registry.ResetToDefaults();
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return Ok();
    }

    /// <summary>
    /// Triggers an immediate run of every registered service.
    /// </summary>
    [HttpPost("run-all")]
    public async Task<ActionResult<TriggerAllResponse>> TriggerAllAsync()
    {
        var triggered = await _registry.TriggerAllAsync();
        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return Accepted(new TriggerAllResponse { TriggeredCount = triggered });
    }
}

public class TriggerAllResponse
{
    public int TriggeredCount { get; set; }
}

public class UpdateScheduleIntervalRequest
{
    public double IntervalHours { get; set; }
}

public class UpdateScheduleRunOnStartupRequest
{
    public bool RunOnStartup { get; set; }
}
