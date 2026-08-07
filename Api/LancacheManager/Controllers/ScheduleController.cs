using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services.Scheduling;
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

    public ScheduleController(IServiceScheduleRegistry registry)
    {
        _registry = registry;
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
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{serviceKey}")]
    public async Task<ActionResult> SetIntervalAsync(string serviceKey, [FromBody] UpdateScheduleIntervalRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        _registry.SetInterval(serviceKey, request.IntervalHours);
        await _registry.BroadcastSchedulesAsync();
        return NoContent();
    }

    /// <summary>
    /// Sets the custom schedule that decides when the service runs, or clears it by sending a null
    /// schedule so the service goes back to its interval.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{serviceKey}/customSchedule")]
    public async Task<ActionResult> SetCustomScheduleAsync(string serviceKey, [FromBody] UpdateScheduleCustomScheduleRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        // Refuse a schedule that can never fire here rather than at the loop, which would accept it,
        // idle forever and only say so in a log line the admin never sees.
        if (request.CustomSchedule is not null)
        {
            var problem = ScheduleTiming.Validate(request.CustomSchedule);
            if (problem is not null)
            {
                return BadRequest(problem);
            }
        }

        if (!_registry.SetCustomSchedule(serviceKey, request.CustomSchedule))
        {
            return BadRequest("This service runs on a fixed interval and cannot take a custom schedule.");
        }

        await _registry.BroadcastSchedulesAsync();
        return NoContent();
    }

    /// <summary>
    /// Updates whether the service runs at app startup.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPut("{serviceKey}/runOnStartup")]
    public async Task<ActionResult> SetRunOnStartupAsync(string serviceKey, [FromBody] UpdateScheduleRunOnStartupRequest request)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        _registry.SetRunOnStartup(serviceKey, request.RunOnStartup);
        await _registry.BroadcastSchedulesAsync();
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
        await _registry.BroadcastSchedulesAsync();
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
        await _registry.BroadcastSchedulesAsync();
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
    /// Triggers an immediate run of the service, bypassing the scheduled interval. Reports whether
    /// this call actually armed a new run or collided with one already in progress, so a repeated
    /// click can be told apart from a real start.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("{serviceKey}/run")]
    public async Task<ActionResult<QueuedOperationResponse>> TriggerRunAsync(string serviceKey)
    {
        var info = _registry.Get(serviceKey);
        if (info == null)
        {
            return NotFound(ApiResponse.NotFound("Schedule"));
        }

        var status = await _registry.TriggerRunAsync(serviceKey);
        // Don't broadcast a schedule snapshot here. The run's status dot is driven by the service
        // loop's own ServiceExecutionStateChanged broadcasts - a START fires the moment the woken loop
        // begins the run. A snapshot at this point would capture IsRunning=false (the loop hasn't
        // started yet) and could be delivered AFTER that START, leaving the dot grey for the whole run.
        if (status.IsRunning)
        {
            // OperationId defaults to Guid.Empty when it can't be parsed (e.g. a running service the
            // tracker hasn't attached an id to yet) - the caller reads AlreadyRunning/Status, not this
            // field alone, so that default is an acceptable fallback rather than an error to surface.
            _ = Guid.TryParse(status.OperationId, out var activeOperationId);
            return Accepted(new QueuedOperationResponse
            {
                Status = "alreadyRunning",
                AlreadyRunning = true,
                OperationId = activeOperationId
            });
        }

        return Accepted(new QueuedOperationResponse
        {
            Status = "started"
        });
    }

    /// <summary>
    /// Resets all service schedules to their hardcoded defaults.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("reset")]
    public async Task<ActionResult> ResetToDefaultsAsync()
    {
        _registry.ResetToDefaults();
        await _registry.BroadcastSchedulesAsync();
        return Ok();
    }

    /// <summary>
    /// Triggers an immediate run of every registered service.
    /// </summary>
    [Authorize(Policy = "AdminOnly")]
    [HttpPost("run-all")]
    public async Task<ActionResult<TriggerAllResponse>> TriggerAllAsync()
    {
        var (triggeredCount, alreadyRunningCount) = await _registry.TriggerAllAsync();
        // As with the single-service run above, each woken service loop broadcasts its own run
        // start/end. A snapshot here would capture every service as not-yet-running and could race
        // those STARTs, so don't broadcast it.
        return Accepted(new TriggerAllResponse { TriggeredCount = triggeredCount, AlreadyRunningCount = alreadyRunningCount });
    }
}

public class TriggerAllResponse
{
    /// <summary>Services that were idle when this call reached them, so it started a new run.</summary>
    public int TriggeredCount { get; set; }

    /// <summary>Services that were already running when this call reached them. They were NOT
    /// skipped - each had one follow-up run armed via its own single pending-run flag, so it runs
    /// again once the current run finishes.</summary>
    public int AlreadyRunningCount { get; set; }
}

public class UpdateScheduleIntervalRequest
{
    public double IntervalHours { get; set; }
}

public class UpdateScheduleRunOnStartupRequest
{
    public bool RunOnStartup { get; set; }
}

public class UpdateScheduleCustomScheduleRequest
{
    /// <summary>
    /// Null clears the schedule and returns the service to its interval, which is why this is a
    /// wrapper rather than the schedule posted directly: an empty body is the clear operation.
    /// </summary>
    public CustomSchedule? CustomSchedule { get; set; }
}
