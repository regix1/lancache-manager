using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Models;
using LancacheManager.Middleware;
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
    private readonly ILogger<ScheduledPrefillConfigController> _logger;

    public ScheduledPrefillConfigController(
        IStateService stateService,
        IServiceScheduleRegistry registry,
        IUnifiedOperationTracker operationTracker,
        ScheduledPrefillService scheduledPrefill,
        ILogger<ScheduledPrefillConfigController>? logger = null)
    {
        _stateService = stateService;
        _registry = registry;
        _operationTracker = operationTracker;
        _scheduledPrefill = scheduledPrefill;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<ScheduledPrefillConfigController>.Instance;
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
        await UpdateAsync(_ => config);
        return NoContent();
    }

    [HttpPost("services/{platform}/schedules")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> CreateScheduleAsync(
        PrefillPlatform platform, [FromBody] ScheduledPrefillSchedule schedule)
        => Ok(await UpdateAsync(config => ChangeService(config, platform, schedules => [.. schedules, schedule])));

    [HttpPut("services/{platform}/schedules/{scheduleId:guid}")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> SetScheduleAsync(
        PrefillPlatform platform, Guid scheduleId, [FromBody] ScheduledPrefillSchedule schedule)
    {
        if (schedule.Id != scheduleId)
            throw new ValidationException("The schedule ID must match the route.");
        return Ok(await UpdateAsync(config => ChangeSchedule(config, platform, scheduleId, _ => schedule)));
    }

    [HttpPut("services/{platform}/schedules/{scheduleId:guid}/enabled")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> SetEnabledAsync(
        PrefillPlatform platform, Guid scheduleId, [FromBody] ScheduledPrefillEnabledRequest request)
        => Ok(await UpdateAsync(config => ChangeSchedule(config, platform, scheduleId,
            schedule => CopyEnabled(schedule, request.Enabled))));

    [HttpPut("services/{platform}/schedules/{scheduleId:guid}/timing")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> SetTimingAsync(
        PrefillPlatform platform, Guid scheduleId, [FromBody] ScheduledPrefillTimingRequest request)
        => Ok(await UpdateAsync(config => ChangeSchedule(config, platform, scheduleId, schedule => new ScheduledPrefillSchedule
        {
            Id = schedule.Id,
            Name = schedule.Name,
            Enabled = schedule.Enabled,
            IntervalHours = request.IntervalHours,
            CustomSchedule = request.CustomSchedule,
            Preset = schedule.Preset,
            TopCount = schedule.TopCount,
            SelectedAppIds = schedule.SelectedAppIds,
            OperatingSystems = schedule.OperatingSystems,
            Force = schedule.Force,
            MaxConcurrency = schedule.MaxConcurrency,
            NotificationMode = schedule.NotificationMode,
            NotificationDisplayMode = schedule.NotificationDisplayMode
        })));

    [HttpDelete("services/{platform}/schedules/{scheduleId:guid}")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> DeleteScheduleAsync(PrefillPlatform platform, Guid scheduleId)
        => Ok(await UpdateAsync(config => ChangeService(config, platform, schedules =>
        {
            if (!schedules.Any(schedule => schedule.Id == scheduleId))
                throw new NotFoundException("Scheduled prefill record");
            if (schedules.Count == 1)
                throw new ValidationException("The final schedule for a service cannot be deleted.");
            return schedules.Where(schedule => schedule.Id != scheduleId).ToList();
        })));

    [HttpPut("schedules/enabled")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> SetAllEnabledAsync([FromBody] ScheduledPrefillEnabledRequest request)
        => Ok(await UpdateAsync(config => CopyConfig(config,
            config.GetServicesInRunOrder().Select(service => ScheduledPrefillConfigFactory.CopyServiceWithSchedules(
                service, service.Schedules.Select(schedule => CopyEnabled(schedule, request.Enabled)).ToList())).ToList(),
            config.PersistenceMode)));

    [HttpPut("settings")]
    public async Task<ActionResult<ScheduledPrefillConfigDto>> SetSettingsAsync([FromBody] ScheduledPrefillPersistenceRequest request)
        => Ok(await UpdateAsync(config => CopyConfig(config, config.GetServicesInRunOrder(), request.Mode)));

    private async Task<ScheduledPrefillConfigDto> UpdateAsync(Func<ScheduledPrefillConfigDto, ScheduledPrefillConfigDto> update)
    {
        ScheduledPrefillConfigDto saved;
        try
        {
            saved = _scheduledPrefill.UpdateConfig(update);
        }
        catch (ScheduledPrefillConfigValidationException ex)
        {
            throw new ValidationException(ex.Message);
        }
        // The file replacement has committed. Schedule broadcasts are best effort after that point.
        try
        {
            await _registry.BroadcastSchedulesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not broadcast the saved scheduled prefill configuration");
        }
        return saved;
    }

    private static ScheduledPrefillConfigDto ChangeSchedule(ScheduledPrefillConfigDto config, PrefillPlatform platform,
        Guid scheduleId, Func<ScheduledPrefillSchedule, ScheduledPrefillSchedule> update)
        => ChangeService(config, platform, schedules =>
        {
            var index = schedules.FindIndex(schedule => schedule.Id == scheduleId);
            if (index < 0) throw new NotFoundException("Scheduled prefill record");
            schedules[index] = update(schedules[index]);
            return schedules;
        });

    private static ScheduledPrefillConfigDto ChangeService(ScheduledPrefillConfigDto config, PrefillPlatform platform,
        Func<List<ScheduledPrefillSchedule>, List<ScheduledPrefillSchedule>> update)
    {
        if (!Enum.IsDefined(platform)) throw new ValidationException("Unknown scheduled prefill platform.");
        return CopyConfig(config, config.GetServicesInRunOrder().Select(service => service.ServiceId == platform
            ? ScheduledPrefillConfigFactory.CopyServiceWithSchedules(service, update(service.Schedules))
            : service).ToList(), config.PersistenceMode);
    }

    private static ScheduledPrefillConfigDto CopyConfig(ScheduledPrefillConfigDto config,
        IReadOnlyList<ScheduledPrefillServiceConfigDto> services, PersistenceMode? mode)
        => new()
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = mode,
            Steam = services.Single(service => service.ServiceId == PrefillPlatform.Steam),
            Epic = services.Single(service => service.ServiceId == PrefillPlatform.Epic),
            Xbox = services.Single(service => service.ServiceId == PrefillPlatform.Xbox),
            BattleNet = services.Single(service => service.ServiceId == PrefillPlatform.BattleNet),
            Riot = services.Single(service => service.ServiceId == PrefillPlatform.Riot)
        };

    private static ScheduledPrefillSchedule CopyEnabled(ScheduledPrefillSchedule schedule, bool enabled)
        => new()
        {
            Id = schedule.Id,
            Name = schedule.Name,
            Enabled = enabled,
            IntervalHours = schedule.IntervalHours,
            CustomSchedule = schedule.CustomSchedule,
            Preset = schedule.Preset,
            TopCount = schedule.TopCount,
            SelectedAppIds = schedule.SelectedAppIds,
            OperatingSystems = schedule.OperatingSystems,
            Force = schedule.Force,
            MaxConcurrency = schedule.MaxConcurrency,
            NotificationMode = schedule.NotificationMode,
            NotificationDisplayMode = schedule.NotificationDisplayMode
        };

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
            Services = serviceStates
                .Select(pair =>
                {
                    var snapshot = pair.State!.Snapshot;
                    return new ScheduledPrefillRunServiceStatus
                    {
                        ServiceId = pair.State.ServiceId,
                        ScheduleId = pair.State.ScheduleId,
                        Name = pair.State.Name,
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
