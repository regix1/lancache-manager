using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>Answers from the real checker, then holds the first caller until a second caller has
/// asked as well (at most 2 s). Two requests that check outside one lock therefore both act on
/// an answer given before either of them started anything.</summary>
internal sealed class SecondCallerBarrier(IOperationConflictChecker checker) : IOperationConflictChecker
{
    private readonly TaskCompletionSource _secondAsked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public async Task<OperationConflictResponse?> CheckAsync(OperationType newType, ConflictScope newScope, CancellationToken ct)
    {
        var conflict = await checker.CheckAsync(newType, newScope, ct);
        if (Interlocked.Increment(ref _calls) == 1)
        {
            await Task.WhenAny(_secondAsked.Task, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
        else
        {
            _secondAsked.TrySetResult();
        }

        return conflict;
    }

    public Task<OperationConflictResponse?> CheckForLogPassAsync(CancellationToken ct) => checker.CheckForLogPassAsync(ct);

    public IDisposable HoldRun(OperationType type, IReadOnlyList<ConflictScope> scopes, Func<Guid> currentOperationId) =>
        checker.HoldRun(type, scopes, currentOperationId);
}
