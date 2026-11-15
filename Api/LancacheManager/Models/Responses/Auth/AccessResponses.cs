namespace LancacheManager.Models;

public sealed class AccessSetupRequest
{
    public required AccountMode Mode { get; set; }
    public string ApiKey { get; set; } = string.Empty;
    public bool Recovery { get; set; }
    public bool AcknowledgeUnauthenticated { get; set; }

    /// <summary>
    /// The account count the person confirmed deleting when switching to unauthenticated access, which
    /// deletes every account. Left out, or different from the count at the time of the save, the switch
    /// is answered with an <see cref="AccountRefusalResponse"/> whose stage key is
    /// <see cref="AccountRefusalResponse.WipeConfirmRequired"/> and whose context holds the current
    /// count, and nothing is saved.
    /// </summary>
    public int? ConfirmedAccountCount { get; set; }
    public LoginSetupRequest? Login { get; set; }
    public OidcSetupRequest? Oidc { get; set; }
}

public sealed class LoginSetupRequest
{
    public required LoginKind Kind { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Tenant { get; set; }
    public string? Authority { get; set; }
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }
    public string? PrivateKey { get; set; }
    public List<string> AllowedSubjects { get; set; } = [];
}

public sealed class OidcSetupRequest
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string DisplayName { get; set; } = "OpenID Connect";
    public List<string> AllowedSubjects { get; set; } = [];
}

public sealed class AccessSetupResponse
{
    public bool Success { get; set; }
    public bool RequiresOidcTest { get; set; }
    public bool RequiresLoginTest { get; set; }
    public string? PendingLoginId { get; set; }
    public List<string> CallbackUrls { get; set; } = [];
}

public sealed class OidcStartRequest
{
    public string ApiKey { get; set; } = string.Empty;
    public bool Setup { get; set; }
    public bool Recovery { get; set; }
    public bool Owner { get; set; }
}

public sealed class LoginStartRequest
{
    public string LoginId { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public bool Setup { get; set; }
    public bool Recovery { get; set; }
    public bool Owner { get; set; }
}

public sealed class RemoveLoginRequest
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class LoginServiceResponse
{
    public required string Id { get; set; }
    public required LoginKind Kind { get; set; }
    public required string DisplayName { get; set; }
    /// <summary>
    /// The settings of this login that environment variables supply; names only, never values.
    /// Empty for a caller who is neither an account holder nor completing setup.
    /// </summary>
    public List<EnvironmentField> EnvironmentFields { get; set; } = [];
}

public sealed class OidcStartResponse
{
    public required string Url { get; set; }
}
