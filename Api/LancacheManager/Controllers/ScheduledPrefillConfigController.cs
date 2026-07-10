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
    private readonly IUnifiedOperationTracker _operationTracker;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        ISignalRNotificationService notifications,
        IServiceScheduleRegistry registry,
        IUnifiedOperationTracker operationTracker)
    {
        _stateService = stateService;
        _notifications = notifications;
        _registry = registry;
        _operationTracker = operationTracker;
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
            var key = service.ServiceId.ToString();
            // Schedule basis (anchor + advance-on-attempt) drives Next run; the genuine last-run drives the
            // "Last run" the card shows. They diverge until the service has truly run once: a just-enabled
            // (anchored) service has a schedule basis but a null actual-run, so Last run reads "Never" while
            // Next run still shows one interval out.
            var scheduleBasis = _stateService.GetScheduledPrefillServiceLastRun(key);
            var actualLastRun = _stateService.GetScheduledPrefillServiceLastActualRun(key);
            schedule.Add(new ScheduledPrefillServiceScheduleDto
            {
                ServiceId = service.ServiceId,
                IntervalHours = service.IntervalHours,
                Enabled = service.Enabled,
                LastRunUtc = actualLastRun,
                NextRunUtc = ScheduledPrefillRunGates.ComputeNextRunUtc(service.IntervalHours, scheduleBasis)
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

    /// <summary>
    /// Reports whether a scheduled prefill run is executing right now. Notification recovery calls
    /// this on (re)connect so a "Prefill in progress" card whose terminal SignalR event was missed
    /// (page closed or connection dropped mid-run) is stale-completed instead of lingering forever,
    /// and so a card is re-seeded when a run is genuinely still going.
    /// </summary>
    [HttpGet("run-status")]
    public ActionResult<ScheduledPrefillRunStatusDto> GetRunStatus()
    {
        var operation = _operationTracker
            .GetActiveOperations(OperationType.ScheduledPrefill)
            .FirstOrDefault();

        return Ok(new ScheduledPrefillRunStatusDto
        {
            IsRunning = operation is not null,
            OperationId = operation?.Id.ToString()
        });
    }
}

/// <summary>
/// Live run state returned by <c>GET /api/system/schedules/scheduledPrefill/run-status</c>.
/// </summary>
public sealed class ScheduledPrefillRunStatusDto
{
    /// <summary>True while a scheduled prefill run is actively executing.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>Operation id of the active run (for cancel wiring), or null when idle.</summary>
    public string? OperationId { get; init; }
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
