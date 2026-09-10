using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

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
