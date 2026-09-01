namespace LancacheManager.Core.Services;

internal sealed class PersistentPrefillEditStartRollbackException : Exception
{
    public PersistentPrefillEditStartRollbackException(
        string sessionId,
        string containerId,
        Exception innerException)
        : base(
            $"Persistent edit-owned session {sessionId} could not confirm rollback of container {containerId}.",
            innerException)
    {
        SessionId = sessionId;
        ContainerId = containerId;
    }

    public string SessionId { get; }
    public string ContainerId { get; }
}

internal sealed record PersistentPrefillEditSessionStartRecord(
    string EditActionId,
    string SessionId,
    bool CreatedByEditSession);

internal sealed class PersistentPrefillEditSessionStartLease
{
    private static readonly Task<PersistentPrefillEditSessionStartRecord> _rejectedCompletion =
        Task.FromResult(new PersistentPrefillEditSessionStartRecord(string.Empty, string.Empty, false));

    internal PersistentPrefillEditSessionStartLease(
        bool accepted,
        bool isOwner,
        Task<PersistentPrefillEditSessionStartRecord>? completion)
    {
        Accepted = accepted;
        IsOwner = isOwner;
        Completion = completion ?? _rejectedCompletion;
    }

    public bool Accepted { get; }
    public bool IsOwner { get; }
    public Task<PersistentPrefillEditSessionStartRecord> Completion { get; }
}

internal sealed class PersistentPrefillEditSessionCleanupLease
{
    internal PersistentPrefillEditSessionCleanupLease(
        bool isOwner,
        Task completion,
        IReadOnlyList<Task<PersistentPrefillEditSessionStartRecord>> pendingStarts,
        IReadOnlyList<Task<PersistentPrefillEditActionRecord>> pendingEditActions)
    {
        IsOwner = isOwner;
        Completion = completion;
        PendingStarts = pendingStarts;
        PendingEditActions = pendingEditActions;
    }

    public bool IsOwner { get; }
    public Task Completion { get; }
    public IReadOnlyList<Task<PersistentPrefillEditSessionStartRecord>> PendingStarts { get; }
    public IReadOnlyList<Task<PersistentPrefillEditActionRecord>> PendingEditActions { get; }
}

internal enum PersistentPrefillEditActionKind
{
    Selection,
    Prefill,
    Login,
    Credential
}

internal enum PersistentPrefillEditResourceKind
{
    Selection,
    Login,
    Prefill
}

internal enum PersistentPrefillEditActionOutcome
{
    Succeeded,
    NoChange,
    Conflict,
    Cancelled,
    Failed
}

internal sealed record PersistentPrefillEditActionRecord(
    string? EditSessionId,
    string? EditActionId,
    PersistentPrefillEditActionKind Kind,
    string SessionId,
    PersistentPrefillEditActionOutcome Outcome,
    IReadOnlyList<PersistentPrefillEditResourceKind> ConfirmedEffects,
    long Sequence);

internal sealed record PersistentPrefillEditResourceOwnership(
    PersistentPrefillEditResourceKind Kind,
    string SessionId,
    string? EditSessionId,
    string? EditActionId,
    long Revision);

internal sealed class PersistentPrefillEditActionState
{
    public required string? EditSessionId { get; init; }
    public required string? EditActionId { get; init; }
    public required PersistentPrefillEditActionKind Kind { get; init; }
    public required string SessionId { get; init; }
    public required long Sequence { get; init; }
    public required TaskCompletionSource<PersistentPrefillEditActionRecord> Completion { get; init; }
    public HashSet<PersistentPrefillEditResourceKind> ConfirmedEffects { get; } = [];
    public bool Completed { get; set; }
}

internal sealed class PersistentPrefillEditActionLease
{
    private static readonly Task<PersistentPrefillEditActionRecord> _rejectedCompletion =
        Task.FromResult(new PersistentPrefillEditActionRecord(
            null,
            null,
            PersistentPrefillEditActionKind.Selection,
            string.Empty,
            PersistentPrefillEditActionOutcome.NoChange,
            [],
            0));

    private readonly PersistentPrefillEditSessionGate? _gate;
    private readonly PersistentPrefillEditActionState? _state;

    internal PersistentPrefillEditActionLease(
        bool accepted,
        PersistentPrefillEditSessionGate? gate,
        PersistentPrefillEditActionState? state)
    {
        Accepted = accepted;
        _gate = gate;
        _state = state;
    }

    public bool Accepted { get; }
    public Task<PersistentPrefillEditActionRecord> Completion =>
        _state?.Completion.Task ?? _rejectedCompletion;

    public void ConfirmEffect(PersistentPrefillEditResourceKind kind)
    {
        if (_gate is not null && _state is not null)
        {
            _gate.ConfirmEditActionEffect(_state, kind);
        }
    }

    public void Complete(PersistentPrefillEditActionOutcome outcome)
    {
        if (_gate is not null && _state is not null)
        {
            _gate.CompleteEditAction(_state, outcome);
        }
    }
}

internal sealed class PersistentPrefillEditMutationLease : IAsyncDisposable
{
    private SemaphoreSlim? _gate;

    internal PersistentPrefillEditMutationLease(SemaphoreSlim gate)
    {
        _gate = gate;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _gate, null)?.Release();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Coordinates edit-session-owned persistent starts with compensating cleanup for one daemon service.
/// Cleanup places a tombstone before it waits, so a request that arrives later cannot create work
/// after cleanup has already won. Accepted starts retain their exact result for cleanup and retries.
/// </summary>
internal sealed class PersistentPrefillEditSessionGate
{
    private sealed class StartState
    {
        public required string EditSessionId { get; init; }
        public required string EditActionId { get; init; }
        public required long Sequence { get; init; }
        public required TaskCompletionSource<PersistentPrefillEditSessionStartRecord> Completion { get; init; }
        public bool Failed { get; set; }
        public PersistentPrefillEditSessionStartRecord? Result { get; set; }
    }

    private sealed class CleanupState
    {
        public required string CleanupId { get; init; }
        public required TaskCompletionSource Completion { get; init; }
    }

    private sealed class EditSessionState
    {
        public bool CleanupRequested { get; set; }
        public Dictionary<string, StartState> Starts { get; } = new(StringComparer.Ordinal);
        public List<PersistentPrefillEditActionState> EditActions { get; } = [];
        public CleanupState? Cleanup { get; set; }
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _mutationGate = new(1, 1);
    private readonly Dictionary<string, EditSessionState> _editSessions = new(StringComparer.Ordinal);
    private readonly List<StartState> _starts = [];
    private readonly Dictionary<
        (PersistentPrefillEditResourceKind Kind, string SessionId),
        PersistentPrefillEditResourceOwnership> _resourceOwners = [];
    private long _sequence;
    private long _resourceRevision;

    public PersistentPrefillEditSessionStartLease BeginStart(string editSessionId, string editActionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(editActionId);

        lock (_sync)
        {
            var editSession = GetOrCreateEditSession(editSessionId);
            if (editSession.CleanupRequested)
            {
                return new PersistentPrefillEditSessionStartLease(false, false, null);
            }

            if (editSession.Starts.TryGetValue(editActionId, out var existing))
            {
                return new PersistentPrefillEditSessionStartLease(true, false, existing.Completion.Task);
            }

            var completion = new TaskCompletionSource<PersistentPrefillEditSessionStartRecord>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new StartState
            {
                EditSessionId = editSessionId,
                EditActionId = editActionId,
                Sequence = ++_sequence,
                Completion = completion
            };
            editSession.Starts.Add(editActionId, start);
            _starts.Add(start);
            return new PersistentPrefillEditSessionStartLease(true, true, completion.Task);
        }
    }

    public void CompleteStart(
        string editSessionId,
        string editActionId,
        PersistentPrefillEditSessionStartRecord result)
    {
        lock (_sync)
        {
            var start = GetStart(editSessionId, editActionId);
            start.Result = result;
            start.Completion.TrySetResult(result);
        }
    }

    public void FailStart(string editSessionId, string editActionId, Exception error)
    {
        lock (_sync)
        {
            var start = GetStart(editSessionId, editActionId);
            start.Failed = true;
            start.Completion.TrySetException(error);
            _ = start.Completion.Task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }

    public PersistentPrefillEditActionLease BeginEditAction(
        string? editSessionId,
        string? editActionId,
        PersistentPrefillEditActionKind kind,
        string sessionId)
    {
        if (string.IsNullOrWhiteSpace(editSessionId)
            != string.IsNullOrWhiteSpace(editActionId))
        {
            throw new ArgumentException(
                "editSessionId and editActionId must either both be supplied or both be omitted.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        lock (_sync)
        {
            EditSessionState? editSession = null;
            if (!string.IsNullOrWhiteSpace(editSessionId))
            {
                editSession = GetOrCreateEditSession(editSessionId);
                if (editSession.CleanupRequested)
                {
                    return new PersistentPrefillEditActionLease(false, null, null);
                }
            }

            var action = new PersistentPrefillEditActionState
            {
                EditSessionId = editSessionId,
                EditActionId = editActionId,
                Kind = kind,
                SessionId = sessionId,
                Sequence = ++_sequence,
                Completion = new TaskCompletionSource<PersistentPrefillEditActionRecord>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            };
            editSession?.EditActions.Add(action);
            return new PersistentPrefillEditActionLease(true, this, action);
        }
    }

    public async ValueTask<PersistentPrefillEditMutationLease> EnterMutationAsync(
        CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken);
        return new PersistentPrefillEditMutationLease(_mutationGate);
    }

    internal void ConfirmEditActionEffect(
        PersistentPrefillEditActionState action,
        PersistentPrefillEditResourceKind kind)
    {
        lock (_sync)
        {
            if (action.Completed)
            {
                throw new InvalidOperationException(
                    $"Persistent-prefill edit action '{action.EditActionId}' is already complete.");
            }

            action.ConfirmedEffects.Add(kind);
            _resourceOwners[(kind, action.SessionId)] =
                new PersistentPrefillEditResourceOwnership(
                    kind,
                    action.SessionId,
                    action.EditSessionId,
                    action.EditActionId,
                    ++_resourceRevision);
        }
    }

    internal void CompleteEditAction(
        PersistentPrefillEditActionState action,
        PersistentPrefillEditActionOutcome outcome)
    {
        lock (_sync)
        {
            if (action.Completed)
            {
                return;
            }

            action.Completed = true;
            action.Completion.TrySetResult(new PersistentPrefillEditActionRecord(
                action.EditSessionId,
                action.EditActionId,
                action.Kind,
                action.SessionId,
                outcome,
                action.ConfirmedEffects.Order().ToArray(),
                action.Sequence));
        }
    }

    public IReadOnlyList<PersistentPrefillEditResourceOwnership> GetCompensableResources(
        string editSessionId,
        PersistentPrefillEditResourceKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editSessionId);

        lock (_sync)
        {
            return _resourceOwners.Values
                .Where(owner =>
                    owner.Kind == kind
                    && string.Equals(
                        owner.EditSessionId,
                        editSessionId,
                        StringComparison.Ordinal))
                .OrderBy(owner => owner.Revision)
                .ToArray();
        }
    }

    public bool CompleteCompensation(
        string editSessionId,
        PersistentPrefillEditResourceOwnership ownership)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editSessionId);

        lock (_sync)
        {
            var key = (ownership.Kind, ownership.SessionId);
            if (!_resourceOwners.TryGetValue(key, out var current)
                || current.Revision != ownership.Revision
                || !string.Equals(
                    current.EditSessionId,
                    editSessionId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            return _resourceOwners.Remove(key);
        }
    }

    public PersistentPrefillEditSessionCleanupLease BeginCleanup(string editSessionId, string cleanupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(editSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupId);

        lock (_sync)
        {
            var editSession = GetOrCreateEditSession(editSessionId);
            editSession.CleanupRequested = true;

            if (editSession.Cleanup is { } existing)
            {
                return new PersistentPrefillEditSessionCleanupLease(
                    false,
                    existing.Completion.Task,
                    editSession.Starts.Values.Select(start => start.Completion.Task).ToArray(),
                    editSession.EditActions.Select(editAction => editAction.Completion.Task).ToArray());
            }

            var cleanup = new CleanupState
            {
                CleanupId = cleanupId,
                Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            editSession.Cleanup = cleanup;
            return new PersistentPrefillEditSessionCleanupLease(
                true,
                cleanup.Completion.Task,
                editSession.Starts.Values.Select(start => start.Completion.Task).ToArray(),
                editSession.EditActions.Select(editAction => editAction.Completion.Task).ToArray());
        }
    }

    public void CompleteCleanup(string editSessionId, string cleanupId)
    {
        lock (_sync)
        {
            var editSession = GetEditSession(editSessionId);
            var cleanup = GetCleanup(editSessionId, cleanupId);
            cleanup.Completion.TrySetResult();

            // Cleanup has released every session this edit session owned, so its starts can no longer
            // answer a stop check and only hold the later edit sessions back. The edit session itself
            // stays behind as the tombstone BeginStart and BeginEditAction read, otherwise a request
            // arriving after cleanup finished would create work nothing is left to compensate.
            _starts.RemoveAll(start =>
                string.Equals(start.EditSessionId, editSessionId, StringComparison.Ordinal));
            editSession.Starts.Clear();
            editSession.EditActions.Clear();
        }
    }

    public void FailCleanup(string editSessionId, string cleanupId, Exception error)
    {
        lock (_sync)
        {
            var editSession = GetEditSession(editSessionId);
            var cleanup = GetCleanup(editSessionId, cleanupId);
            cleanup.Completion.TrySetException(error);
            _ = cleanup.Completion.Task.ContinueWith(
                completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            editSession.Cleanup = null;
        }
    }

    public bool CanStopStartedSession(string editSessionId, PersistentPrefillEditSessionStartRecord record)
    {
        if (!record.CreatedByEditSession)
        {
            return false;
        }

        lock (_sync)
        {
            var owner = GetStart(editSessionId, record.EditActionId);
            if (owner.Failed
                || owner.Result is null
                || !string.Equals(owner.Result.SessionId, record.SessionId, StringComparison.Ordinal))
            {
                return false;
            }

            return !_starts.Any(
                start => start.Sequence > owner.Sequence
                    && !start.Failed
                    && !string.Equals(start.EditSessionId, editSessionId, StringComparison.Ordinal));
        }
    }

    public bool CanStopUntrackedSession(string sessionId)
    {
        lock (_sync)
        {
            return !_starts.Any(
                start => !start.Failed
                    && (start.Result is null
                        || string.Equals(
                            start.Result.SessionId,
                            sessionId,
                            StringComparison.Ordinal)));
        }
    }

    public bool HasPendingLaterStart(string editSessionId, PersistentPrefillEditSessionStartRecord record)
    {
        lock (_sync)
        {
            var owner = GetStart(editSessionId, record.EditActionId);
            return _starts.Any(
                start => start.Sequence > owner.Sequence
                    && !start.Failed
                    && start.Result is null
                    && !string.Equals(start.EditSessionId, editSessionId, StringComparison.Ordinal));
        }
    }

    public bool HasPendingStart()
    {
        lock (_sync)
        {
            return _starts.Any(start => !start.Failed && start.Result is null);
        }
    }

    private EditSessionState GetOrCreateEditSession(string editSessionId)
    {
        if (_editSessions.TryGetValue(editSessionId, out var editSession))
        {
            return editSession;
        }

        editSession = new EditSessionState();
        _editSessions.Add(editSessionId, editSession);
        return editSession;
    }

    private EditSessionState GetEditSession(string editSessionId)
    {
        if (_editSessions.TryGetValue(editSessionId, out var editSession))
        {
            return editSession;
        }

        throw new InvalidOperationException($"Unknown persistent-prefill edit session '{editSessionId}'.");
    }

    private StartState GetStart(string editSessionId, string editActionId)
    {
        var editSession = GetEditSession(editSessionId);
        if (editSession.Starts.TryGetValue(editActionId, out var start))
        {
            return start;
        }

        throw new InvalidOperationException(
            $"Unknown persistent-prefill start edit action '{editActionId}' for edit session '{editSessionId}'.");
    }

    private CleanupState GetCleanup(string editSessionId, string cleanupId)
    {
        var cleanup = GetEditSession(editSessionId).Cleanup;
        if (cleanup is not null
            && string.Equals(cleanup.CleanupId, cleanupId, StringComparison.Ordinal))
        {
            return cleanup;
        }

        throw new InvalidOperationException(
            $"Unknown persistent-prefill cleanup '{cleanupId}' for edit session '{editSessionId}'.");
    }
}
