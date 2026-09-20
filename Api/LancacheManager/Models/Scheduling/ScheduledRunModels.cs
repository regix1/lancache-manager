using LancacheManager.Core.Interfaces;
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
/// <see cref="ShowNotification"/> selects a full card or background progress.
/// <see cref="HideNotification"/> suppresses both presentations. Both values are immutable for the
/// run; lifecycle events are always emitted so recovery and cancellation retain one contract.
/// </summary>
public sealed record ScheduledRunStartedEvent(
    string ServiceKey,
    Guid OperationId,
    string StageKey,
    Dictionary<string, object?>? Context,
    bool ShowNotification,
    bool HideNotification = false);

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
    bool ShowNotification,
    bool HideNotification = false);

/// <summary>
/// Single terminal payload for a scheduled maintenance service. Emitted exactly once per run attempt
/// through the tracker's terminal-emit gate. On success <see cref="PercentComplete"/> carries 100; on
/// failure or cancellation it carries the highest percent reached (never a regression to 0).
/// Implements <see cref="IOperationComplete"/> so failures route through the uniform
/// <c>NotifyOperationFailedAsync</c> funnel. The interface's <c>OperationId</c>/<c>Status</c>/
/// <c>Cancelled</c> members are public wire properties so cancellation remains a distinct terminal
/// outcome instead of being encoded as a failed run with a magic error string.
/// </summary>
public sealed record ScheduledRunCompleteEvent(
    string ServiceKey,
    Guid OperationId,
    bool Success,
    string StageKey,
    double PercentComplete,
    string? Error,
    Dictionary<string, object?>? Context,
    bool ShowNotification,
    bool Cancelled,
    OperationStatus Status,
    bool HideNotification = false) : IOperationComplete
{
    Guid? IOperationComplete.OperationId => OperationId;
}
