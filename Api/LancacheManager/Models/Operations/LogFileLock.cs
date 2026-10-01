using LancacheManager.Core.Services;

namespace LancacheManager.Models;

/// <summary>
/// A held turn on the datasource logs, granted by <see cref="OperationStateService.LockLogFilesAsync"/>.
/// Disposing it gives the turn back; a second dispose does nothing.
/// </summary>
public sealed class LogFileLock : IAsyncDisposable
{
    private OperationStateService? _owner;

    internal LogFileLock(
        OperationStateService owner,
        Guid? operationId,
        OperationType operationType,
        LogFileLockKind kind)
    {
        _owner = owner;
        OperationId = operationId;
        OperationType = operationType;
        Kind = kind;
    }

    /// <summary>The tracked operation holding the logs; null for the log endpoints and the orphan removal.</summary>
    public Guid? OperationId { get; }

    public OperationType OperationType { get; }

    public LogFileLockKind Kind { get; }

    public ValueTask DisposeAsync()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        return owner is null ? ValueTask.CompletedTask : new ValueTask(owner.ReleaseLogFileLockAsync(this));
    }
}
