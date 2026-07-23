using System.Collections.Concurrent;

namespace LancacheManager.Core.Services.SteamPrefill;

internal sealed class DaemonClientConnectionLifecycle
{
    private long _nextGeneration;
    private long _currentGeneration;

    public bool IsConnected => CurrentGeneration != 0;

    public long CurrentGeneration => Volatile.Read(ref _currentGeneration);

    public long CreateGeneration()
        => Interlocked.Increment(ref _nextGeneration);

    public void MarkConnected(long generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);

        if (Interlocked.CompareExchange(ref _currentGeneration, generation, 0) != 0)
        {
            throw new InvalidOperationException("A daemon connection is already active.");
        }
    }

    public bool TryMarkDisconnected(long generation)
        => generation > 0
            && Interlocked.CompareExchange(ref _currentGeneration, 0, generation) == generation;

    public long MarkDisconnected()
        => Interlocked.Exchange(ref _currentGeneration, 0);
}

internal sealed class PendingDaemonCommand
{
    public PendingDaemonCommand(long generation)
    {
        Generation = generation;
        Completion = new TaskCompletionSource<CommandResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public long Generation { get; }

    public TaskCompletionSource<CommandResponse> Completion { get; }
}

internal static class DaemonPendingCommandRegistry
{
    public static int FailGeneration(
        ConcurrentDictionary<string, PendingDaemonCommand> pendingCommands,
        long generation,
        Exception exception)
    {
        var failedCount = 0;
        foreach (var pair in pendingCommands)
        {
            if (pair.Value.Generation != generation
                || !pendingCommands.TryRemove(pair.Key, out var removed))
            {
                continue;
            }

            removed.Completion.TrySetException(exception);
            failedCount++;
        }

        return failedCount;
    }

    public static int FailAll(
        ConcurrentDictionary<string, PendingDaemonCommand> pendingCommands,
        Exception exception)
    {
        var failedCount = 0;
        foreach (var pair in pendingCommands)
        {
            if (!pendingCommands.TryRemove(pair.Key, out var removed))
            {
                continue;
            }

            removed.Completion.TrySetException(exception);
            failedCount++;
        }

        return failedCount;
    }
}
