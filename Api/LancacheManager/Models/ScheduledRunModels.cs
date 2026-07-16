namespace LancacheManager.Models;

/// <summary>
/// The SignalR event-name triple for one scheduled service's run lifecycle (started / progress /
/// complete). Each pipeline-less maintenance service owns its own triple so lifecycle events stay
/// operationId-scoped per service; a shared event name would let a running card consume a concurrent
/// service's progress. A strongly-typed carrier (not three loose string arguments) so the reporter
/// cannot be constructed with a mismatched or reordered set.
/// </summary>
/// <param name="Started">Event name for the run-started broadcast (a SignalREvents constant).</param>
/// <param name="Progress">Event name for progress broadcasts (a SignalREvents constant).</param>
/// <param name="Complete">Event name for the single terminal broadcast (a SignalREvents constant).</param>
public readonly record struct ScheduledRunEventNames(string Started, string Progress, string Complete);

/// <summary>
/// Run-started payload for a scheduled maintenance service. Emitted once per run attempt.
/// <see cref="ShowNotification"/> is stamped once from the run's notification mode + trigger and is
/// immutable for the run; lifecycle events are ALWAYS emitted and the frontend gates display on this
/// flag (display-flag pattern, not transport suppression).
/// </summary>
public sealed record ScheduledRunStartedEvent(
    string ServiceKey,
    Guid OperationId,
    string StageKey,
    Dictionary<string, object?>? Context,
    bool ShowNotification);

/// <summary>
/// Progress payload for a scheduled maintenance service. <see cref="PercentComplete"/> is clamped
/// monotonic by the reporter (it never regresses below the highest value already sent).
/// </summary>
public sealed record ScheduledRunProgressEvent(
    string ServiceKey,
    Guid OperationId,
    string Status,
    string StageKey,
    double PercentComplete,
    Dictionary<string, object?>? Context,
    bool ShowNotification);

/// <summary>
/// Single terminal payload for a scheduled maintenance service. Emitted exactly once per run attempt
/// through the tracker's terminal-emit gate. On success <see cref="PercentComplete"/> carries 100; on
/// failure or cancellation it carries the highest percent reached (never a regression to 0).
/// Implements <see cref="IOperationComplete"/> so failures route through the uniform
/// <c>NotifyOperationFailedAsync</c> funnel. The interface's <c>OperationId</c>/<c>Status</c>/
/// <c>Cancelled</c> members are computed (explicit implementations) and are not serialized, so the
/// wire contract stays exactly the declared positional properties.
/// </summary>
public sealed record ScheduledRunCompleteEvent(
    string ServiceKey,
    Guid OperationId,
    bool Success,
    string StageKey,
    double PercentComplete,
    string? Error,
    Dictionary<string, object?>? Context,
    bool ShowNotification) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;

    OperationStatus IOperationComplete.Status =>
        Success ? OperationStatus.Completed : OperationStatus.Failed;

    // Cancellation is surfaced as Success=false with an "Cancelled by user" Error rather than a
    // distinct wire field (the run payloads carry no Cancelled flag). This member exists only to
    // satisfy the failure funnel's Cancelled=false precondition.
    bool IOperationComplete.Cancelled => false;
}
