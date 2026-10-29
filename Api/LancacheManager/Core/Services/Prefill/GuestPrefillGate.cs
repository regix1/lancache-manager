using LancacheManager.Middleware;

namespace LancacheManager.Core.Services;

internal sealed class GuestPrefillGate
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, GuestPrefillState> _owners = new();
    // Per owner, the sessions whose failed teardown keeps that owner's cleanup pending.
    private readonly Dictionary<Guid, HashSet<string>> _failedTeardowns = new();

    public GuestPrefillStart EnterCreate(Guid userId, PrefillDaemonServiceBase daemon)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(userId, out var owner))
                _owners[userId] = owner = new GuestPrefillState();
            if (owner.Stops != 0 || owner.CleanupPending)
                throw new ForbiddenException("Prefill cleanup is in progress. Retry after it completes.")
                {
                    StageKey = "errors.prefill.cleanupInProgress"
                };
            var start = new GuestPrefillStart { UserId = userId, Daemon = daemon };
            owner.Starts.Add(start);
            return start;
        }
    }

    public void Check(GuestPrefillStart start)
    {
        lock (_sync)
        {
            if (start.Stopped)
                throw new ForbiddenException("This prefill session was stopped.") { StageKey = "errors.prefill.sessionStopped" };
        }
    }

    public void EnterStop(Guid userId)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(userId, out var owner))
                _owners[userId] = owner = new GuestPrefillState();
            owner.Stops++;
            foreach (var start in owner.Starts.Where(s => s.Active && !s.Stopped))
            {
                start.Stopped = true;
                // CancelAsync marks cancellation now without running callbacks on the stopping thread.
                start.CancellationTask = start.Cancellation.CancelAsync();
            }
        }
    }

    public void ExitStop(Guid userId)
    {
        lock (_sync)
        {
            var owner = _owners[userId];
            owner.Stops--;
            Prune(userId, owner);
        }
    }

    public void FinishCreate(GuestPrefillStart start, bool cleanupPending)
    {
        lock (_sync)
        {
            var owner = _owners[start.UserId];
            start.Active = false;
            owner.CleanupPending |= cleanupPending;
            if (!cleanupPending)
                owner.Starts.Remove(start);
            start.Completion.TrySetResult();
            Prune(start.UserId, owner);
        }
    }

    public void CompleteCreate(GuestPrefillStart start)
    {
        lock (_sync)
        {
            Check(start);
            FinishCreate(start, cleanupPending: false);
        }
    }

    public GuestPrefillStart[] GetStarts(Guid userId)
    {
        lock (_sync)
            return _owners.TryGetValue(userId, out var owner) ? owner.Starts.ToArray() : [];
    }

    public Guid[] GetPendingOwners()
    {
        lock (_sync)
            return _owners.Where(p => p.Value.CleanupPending).Select(p => p.Key).ToArray();
    }

    public void Retain(Guid userId, string sessionId)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(userId, out var owner))
                _owners[userId] = owner = new GuestPrefillState();
            if (!_failedTeardowns.TryGetValue(userId, out var sessionIds))
                _failedTeardowns[userId] = sessionIds = new HashSet<string>();
            sessionIds.Add(sessionId);
            owner.CleanupPending = true;
        }
    }

    // A teardown that succeeded no longer holds the owner; cleanup stays pending while another failed
    // teardown or a kept start of the owner remains.
    public void Release(Guid userId, string sessionId)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(userId, out var owner))
                return;
            if (_failedTeardowns.TryGetValue(userId, out var sessionIds) && sessionIds.Remove(sessionId) && sessionIds.Count == 0)
                _failedTeardowns.Remove(userId);
            owner.Starts.RemoveAll(s => !s.Active && s.Removed);
            owner.CleanupPending = _failedTeardowns.ContainsKey(userId) || owner.Starts.Any(s => !s.Active);
            Prune(userId, owner);
        }
    }

    public void FinishStop(Guid userId, bool cleanupPending)
    {
        lock (_sync)
        {
            if (!_owners.TryGetValue(userId, out var owner))
                return;
            owner.Starts.RemoveAll(s => !s.Active && s.Removed);
            owner.CleanupPending = cleanupPending || owner.Starts.Any(s => !s.Active) || _failedTeardowns.ContainsKey(userId);
        }
    }

    private void Prune(Guid userId, GuestPrefillState owner)
    {
        if (owner.Stops == 0 && owner.Starts.Count == 0 && !owner.CleanupPending)
            _owners.Remove(userId);
    }
}
