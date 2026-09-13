namespace LancacheManager.Core.Services;

internal sealed record PersistentPrefillEditSessionStartRecord(
    string EditActionId,
    string SessionId,
    bool CreatedByEditSession);

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
    long Revision,
    Guid? RunId = null);

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
