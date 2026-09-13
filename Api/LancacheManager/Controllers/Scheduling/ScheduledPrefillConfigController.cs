using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

[ApiController]
[Route("api/system/schedules/scheduledPrefill")]
// Account holders only: scheduled prefill config is shared mutable state. Bare [Authorize] satisfies
// the DefaultPolicy (RequireAuthenticatedUser), which a SessionType=guest session also satisfies. The
// AccountHolder policy (RequireClaim "SessionType" is "admin" or "user") blocks guests, matching every
// other shared-state controller (CacheController, PrefillAdminController, etc.).
[Authorize(Policy = "AccountHolder")]
public class ScheduledPrefillConfigController : ControllerBase
{
    private readonly IStateService _stateService;
    private readonly IServiceScheduleRegistry _registry;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ScheduledPrefillService _scheduledPrefill;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        IServiceScheduleRegistry registry,
        IUnifiedOperationTracker operationTracker,
        ScheduledPrefillService scheduledPrefill)
    {
        _stateService = stateService;
        _registry = registry;
        _operationTracker = operationTracker;
        _scheduledPrefill = scheduledPrefill;
    }

    /// <summary>
    /// Returns the current scheduled prefill configuration.
    /// </summary>
    [HttpGet("config")]
    [ProducesResponseType(typeof(ScheduledPrefillConfigDto), StatusCodes.Status200OK)]
    public ActionResult<ScheduledPrefillConfigDto> GetConfig()
    {
        return Ok(_stateService.GetScheduledPrefillConfig());
    }

    /// <summary>
    /// Returns the independent per-service schedule view.
    /// </summary>
    /// <remarks>
    /// Each service's interval, enabled flag, and the durable last/next run times. <c>nextRunUtc</c>
    /// = <c>lastRun + interval</c>, and is null when the service has never run or is paused /
    /// startup-only. A service running on a custom schedule gets that schedule's own next
    /// occurrence instead, whatever its interval value says, so the column is not blank for
    /// exactly the schedules that do not use an interval.
    /// </remarks>
    [HttpGet("schedule")]
    [ProducesResponseType(typeof(ScheduledPrefillServiceScheduleDto[]), StatusCodes.Status200OK)]
    public ActionResult<ScheduledPrefillServiceScheduleDto[]> GetSchedule()
    {
        var config = _stateService.GetScheduledPrefillConfig();
        var runningSchedules = _operationTracker
            .GetActiveOperations(OperationType.ScheduledPrefill)
            .Select(operation => new
            {
                operation.Id,
                State = operation.Metadata as ScheduledPrefillServiceRunState
            })
            .Where(pair => pair.State is not null)
            .ToLookup(pair => pair.State!.ScheduleId, pair => pair.Id);

        var schedule = config.GetSchedulesInRunOrder()
            .Select(record =>
            {
                var key = record.ScheduleId.ToString("N");
                var scheduleBasis = _stateService.GetScheduledPrefillServiceLastRun(key);
                var actualLastRun = _stateService.GetScheduledPrefillServiceLastActualRun(key);
                return new ScheduledPrefillServiceScheduleDto
                {
                    ServiceId = record.ServiceId,
                    ScheduleId = record.ScheduleId,
                    Name = record.ScheduleName,
                    IntervalHours = record.IntervalHours,
                    Enabled = record.Enabled,
                    IsRunning = runningSchedules.Contains(record.ScheduleId),
                    OperationId = runningSchedules[record.ScheduleId]
                        .Select(id => id.ToString())
                        .FirstOrDefault(),
                    LastRunUtc = actualLastRun,
                    NextRunUtc = record.Enabled
                        ? ScheduledPrefillRunGates.ComputeNextRunUtc(
                            record.IntervalHours,
                            scheduleBasis,
                            record.CustomSchedule)
                        : null,
                    CustomSchedule = record.CustomSchedule
                };
            })
            .ToArray();

        return Ok(schedule);
    }

    /// <summary>
    /// Saves the scheduled prefill configuration.
    /// </summary>
    [HttpPut("config")]
    public async Task<ActionResult> SetConfigAsync([FromBody] ScheduledPrefillConfigDto config)
    {
        ScheduledPrefillConfigDto validated;
        try
        {
            validated = ScheduledPrefillConfigFactory.Validate(config);
        }
        catch (ScheduledPrefillConfigValidationException ex)
        {
            return BadRequest(ex.Message);
        }

        var retainedIds = validated.GetSchedulesInRunOrder()
            .Select(schedule => schedule.ScheduleId)
            .ToHashSet();
        var deletingActive = _operationTracker
            .GetActiveOperations(OperationType.ScheduledPrefill)
            .Any(operation => operation.Metadata is ScheduledPrefillServiceRunState state
                && !retainedIds.Contains(state.ScheduleId));
        if (deletingActive)
        {
            return Conflict(ApiResponse.Conflict("An active scheduled prefill record cannot be deleted"));
        }

        _stateService.SetScheduledPrefillConfig(validated);
        await _registry.BroadcastSchedulesAsync();
        return NoContent();
    }

    /// <summary>
    /// Reports whether a scheduled prefill run is executing right now.
    /// </summary>
    /// <remarks>
    /// Notification recovery calls this on (re)connect so a "Prefill in progress" card whose
    /// terminal SignalR event was missed (page closed or connection dropped mid-run) is
    /// stale-completed instead of lingering forever, and so a card is re-seeded when a run is
    /// genuinely still going.
    /// </remarks>
    [HttpGet("run-status")]
    [ProducesResponseType(typeof(ScheduledPrefillRunStatusDto), StatusCodes.Status200OK)]
    public ActionResult<ScheduledPrefillRunStatusDto> GetRunStatus()
    {
        var active = _operationTracker.GetActiveOperations(OperationType.ScheduledPrefill).ToList();
        var operation = active.FirstOrDefault(op => op.Metadata is ScheduledPrefillOperationMetadata);
        var serviceStates = active
            .Select(op => new { Operation = op, State = op.Metadata as ScheduledPrefillServiceRunState })
            .Where(pair => pair.State is not null)
            .ToList();

        return Ok(new ScheduledPrefillRunStatusDto
        {
            IsRunning = operation is not null || serviceStates.Count > 0,
            OperationId = operation?.Id.ToString(),
            ShowNotification = operation?.Metadata is ScheduledPrefillOperationMetadata metadata
                ? metadata.ShowNotification
                : true,
            Services = serviceStates
                .Select(pair =>
                {
                    var snapshot = pair.State!.Snapshot;
                    return new ScheduledPrefillRunServiceStatus
                    {
                        ServiceId = pair.State.ServiceId,
                        ScheduleId = pair.State.ScheduleId,
                        Name = pair.State.Name,
                        ShowNotification = pair.State.ShowNotification,
                        OperationId = pair.Operation.Id.ToString(),
                        Stage = snapshot.Stage,
                        Message = snapshot.Message,
                        StageKey = snapshot.StageKey,
                        PercentComplete = snapshot.PercentComplete,
                        EventEpoch = snapshot.EventEpoch,
                        EventSequence = snapshot.EventSequence,
                        DaemonInstanceId = snapshot.DaemonInstanceId,
                        StageContext = snapshot.StageContext,
                        BytesDownloaded = snapshot.BytesDownloaded,
                        TotalBytes = snapshot.TotalBytes,
                        DownloadSessionId = snapshot.DownloadSessionId,
                        NeedsLoginReason = snapshot.NeedsLoginReason,
                        Recovering = snapshot.Recovering
                    };
                })
                .ToArray()
        });
    }

    /// <summary>
    /// Runs one named platform's scheduled prefill immediately, leaving every other platform alone.
    /// </summary>
    /// <remarks>
    /// Backs the per-service Run button on the Schedules page. The whole-schedule Run Now posts to
    /// <c>POST api/system/schedules/{serviceKey}/run</c> and runs every enabled service, because
    /// <c>scheduledPrefill</c> is one registry key and the trigger behind it takes no arguments.
    /// </remarks>
    [HttpPost("services/{platform}/schedules/{scheduleId:guid}/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ConflictResponse), StatusCodes.Status409Conflict)]
    public ActionResult RunService(PrefillPlatform platform, Guid scheduleId)
    {
        var schedule = _stateService.GetScheduledPrefillConfig()
            .GetSchedulesInRunOrder()
            .FirstOrDefault(record => record.ServiceId == platform && record.ScheduleId == scheduleId);
        if (schedule is null)
        {
            return NotFound();
        }

        var active = _operationTracker.GetActiveOperations(OperationType.ScheduledPrefill).ToList();
        if (active.Any(operation => operation.Metadata is ScheduledPrefillServiceRunState state
            && state.ServiceId == platform))
        {
            return Conflict(ApiResponse.Conflict(
                $"Scheduled prefill for {platform} is already running"));
        }

        var operationId = _scheduledPrefill.TriggerServiceRun(platform, scheduleId);
        if (operationId is null)
        {
            return Conflict(ApiResponse.Conflict(
                $"Scheduled prefill for {platform} is already running"));
        }

        return Accepted(new { operationId });
    }

}
