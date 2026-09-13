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

public sealed class IntegrationLoginRequest
{
    public Guid? AttemptId { get; set; }
    public bool Recover { get; set; }
}
