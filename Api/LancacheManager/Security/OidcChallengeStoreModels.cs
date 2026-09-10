namespace LancacheManager.Security;

public sealed record OidcChallenge(
    string LoginId,
    long Revision,
    bool Setup,
    bool Owner,
    DateTimeOffset ExpiresAtUtc);
