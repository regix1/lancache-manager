using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
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
                .Select(pair => new ScheduledPrefillRunServiceStatus
                {
                    ServiceId = pair.State!.ServiceId,
                    ScheduleId = pair.State.ScheduleId,
                    Name = pair.State.Name,
                    OperationId = pair.Operation.Id.ToString(),
                    Stage = pair.State.Stage,
                    Message = pair.State.Message,
                    StageKey = pair.State.StageKey,
                    PercentComplete = pair.State.PercentComplete
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

/// <summary>
/// Live run state returned by <c>GET /api/system/schedules/scheduledPrefill/run-status</c>.
/// </summary>
public sealed class ScheduledPrefillRunStatusDto
{
    /// <summary>True while a scheduled prefill run is actively executing.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>Operation id of the active run (for cancel wiring), or null when idle.</summary>
    public string? OperationId { get; init; }

    /// <summary>Whether this run should have a universal notification.</summary>
    public required bool ShowNotification { get; init; }

    /// <summary>
    /// One entry per platform currently prefilling in this run. Recovery rebuilds a notification card
    /// from each after a page reload, so a browser that reconnected mid-run sees every running
    /// platform rather than only the run itself. Empty when nothing is running. [25]
    /// </summary>
    public required IReadOnlyList<ScheduledPrefillRunServiceStatus> Services { get; init; }
}

/// <summary>
/// One platform's live state inside a scheduled prefill run, as returned by
/// <c>GET /api/system/schedules/scheduledPrefill/run-status</c>.
/// </summary>
public sealed class ScheduledPrefillRunServiceStatus
{
    /// <summary>Platform this entry describes (serializes as the PrefillPlatform name, e.g. "Steam").</summary>
    public required PrefillPlatform ServiceId { get; init; }

    public required Guid ScheduleId { get; init; }

    public required string Name { get; init; }

    /// <summary>This platform's own operation id, which its card is keyed on and its cancel targets.</summary>
    public required string OperationId { get; init; }

    /// <summary>The stage its last progress event reported, e.g. "running" or "needs-login".</summary>
    public required string Stage { get; init; }

    /// <summary>The English sentence that event put on the card.</summary>
    public required string Message { get; init; }

    /// <summary>The i18n key naming that same sentence, null when the text has no key.</summary>
    public string? StageKey { get; init; }

    /// <summary>The percent its bar was last moved to.</summary>
    public double? PercentComplete { get; init; }
}

/// <summary>
/// Per-service schedule row returned by <c>GET /api/system/schedules/scheduledPrefill/schedule</c>.
/// </summary>
public sealed class ScheduledPrefillServiceScheduleDto
{
    /// <summary>Platform this row describes (serializes as the PrefillPlatform name, e.g. "Steam").</summary>
    public required PrefillPlatform ServiceId { get; init; }

    public required Guid ScheduleId { get; init; }

    public required string Name { get; init; }

    /// <summary>Per-service cadence in hours: <c>&gt; 0</c> = every N hours, <c>0</c> = paused, <c>-1</c> = startup-only.</summary>
    public required double IntervalHours { get; init; }

    /// <summary>Master on/off for this service.</summary>
    public required bool Enabled { get; init; }

    /// <summary>
    /// True while THIS platform has a run of its own in flight. Per service, not per schedule: the
    /// services run concurrently, so a row must not read "something is running" as "I am running" and
    /// refuse a second start. [48]
    /// </summary>
    public required bool IsRunning { get; init; }

    /// <summary>
    /// Operation id of this platform's in-flight run, or null when it is not running. The row cancels
    /// through it: a silent run raises no notification, which used to leave no way to stop it.
    /// </summary>
    public string? OperationId { get; init; }

    /// <summary>Last time this service actually ran (UTC), or null when it has never run.</summary>
    public DateTime? LastRunUtc { get; init; }

    /// <summary>Next scheduled run (UTC) = lastRun + interval; null when never-run, paused, or startup-only.</summary>
    public DateTime? NextRunUtc { get; init; }

    /// <summary>
    /// The service's custom schedule when it has one, null when it runs on its plain interval.
    /// Mirrored from the config so a summary row can word its own timing without fetching the whole
    /// config first - <see cref="IntervalHours"/> alone cannot tell a paused service apart from one
    /// that keeps a paused interval while a schedule drives it.
    /// </summary>
    public CustomSchedule? CustomSchedule { get; init; }
}
