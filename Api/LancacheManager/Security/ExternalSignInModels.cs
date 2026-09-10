namespace LancacheManager.Security;

public sealed record ExternalSignInResult(
    Guid AccountId,
    Guid SessionId,
    string RawToken,
    DateTime ExpiresAtUtc,
    bool AccountCreated);
