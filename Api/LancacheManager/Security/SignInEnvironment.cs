using LancacheManager.Models;

namespace LancacheManager.Security;

/// <summary>
/// The sign-in mode and sign-in service settings an administrator supplies through
/// <c>Security__*</c> environment variables. Read once at startup; the process environment does not
/// change while it runs, so every login built here carries revision 0.
/// </summary>
public sealed class SignInEnvironment
{
    private static readonly (string Name, LoginKind Kind)[] _services =
    [
        ("Oidc", LoginKind.CustomOidc),
        ("Google", LoginKind.Google),
        ("GitHub", LoginKind.GitHub),
        ("Microsoft", LoginKind.Microsoft),
        ("Apple", LoginKind.Apple)
    ];

    private readonly Dictionary<string, OidcSettings> _logins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlySet<EnvironmentField>> _fields = new(StringComparer.Ordinal);

    public SignInEnvironment(IConfiguration configuration, ILogger logger)
    {
        Mode = ReadMode(configuration);
        if (Mode is null && !string.IsNullOrWhiteSpace(configuration["Security:SignInMode"]))
        {
            logger.LogError("Security__SignInMode is not a known sign-in mode and is ignored");
        }
        if (Mode == AccountMode.Unauthenticated)
        {
            logger.LogWarning("Security__SignInMode turns sign-in off: anyone who can reach this server can manage it");
        }

        foreach (var (name, kind) in _services)
        {
            var fields = new HashSet<EnvironmentField>();
            string? Read(string setting, EnvironmentField field)
            {
                var value = configuration[$"Security:{name}:{setting}"];
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }
                fields.Add(field);
                return value;
            }
            string? ReadSecret(string setting, EnvironmentField field)
            {
                var inline = Read(setting, field);
                var path = configuration[$"Security:{name}:{setting}File"];
                if (string.IsNullOrWhiteSpace(path))
                {
                    return inline;
                }
                if (inline is not null)
                {
                    logger.LogWarning(
                        "{Variable} and {FileVariable} are both set; the file is used",
                        $"Security__{name}__{setting}",
                        $"Security__{name}__{setting}File");
                }

                try
                {
                    var value = File.ReadAllText(path).Trim();
                    fields.Add(field);
                    return value;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogError(ex, "{FileVariable} could not be read", $"Security__{name}__{setting}File");
                    return inline;
                }
            }

            var login = new OidcSettings
            {
                Id = AccessService.LoginId(kind),
                Kind = kind,
                ClientId = Read("ClientId", EnvironmentField.ClientId) ?? string.Empty,
                ClientSecret = ReadSecret("ClientSecret", EnvironmentField.ClientSecret),
                DisplayName = Read("DisplayName", EnvironmentField.DisplayName) ?? string.Empty,
                AllowedSubjects = Read("AllowedSubjects", EnvironmentField.AllowedSubjects)?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList() ?? [],
                OwnerSubject = Read("OwnerSubject", EnvironmentField.OwnerSubject)?.Trim(),
                OwnerIssuer = Read("OwnerIssuer", EnvironmentField.OwnerIssuer)?.Trim(),
                Authority = kind == LoginKind.CustomOidc
                    ? Read("Authority", EnvironmentField.Authority) ?? string.Empty
                    : string.Empty,
                Tenant = kind == LoginKind.Microsoft ? Read("Tenant", EnvironmentField.Tenant) : null,
                TeamId = kind == LoginKind.Apple ? Read("TeamId", EnvironmentField.TeamId) : null,
                KeyId = kind == LoginKind.Apple ? Read("KeyId", EnvironmentField.KeyId) : null,
                PrivateKey = kind == LoginKind.Apple ? ReadSecret("PrivateKey", EnvironmentField.PrivateKey) : null
            };
            if (fields.Count == 0)
            {
                continue;
            }

            // The owner's issuer follows the owner's subject: when only the subject is set, the
            // service's own issuer is filled in when the login is merged.
            if (fields.Contains(EnvironmentField.OwnerSubject))
            {
                fields.Add(EnvironmentField.OwnerIssuer);
            }
            _logins[login.Id] = login;
            _fields[login.Id] = fields;
        }
    }

    public AccountMode? Mode { get; }

    /// <summary>One login per sign-in service with at least one environment field, keyed by login id.</summary>
    public IReadOnlyDictionary<string, OidcSettings> Logins => _logins;

    /// <summary>The fields the environment set, keyed by login id.</summary>
    public IReadOnlyDictionary<string, IReadOnlySet<EnvironmentField>> Fields => _fields;

    /// <summary>
    /// Reads <c>Security__SignInMode</c>. Null when it is unset or not a mode name. <c>Sso</c> and
    /// <c>ApiKeyAndSso</c> are accepted for the two external sign-in modes.
    /// </summary>
    public static AccountMode? ReadMode(IConfiguration configuration)
        => configuration["Security:SignInMode"]?.Trim().ToLowerInvariant() switch
        {
            "password" => AccountMode.Password,
            "apikeypassword" => AccountMode.ApiKeyPassword,
            "apikeyoidc" or "apikeyandsso" => AccountMode.ApiKeyOidc,
            "oidc" or "sso" => AccountMode.Oidc,
            "unauthenticated" => AccountMode.Unauthenticated,
            _ => null
        };
}
