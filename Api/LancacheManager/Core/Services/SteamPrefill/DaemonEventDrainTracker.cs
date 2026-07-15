namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Tracks the fire-and-forget daemon event-callback tasks a socket/TCP client launches (each
/// <c>ProcessEventAsync</c> invocation, plus the receive loop's disconnect callback), so a teardown -
/// detach or terminate - can wait a bounded time for any in-flight callback to finish BEFORE the client
/// is disposed. This closes the window where an already-dispatched status/progress event writes a DB row
/// or broadcasts after teardown has declared completion. Admission and drain-start are one atomic
/// operation: <see cref="TryTrack"/> and <see cref="DrainAsync"/> share <c>_lock</c>, so once
/// <see cref="DrainAsync"/> has flipped draining and taken its snapshot, a later admission is REJECTED
/// (its callback never starts) instead of slipping in after the snapshot - the in-flight set cannot grow
/// during a drain. The de-register-before-drain ordering in the teardown consumers plus the
/// reference-equality guard in the event handlers remain load-bearing (defense in depth).
/// </summary>
internal sealed class DaemonEventDrainTracker
{
    private readonly ILogger? _logger;
    private readonly object _lock = new();
    private readonly HashSet<Task> _inFlight = new();
    private bool _draining; // guarded by _lock so admission + drain-start are atomic

    public DaemonEventDrainTracker(ILogger? logger)
    {
        _logger = logger;
    }

    /// <summary>True once <see cref="DrainAsync"/> has begun. A snapshot value; the real admission gate is
    /// the atomic <see cref="TryTrack"/>.</summary>
    public bool IsDraining
    {
        get { lock (_lock) return _draining; }
    }

    /// <summary>
    /// Atomically admits and starts an event callback. Under <c>_lock</c>: if draining has begun it
    /// returns <c>false</c> WITHOUT invoking <paramref name="callbackFactory"/> (the callback never runs);
    /// otherwise it invokes the factory, tracks the returned task (unless it already completed), and
    /// returns <c>true</c>. Starting the callback under the same lock <see cref="DrainAsync"/> uses to flip
    /// draining + snapshot is what makes a post-snapshot escape impossible.
    /// </summary>
    public bool TryTrack(Func<Task> callbackFactory)
    {
        lock (_lock)
        {
            if (_draining)
            {
                return false;
            }

            var task = callbackFactory();
            if (!task.IsCompleted)
            {
                _inFlight.Add(task);
                task.ContinueWith(
                    RemoveCompletedTask,
                    this,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else if (task.IsFaulted)
            {
                _logger?.LogWarning(task.Exception, "A daemon event callback faulted");
            }

            return true;
        }
    }

    private static void RemoveCompletedTask(Task completed, object? state)
    {
        var self = (DaemonEventDrainTracker)state!;
        if (completed.IsFaulted)
        {
            // Observe + log so a faulted tracked task is never an unobserved TaskException. The
            // dispatcher isolates handler faults, so a fault reaching here is unexpected.
            self._logger?.LogWarning(completed.Exception, "A daemon event callback faulted");
        }

        lock (self._lock)
        {
            self._inFlight.Remove(completed);
        }
    }

    /// <summary>
    /// Marks the tracker draining (so <see cref="TryTrack"/> rejects new callbacks) and waits up to
    /// <paramref name="timeout"/> for all currently in-flight event tasks to finish. Never throws and
    /// never blocks longer than <paramref name="timeout"/>; if the drain does not complete in time it logs
    /// a warning and returns so shutdown is never blocked. Best-effort: a callback slower than the timeout
    /// can still run afterwards, which is why the teardown consumers de-register the session first so its
    /// side effects hit the handler's reference-equality guard.
    /// </summary>
    public async Task DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task[] pending;
        lock (_lock)
        {
            _draining = true;
            pending = _inFlight.ToArray();
        }

        if (pending.Length == 0)
        {
            return;
        }

        var all = Task.WhenAll(pending);
        var finished = await Task.WhenAny(all, Task.Delay(timeout, cancellationToken));
        if (finished == all)
        {
            // Observe the aggregate so a faulted drained task is never unobserved (defensive - the
            // dispatcher already isolates handler faults).
            try
            {
                await all;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "One or more drained daemon event tasks faulted");
            }
        }
        else
        {
            _logger?.LogWarning(
                "Timed out after {Timeout}s draining {Count} in-flight daemon event task(s) during teardown; proceeding with client disposal",
                timeout.TotalSeconds, pending.Length);
        }
    }
}

/// <summary>
/// Multicast-safe async event invocation for the daemon clients. A bare <c>Func&lt;...,Task&gt;.Invoke</c>
/// on a MULTICAST delegate runs every subscriber but returns ONLY the last subscriber's task, so awaiting
/// it lets earlier subscribers' tasks run untracked - which lets a real status callback escape the event
/// drain whenever login attaches its temporary fail-fast subscriber last. These helpers invoke every
/// subscriber explicitly and return a task that completes when ALL of them finish, isolating each
/// subscriber's exceptions so one faulting handler neither stops the others nor faults the aggregate.
/// </summary>
internal static class DaemonEventDispatch
{
    public static Task InvokeAllAsync<T>(Func<T, Task>? handler, T arg, ILogger? logger)
    {
        if (handler == null)
        {
            return Task.CompletedTask;
        }

        var subscribers = handler.GetInvocationList();
        if (subscribers.Length == 1)
        {
            return InvokeOneAsync((Func<T, Task>)subscribers[0], arg, logger);
        }

        var tasks = new Task[subscribers.Length];
        for (var i = 0; i < subscribers.Length; i++)
        {
            tasks[i] = InvokeOneAsync((Func<T, Task>)subscribers[i], arg, logger);
        }
        return Task.WhenAll(tasks);
    }

    public static Task InvokeAllAsync(Func<Task>? handler, ILogger? logger)
    {
        if (handler == null)
        {
            return Task.CompletedTask;
        }

        var subscribers = handler.GetInvocationList();
        if (subscribers.Length == 1)
        {
            return InvokeOneAsync((Func<Task>)subscribers[0], logger);
        }

        var tasks = new Task[subscribers.Length];
        for (var i = 0; i < subscribers.Length; i++)
        {
            tasks[i] = InvokeOneAsync((Func<Task>)subscribers[i], logger);
        }
        return Task.WhenAll(tasks);
    }

    private static async Task InvokeOneAsync<T>(Func<T, Task> subscriber, T arg, ILogger? logger)
    {
        try
        {
            await subscriber(arg);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "A daemon event subscriber threw; isolated from the other subscribers");
        }
    }

    private static async Task InvokeOneAsync(Func<Task> subscriber, ILogger? logger)
    {
        try
        {
            await subscriber();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "A daemon event subscriber threw; isolated from the other subscribers");
        }
    }
}
