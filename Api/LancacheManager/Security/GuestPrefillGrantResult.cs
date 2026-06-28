namespace LancacheManager.Security;

public sealed record GuestPrefillGrantResult(Guid SessionId, DateTime PrefillExpiresAtUtc);
