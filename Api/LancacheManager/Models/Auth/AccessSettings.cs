namespace LancacheManager.Models;

public sealed class AccessSettings
{
    public const int RequiredSetupVersion = 11007;

    public AccountMode? Mode { get; set; }
    public int SetupVersion { get; set; }
    public long Revision { get; set; }
    public List<OidcSettings> Logins { get; set; } = [];
    public OidcSettings? Oidc { get; set; }
    public OidcSettings? PendingOidc { get; set; }
    public AccountMode? PendingMode { get; set; }
}

public sealed class OidcSettings
{
    public string Id { get; set; } = "customOidc";
    public LoginKind Kind { get; set; } = LoginKind.CustomOidc;
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string DisplayName { get; set; } = "OpenID Connect";
    public string? Tenant { get; set; }
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? PrivateKey { get; set; }
    public List<string> AllowedSubjects { get; set; } = [];
    public string? OwnerIssuer { get; set; }
    public string? OwnerSubject { get; set; }
    public Guid? OwnerAccountId { get; set; }
    public Dictionary<string, Guid> AccountIds { get; set; } = [];
    public int IdentityVersion { get; set; }
    public long Revision { get; set; }

    public OidcSettings Copy() => new()
    {
        Id = Id,
        Kind = Kind,
        Authority = Authority,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        DisplayName = DisplayName,
        Tenant = Tenant,
        TeamId = TeamId,
        KeyId = KeyId,
        PrivateKey = PrivateKey,
        AllowedSubjects = [.. AllowedSubjects],
        OwnerIssuer = OwnerIssuer,
        OwnerSubject = OwnerSubject,
        OwnerAccountId = OwnerAccountId,
        AccountIds = new Dictionary<string, Guid>(AccountIds, StringComparer.Ordinal),
        IdentityVersion = IdentityVersion,
        Revision = Revision
    };
}
