namespace LancacheManager.Models;

/// <summary>
/// Strongly-typed carrier passed to <see cref="OperationInfo.OnTerminalEmit"/> exactly once when an
/// operation reaches a terminal state inside <c>UnifiedOperationTracker.CompleteOperation</c>
/// (CompletedFlag-gated). Lets the owning service's terminal-emit closure decide which terminal
/// SignalR record to send without re-deriving success/cancelled state from the tracker.
/// NOT an anonymous object — keeps the contract explicit.
/// </summary>
/// <param name="Success">True when the operation completed successfully.</param>
/// <param name="Cancelled">True when the operation was cancelled (mirrors <see cref="OperationInfo.Cancelled"/>).</param>
/// <param name="Error">Error/diagnostic message when the operation failed; null on success/cancel.</param>
/// <param name="Skipped">
/// True when the run started, found nothing to do, and stopped without doing it. Carried alongside
/// <see cref="Success"/> rather than replacing it: a skipped run did not fail, so it must stay out
/// of the failure funnel, but it must not present as an ordinary completion either.
/// </param>
public readonly record struct OperationTerminalInfo(
    bool Success,
    bool Cancelled,
    string? Error,
    bool Skipped = false);
