using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Shared terminal contract implemented by every long-running-operation <c>*Complete</c> SignalR
/// record. It is an INTERFACE rather than a positional base record because the concrete records carry
/// their <see cref="OperationId"/> at different positions and with different nullability (<c>Guid</c>
/// vs <c>Guid?</c>), so a shared positional constructor could not fit them. Records satisfy the members
/// directly where a matching positional property exists, and via explicit interface implementation
/// (computed from <see cref="Success"/>/<see cref="Cancelled"/>, or from a per-record <c>Message</c>)
/// where one does not — the latter are NOT serialized, so the wire contract is unchanged.
///
/// Terminal semantics (aligned with the tri-layer status vocabulary): success =
/// <c>Success=true, Status=Completed, Cancelled=false, Error=null</c>; failure =
/// <c>Success=false, Status=Failed, Cancelled=false, Error=&lt;message&gt;</c>; cancellation =
/// <c>Cancelled=true, Status=Cancelled</c>; a run that found nothing to do =
/// <c>Success=true, Status=Skipped, Cancelled=false, Error=null</c>.
/// </summary>
public interface IOperationComplete
{
    /// <summary>Tracked operation id, or null for untracked (e.g. scheduled) completions.</summary>
    Guid? OperationId { get; }

    /// <summary>True when the operation completed successfully.</summary>
    bool Success { get; }

    /// <summary>Canonical terminal status (Completed / Failed / Cancelled / Skipped).</summary>
    OperationStatus Status { get; }

    /// <summary>True when the operation was cancelled (a distinct terminal outcome, never a failure).</summary>
    bool Cancelled { get; }

    /// <summary>Error/diagnostic message when the operation failed; null on success/cancel.</summary>
    string? Error { get; }

    /// <summary>
    /// The canonical terminal carrier for this completion, composed from the guaranteed members.
    /// Lets callers pass a uniform <see cref="OperationTerminalInfo"/> regardless of concrete record type.
    /// </summary>
    OperationTerminalInfo Terminal =>
        new(Success, Cancelled, Error, Status == OperationStatus.Skipped);
}
