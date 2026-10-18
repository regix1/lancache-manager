using LancacheManager.Models;

namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Evaluates whether a prospective long-running operation conflicts with any
/// currently-active operation in <see cref="IUnifiedOperationTracker"/>.
///
/// Replaces the four ad-hoc conflict-response shapes scattered across controllers
/// (<c>ConflictResponse</c> / <c>ErrorResponse</c> / anonymous <c>{error}</c> /
/// anonymous <c>{error, operationId}</c>) with a single canonical
/// <see cref="OperationConflictResponse"/>.
/// </summary>
public interface IOperationConflictChecker
{
    /// <summary>
    /// Called by controllers just before <c>RegisterOperation</c>. Returns <c>null</c>
    /// if the new op is allowed to proceed; otherwise returns a canonical 409 body
    /// describing the blocking active operation.
    /// </summary>
    Task<OperationConflictResponse?> CheckAsync(OperationType newType, ConflictScope newScope, CancellationToken ct);

    /// <summary>
    /// Keeps a run that registers one operation per step in the check as one operation over all of
    /// <paramref name="scopes"/>, also between its steps, until the returned handle is disposed.
    /// A conflict names <paramref name="currentOperationId"/>, the step running now (empty before the
    /// first step registers). Disposing lets parked operations check again.
    /// </summary>
    IDisposable HoldRun(OperationType type, IReadOnlyList<ConflictScope> scopes, Func<Guid> currentOperationId);
}
