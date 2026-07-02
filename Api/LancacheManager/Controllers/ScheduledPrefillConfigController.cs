using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/system/schedules/scheduledPrefill")]
// Admin-only: scheduled prefill config is shared mutable state. Bare [Authorize] satisfies the
// DefaultPolicy (RequireAuthenticatedUser), which a SessionType=guest session also satisfies. The
// AdminOnly policy (RequireClaim "SessionType"=="admin") blocks guests, matching every other
// admin-only shared-state controller (CacheController, PrefillAdminController, etc.).
[Authorize(Policy = "AdminOnly")]
public class ScheduledPrefillConfigController : ControllerBase
{
    private readonly IStateService _stateService;
    private readonly ISignalRNotificationService _notifications;
    private readonly IServiceScheduleRegistry _registry;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        ISignalRNotificationService notifications,
        IServiceScheduleRegistry registry)
    {
        _stateService = stateService;
        _notifications = notifications;
        _registry = registry;
    }

    /// <summary>
    /// Returns the current scheduled prefill configuration.
    /// </summary>
    [HttpGet("config")]
    public ActionResult<ScheduledPrefillConfigDto> GetConfig()
    {
        return Ok(_stateService.GetScheduledPrefillConfig());
    }

    /// <summary>
    /// Returns the independent per-service schedule view: each service's interval, enabled flag, and
    /// the durable last/next run times. <c>nextRunUtc</c> = <c>lastRun + interval</c>, and is null when
    /// the service has never run or is paused / startup-only.
    /// </summary>
    [HttpGet("schedule")]
    public ActionResult<ScheduledPrefillServiceScheduleDto[]> GetSchedule()
    {
        var config = _stateService.GetScheduledPrefillConfig();

        var schedule = new List<ScheduledPrefillServiceScheduleDto>();
        foreach (var service in config.GetServicesInRunOrder())
        {
            var lastRun = _stateService.GetScheduledPrefillServiceLastRun(service.ServiceId.ToString());
            schedule.Add(new ScheduledPrefillServiceScheduleDto
            {
                ServiceId = service.ServiceId,
                IntervalHours = service.IntervalHours,
                Enabled = service.Enabled,
                LastRunUtc = lastRun,
                NextRunUtc = ScheduledPrefillRunGates.ComputeNextRunUtc(service.IntervalHours, lastRun)
            });
        }

        return Ok(schedule.ToArray());
    }

    /// <summary>
    /// Saves the scheduled prefill configuration.
    /// </summary>
    [HttpPut("config")]
    public async Task<ActionResult> SetConfigAsync([FromBody] ScheduledPrefillConfigDto config)
    {
        try
        {
            _stateService.SetScheduledPrefillConfig(config);
        }
        catch (ScheduledPrefillConfigValidationException ex)
        {
            return BadRequest(ex.Message);
        }

        await _notifications.NotifyAllAsync(SignalREvents.SchedulesUpdated, _registry.GetAll());
        return NoContent();
    }
}

/// <summary>
/// Per-service schedule row returned by <c>GET /api/system/schedules/scheduledPrefill/schedule</c>.
/// </summary>
public sealed class ScheduledPrefillServiceScheduleDto
{
    /// <summary>Platform this row describes (serializes as the PrefillPlatform name, e.g. "Steam").</summary>
    public required PrefillPlatform ServiceId { get; init; }

    /// <summary>Per-service cadence in hours: <c>&gt; 0</c> = every N hours, <c>0</c> = paused, <c>-1</c> = startup-only.</summary>
    public required double IntervalHours { get; init; }

    /// <summary>Master on/off for this service.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Last time this service actually ran (UTC), or null when it has never run.</summary>
    public DateTime? LastRunUtc { get; init; }

    /// <summary>Next scheduled run (UTC) = lastRun + interval; null when never-run, paused, or startup-only.</summary>
    public DateTime? NextRunUtc { get; init; }
}
