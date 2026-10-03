namespace LancacheManager.Models;

public sealed record IntegrationCaller(Guid? AccountId, Guid? SessionId, bool AuthenticationEnabled, bool OwnsInstallation = false);

public sealed record IntegrationLogin(
    Guid AttemptId,
    long Generation,
    Guid? AccountId,
    Guid? SessionId,
    DateTime ExpiresAtUtc,
    bool Shared,
    bool Recover);

public sealed record IntegrationAccess(
    bool CanManage,
    bool CanSignIn,
    bool CanLogout,
    bool CanCancel,
    bool CanRecover,
    string? OwnershipReason,
    Guid? AttemptId = null,
    DateTime? LoginExpiresAtUtc = null);

/// <summary>How one sign-in ended, kept by its attempt id for a browser that missed the ending.</summary>
public sealed record IntegrationLoginEnding(
    Guid AttemptId,
    OperationStatus Status,
    string StageKey,
    Dictionary<string, object?>? Context = null);

/// <summary>
/// How one sign-in on a prefill daemon session ended, kept by the session's attempt number for a browser that missed
/// the ending. The prefill counterpart of <see cref="IntegrationLoginEnding"/>: a daemon session numbers its sign-ins
/// instead of giving each one an id.
/// </summary>
public sealed record PrefillLoginEnding(long LoginAttempt, OperationStatus Status, string StageKey);

public sealed class IntegrationLoginRequest
{
    public Guid? AttemptId { get; set; }
    public bool Recover { get; set; }
}
