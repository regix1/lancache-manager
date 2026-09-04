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

        // Which platforms have a run of their own in flight. One tracked operation per service means
        // this is answerable per row; the schedule-wide flag cannot distinguish "Steam is downloading"
        // from "Xbox may not start", which is what stopped a second service being run by hand. [48]
        // Keyed by platform and carrying the operation id, because the row needs both: the flag to
        // word itself and disable Run, the id to cancel that service's run. A lookup rather than a
        // dictionary so a duplicate registration cannot throw and take the whole schedule with it.
        var runningServices = _operationTracker
            .GetActiveOperations(OperationType.ScheduledPrefill)
            .Select(op => new { op.Id, State = op.Metadata as ScheduledPrefillServiceRunState })
            .Where(pair => pair.State is not null)
            .ToLookup(pair => pair.State!.ServiceId, pair => pair.Id);

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
                IsRunning = runningServices.Contains(service.ServiceId),
                OperationId = runningServices[service.ServiceId].Select(id => id.ToString()).FirstOrDefault(),
                LastRunUtc = actualLastRun,
                NextRunUtc = ScheduledPrefillRunGates.ComputeNextRunUtc(service.IntervalHours, scheduleBasis, service.CustomSchedule),
                CustomSchedule = service.CustomSchedule
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

        // A run registers one operation per due platform beside its run-level one, so the run-level
        // operation is the one WITHOUT a per-platform state rather than whichever the tracker
        // enumerated first.
        var operation = active.FirstOrDefault(op => op.Metadata is not ScheduledPrefillServiceRunState);

        return Ok(new ScheduledPrefillRunStatusDto
        {
            IsRunning = operation is not null,
            OperationId = operation?.Id.ToString(),
            ShowNotification = operation?.Metadata is ScheduledPrefillOperationMetadata metadata
                ? metadata.ShowNotification
                : true,
            Services = active
                .Select(op => new { Operation = op, State = op.Metadata as ScheduledPrefillServiceRunState })
                .Where(pair => pair.State is not null)
                .Select(pair => new ScheduledPrefillRunServiceStatus
                {
                    ServiceId = pair.State!.ServiceId,
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
    [HttpPost("services/{platform}/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ConflictResponse), StatusCodes.Status409Conflict)]
    public ActionResult RunService(PrefillPlatform platform)
    {
        // A disabled service does not prefill, however the run was asked for. Answered here rather than
        // in the scheduling loop so the press gets a reason instead of a silent no-op, and so the loop
        // is never woken for a platform it will drop. Temporary and guest prefill are a separate entity
        // and carry no such restriction; nothing on this controller governs them. [47]
        var service = _stateService.GetScheduledPrefillConfig()
            .GetServicesInRunOrder()
            .FirstOrDefault(s => s.ServiceId == platform);

        if (service is not null && !service.Enabled)
        {
            return Conflict(ApiResponse.Conflict($"Scheduled prefill for {platform} is disabled"));
        }

        var active = _operationTracker.GetActiveOperations(OperationType.ScheduledPrefill).ToList();

        if (active.Any(op => op.Metadata is ScheduledPrefillServiceRunState state && state.ServiceId == platform))
        {
            return Conflict(ApiResponse.Conflict($"Scheduled prefill for {platform} is already running"));
        }

        // The run starts on its own task, so it no longer waits for a run already in flight and there is
        // no queued outcome left to report. A false here means the claim was lost between the check
        // above and the call, which is a second press landing in that window. [43][49]
        if (!_scheduledPrefill.TriggerServiceRun(platform))
        {
            return Conflict(ApiResponse.Conflict($"Scheduled prefill for {platform} is already running"));
        }

        return Accepted(ApiResponse.Message($"Scheduled prefill started for {platform}"));
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

    /// <summary>This platform's own operation id, which its card is keyed on and its cancel targets.</summary>
    public required string OperationId { get; init; }

    /// <summary>The stage its last progress event reported, e.g. "running" or "needs-login".</summary>
    public required string Stage { get; init; }

    /// <summary>The English sentence that event put on the card.</summary>
    public required string Message { get; init; }

    /// <summary>The i18n key naming that same sentence, null when the text has no key.</summary>
    public string? StageKey { get; init; }

    /// <summary>The percent its bar was last moved to.</summary>
    public required double PercentComplete { get; init; }
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
