using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

public sealed class AccessService
{
    public const string OidcScheme = "Oidc";
    public const string OidcSetupScheme = "OidcSetup";
    public const string GoogleScheme = "Google";
    public const string GoogleSetupScheme = "GoogleSetup";
    public const string GitHubScheme = "GitHub";
    public const string GitHubSetupScheme = "GitHubSetup";
    public const string MicrosoftScheme = "Microsoft";
    public const string MicrosoftSetupScheme = "MicrosoftSetup";
    public const string AppleScheme = "Apple";
    public const string AppleSetupScheme = "AppleSetup";
    public const string OidcCookieScheme = "OidcTemporary";
    public const string AppleCookieScheme = "AppleTemporary";

    private readonly StateService _stateService;
    private readonly IConfiguration _configuration;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;

    public AccessService(
        StateService stateService,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> dbContextFactory)
    {
        _stateService = stateService;
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
    }

    public AccountMode GetMode()
    {
        var mode = _stateService.GetState().Access.Mode;
        if (mode.HasValue)
        {
            return mode.Value;
        }

        return _configuration.GetValue<bool>("Security:EnableAuthentication", true)
            ? AccountMode.ApiKeyPassword
            : AccountMode.Unauthenticated;
    }

    public bool IsSetupRequired()
        => _stateService.GetState().Access.SetupVersion < AccessSettings.RequiredSetupVersion;

    public bool IsAuthenticationEnabled()
        => GetMode() != AccountMode.Unauthenticated;

    public bool RejectAccountlessAdminSession()
        => !IsSetupRequired() && GetMode() != AccountMode.Unauthenticated;

    public bool RequiresApiKeyForPassword()
        => GetMode() == AccountMode.ApiKeyPassword;

    public bool RequiresApiKeyForOidc()
        => GetMode() == AccountMode.ApiKeyOidc;

    public bool UsesPassword()
        => GetMode() is AccountMode.Password or AccountMode.ApiKeyPassword;

    public bool UsesOidc()
        => GetMode() is AccountMode.Oidc or AccountMode.ApiKeyOidc;

    public bool AllowsKeyRecovery()
        => !IsSetupRequired() && GetMode() == AccountMode.Unauthenticated;

    public bool AllowsOwnerPassword(UserAccount? account)
        => GetMode() == AccountMode.Unauthenticated && account?.IsMainAdmin == true;

    public bool HasPendingOidc()
        => _stateService.GetState().Access.PendingOidc is not null;

    public LoginKind? GetPendingLoginKind()
        => _stateService.GetState().Access.PendingOidc?.Kind;

    public string? GetOidcDisplayName()
    {
        var access = _stateService.GetState().Access;
        return access.PendingOidc?.DisplayName
            ?? ActiveSettings(access).FirstOrDefault()?.DisplayName;
    }

    public bool HasOidcOwner()
        => ActiveSettings(_stateService.GetState().Access).Any(settings => settings.OwnerSubject is not null);

    public bool HasOwnerAccount(Guid accountId)
        => ActiveSettings(_stateService.GetState().Access)
            .Any(settings => settings.OwnerAccountId == accountId);

    public async Task<bool> HasOwnerPasswordAsync()
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var passwordHash = await context.UserAccounts
            .Where(account => account.IsMainAdmin)
            .Select(account => account.PasswordHash)
            .SingleOrDefaultAsync();
        return !string.IsNullOrEmpty(passwordHash);
    }

    public async Task<bool> CanUsePasswordAsync()
    {
        if (_configuration.GetValue<bool>("Runtime:DatabaseSetupPending"))
        {
            return true;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        var owner = await context.UserAccounts
            .Where(account => account.IsMainAdmin)
            .Select(account => new { account.PasswordHash })
            .SingleOrDefaultAsync();
        return owner is null || !string.IsNullOrEmpty(owner.PasswordHash);
    }

    public List<LoginServiceResponse> GetLoginServices()
        => ActiveSettings(_stateService.GetState().Access)
            .Select(settings => new LoginServiceResponse
            {
                Id = settings.Id,
                Kind = settings.Kind,
                DisplayName = settings.DisplayName
            })
            .ToList();

    public List<string> GetOwnerLoginServices()
        => ActiveSettings(_stateService.GetState().Access)
            .Where(settings => settings.OwnerSubject is not null)
            .Select(settings => settings.Id)
            .ToList();

    public OidcSettings? GetOidcSettings(bool setup)
    {
        var access = _stateService.GetState().Access;
        if (setup)
        {
            return access.PendingOidc?.Copy();
        }

        var active = ActiveSettings(access);
        return (active.FirstOrDefault(settings => settings.Kind == LoginKind.CustomOidc)
            ?? (active.Count == 1 ? active[0] : null))?.Copy();
    }

    public OidcSettings? GetLoginSettings(string loginId, bool setup)
    {
        var access = _stateService.GetState().Access;
        if (setup)
        {
            return access.PendingOidc is { } pending
                && string.Equals(pending.Id, loginId, StringComparison.Ordinal)
                ? pending.Copy()
                : null;
        }

        return ActiveSettings(access)
            .FirstOrDefault(settings => string.Equals(settings.Id, loginId, StringComparison.Ordinal))
            ?.Copy();
    }

    public string? GetCompatibleLoginId(bool setup)
    {
        var access = _stateService.GetState().Access;
        if (setup)
        {
            return access.PendingOidc?.Id;
        }

        var active = ActiveSettings(access);
        return active.FirstOrDefault(settings => settings.Kind == LoginKind.CustomOidc)?.Id
            ?? (active.Count == 1 ? active[0].Id : null);
    }

    public static string GetScheme(LoginKind kind, bool setup)
        => (kind, setup) switch
        {
            (LoginKind.CustomOidc, false) => OidcScheme,
            (LoginKind.CustomOidc, true) => OidcSetupScheme,
            (LoginKind.Google, false) => GoogleScheme,
            (LoginKind.Google, true) => GoogleSetupScheme,
            (LoginKind.GitHub, false) => GitHubScheme,
            (LoginKind.GitHub, true) => GitHubSetupScheme,
            (LoginKind.Microsoft, false) => MicrosoftScheme,
            (LoginKind.Microsoft, true) => MicrosoftSetupScheme,
            (LoginKind.Apple, false) => AppleScheme,
            (LoginKind.Apple, true) => AppleSetupScheme,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public static string GetCallbackPath(LoginKind kind, bool setup)
        => (kind, setup) switch
        {
            (LoginKind.CustomOidc, false) => "/api/auth/oidc/callback",
            (LoginKind.CustomOidc, true) => "/api/auth/oidc/setup-callback",
            (LoginKind.Google, false) => "/api/auth/login/google/callback",
            (LoginKind.Google, true) => "/api/auth/login/google/setup-callback",
            (LoginKind.GitHub, false) => "/api/auth/login/github/callback",
            (LoginKind.GitHub, true) => "/api/auth/login/github/setup-callback",
            (LoginKind.Microsoft, false) => "/api/auth/login/microsoft/callback",
            (LoginKind.Microsoft, true) => "/api/auth/login/microsoft/setup-callback",
            (LoginKind.Apple, false) => "/api/auth/login/apple/callback",
            (LoginKind.Apple, true) => "/api/auth/login/apple/setup-callback",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public static bool TryReadScheme(string? scheme, out LoginKind kind, out bool setup)
    {
        (kind, setup) = scheme switch
        {
            OidcScheme => (LoginKind.CustomOidc, false),
            OidcSetupScheme => (LoginKind.CustomOidc, true),
            GoogleScheme => (LoginKind.Google, false),
            GoogleSetupScheme => (LoginKind.Google, true),
            GitHubScheme => (LoginKind.GitHub, false),
            GitHubSetupScheme => (LoginKind.GitHub, true),
            MicrosoftScheme => (LoginKind.Microsoft, false),
            MicrosoftSetupScheme => (LoginKind.Microsoft, true),
            AppleScheme => (LoginKind.Apple, false),
            AppleSetupScheme => (LoginKind.Apple, true),
            _ => default
        };
        return scheme is OidcScheme or OidcSetupScheme
            or GoogleScheme or GoogleSetupScheme
            or GitHubScheme or GitHubSetupScheme
            or MicrosoftScheme or MicrosoftSetupScheme
            or AppleScheme or AppleSetupScheme;
    }

    public async Task<bool> RequiresMainAdminAsync()
    {
        if (_configuration.GetValue<bool>("Runtime:DatabaseSetupPending"))
        {
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await context.UserAccounts.AnyAsync();
    }

    public bool CanDeferOidcAccount(long revision)
    {
        var state = _stateService.GetState();
        return !state.SetupCompleted
            && _configuration.GetValue<bool>("Runtime:DatabaseSetupPending")
            && state.Access.PendingOidc is { OwnerAccountId: null } pending
            && pending.Revision == revision;
    }

    public async Task<bool> IsMainAdminAsync(UserSession? session)
    {
        if (session?.AccountId is not { } accountId)
        {
            return false;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync();
        return await MainAdminVisibility.OwnsInstallationAsync(context, accountId);
    }

    public AccessSetupResponse Apply(AccessSetupRequest request, Guid? ownerAccountId)
    {
        if (request.Mode == AccountMode.Unauthenticated && !request.AcknowledgeUnauthenticated)
        {
            throw new ValidationException("Unauthenticated access must be explicitly acknowledged");
        }

        var loginRequest = request.Login ?? FromLegacy(request.Oidc);
        OidcSettings? pending = null;
        if (loginRequest is not null)
        {
            if (request.Mode is not (AccountMode.Oidc or AccountMode.ApiKeyOidc))
            {
                throw new ValidationException("Login service settings are only valid for an external sign-in mode");
            }
            pending = ValidateLogin(loginRequest, ownerAccountId);
        }
        else if (request.Mode is AccountMode.Oidc or AccountMode.ApiKeyOidc
            && ActiveSettings(_stateService.GetState().Access).Count == 0)
        {
            throw new ValidationException("At least one tested login service is required for this account mode");
        }

        var requiresLoginTest = pending is not null;
        _stateService.UpdateAccess(access =>
        {
            var nextRevision = checked(access.Revision + 1);
            access.Revision = nextRevision;

            if (pending is not null)
            {
                var existing = ActiveSettings(access).FirstOrDefault(settings =>
                    string.Equals(settings.Id, pending.Id, StringComparison.Ordinal));
                if (existing is not null)
                {
                    pending.AccountIds = new Dictionary<string, Guid>(existing.AccountIds, StringComparer.Ordinal);
                }
                pending.Revision = nextRevision;
                access.PendingOidc = pending;
                access.PendingMode = request.Mode;
                return;
            }

            access.Mode = request.Mode;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.PendingOidc = null;
            access.PendingMode = null;
        });

        return new AccessSetupResponse
        {
            Success = true,
            RequiresOidcTest = requiresLoginTest,
            RequiresLoginTest = requiresLoginTest,
            PendingLoginId = pending?.Id,
            CallbackUrls = pending is null
                ? []
                : [GetCallbackPath(pending.Kind, false), GetCallbackPath(pending.Kind, true)]
        };
    }

    public bool IdentityAllowed(
        string loginId,
        long revision,
        bool setup,
        bool ownerOnly,
        string issuer,
        string subject)
    {
        var settings = GetLoginSettings(loginId, setup);
        if (settings is null || settings.Revision != revision)
        {
            return false;
        }

        if (setup)
        {
            return true;
        }

        var owner = string.Equals(settings.OwnerIssuer, issuer, StringComparison.Ordinal)
            && string.Equals(settings.OwnerSubject, subject, StringComparison.Ordinal);
        if (ownerOnly)
        {
            return owner;
        }

        return owner || settings.AllowedSubjects.Contains(subject, StringComparer.Ordinal);
    }

    public bool IdentityAllowed(long revision, bool setup, bool ownerOnly, string issuer, string subject)
    {
        var settings = setup
            ? _stateService.GetState().Access.PendingOidc
            : ActiveSettings(_stateService.GetState().Access).FirstOrDefault(item => item.Revision == revision);
        return settings is not null
            && IdentityAllowed(settings.Id, revision, setup, ownerOnly, issuer, subject);
    }

    public bool PromotePendingLogin(
        string loginId,
        long revision,
        string issuer,
        string subject,
        Guid? ownerAccountId = null)
    {
        var promoted = false;
        _stateService.UpdateAccess(access =>
        {
            var pending = access.PendingOidc;
            if (pending is null
                || pending.Revision != revision
                || !string.Equals(pending.Id, loginId, StringComparison.Ordinal))
            {
                return;
            }

            pending.OwnerIssuer = issuer;
            pending.OwnerSubject = subject;
            pending.OwnerAccountId = ownerAccountId;
            var existing = access.Logins.FirstOrDefault(settings =>
                string.Equals(settings.Id, loginId, StringComparison.Ordinal));
            if (existing is not null)
            {
                foreach (var (identity, accountId) in existing.AccountIds)
                {
                    pending.AccountIds.TryAdd(identity, accountId);
                }
            }
            access.Logins.RemoveAll(settings => string.Equals(settings.Id, loginId, StringComparison.Ordinal));
            access.Logins.Add(pending);
            if (pending.Kind == LoginKind.CustomOidc)
            {
                access.Oidc = pending;
            }

            access.Mode = access.PendingMode ?? AccountMode.Oidc;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.PendingOidc = null;
            access.PendingMode = null;
            promoted = true;
        });
        return promoted;
    }

    public bool PromotePendingOidc(long revision, string issuer, string subject, Guid? ownerAccountId = null)
    {
        var loginId = _stateService.GetState().Access.PendingOidc?.Id;
        return loginId is not null
            && PromotePendingLogin(loginId, revision, issuer, subject, ownerAccountId);
    }

    public void SetOidcOwnerAccount(Guid accountId, long revision)
    {
        _stateService.UpdateAccess(access =>
        {
            var settings = access.Logins.FirstOrDefault(candidate => candidate.Revision == revision);
            if (settings is not null)
            {
                settings.OwnerAccountId = accountId;
            }
            if (access.Oidc is { } legacy && legacy.Revision == revision)
            {
                legacy.OwnerAccountId = accountId;
            }
        });
    }

    public Guid GetOrCreateOidcAccountId(string subject, long revision)
    {
        var settings = ActiveSettings(_stateService.GetState().Access)
            .FirstOrDefault(candidate => candidate.Revision == revision)
            ?? throw new ValidationException("Login settings changed during sign-in");
        return GetOrCreateAccountId(settings.Id, settings.OwnerIssuer ?? string.Empty, subject, revision);
    }

    public Guid GetOrCreateAccountId(string loginId, string issuer, string subject, long revision)
    {
        var accountId = Guid.Empty;
        var identityKey = IdentityKey(issuer, subject);
        _stateService.UpdateAccess(access =>
        {
            var settings = access.Logins.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, loginId, StringComparison.Ordinal)
                && candidate.Revision == revision);
            if (settings is null
                && access.Oidc is { } legacy
                && string.Equals(legacy.Id, loginId, StringComparison.Ordinal)
                && legacy.Revision == revision)
            {
                settings = legacy;
            }
            if (settings is null)
            {
                return;
            }

            if (!settings.AccountIds.TryGetValue(identityKey, out accountId))
            {
                accountId = Guid.NewGuid();
                settings.AccountIds[identityKey] = accountId;
            }
        });

        return accountId == Guid.Empty
            ? throw new ValidationException("Login settings changed during sign-in")
            : accountId;
    }

    public bool RemoveLogin(string loginId)
    {
        var removed = false;
        _stateService.UpdateAccess(access =>
        {
            var active = ActiveSettings(access);
            if (active.All(settings => !string.Equals(settings.Id, loginId, StringComparison.Ordinal)))
            {
                return;
            }
            if (access.Mode is AccountMode.Oidc or AccountMode.ApiKeyOidc && active.Count == 1)
            {
                throw new ConflictException("The last login service cannot be removed while external sign-in is selected");
            }

            access.Logins.RemoveAll(settings => string.Equals(settings.Id, loginId, StringComparison.Ordinal));
            if (string.Equals(access.Oidc?.Id, loginId, StringComparison.Ordinal))
            {
                access.Oidc = null;
            }
            if (string.Equals(access.PendingOidc?.Id, loginId, StringComparison.Ordinal))
            {
                access.PendingOidc = null;
                access.PendingMode = null;
            }
            access.Revision = checked(access.Revision + 1);
            removed = true;
        });
        return removed;
    }

    public void ForgetAccounts(IEnumerable<Guid> accountIds)
    {
        var forgotten = accountIds.ToHashSet();
        if (forgotten.Count == 0)
        {
            return;
        }

        _stateService.UpdateAccess(access =>
        {
            var ids = access.Logins.Select(settings => settings.Id)
                .Concat(access.Oidc is null ? [] : [access.Oidc.Id])
                .Concat(access.PendingOidc is null ? [] : [access.PendingOidc.Id])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var id in ids)
            {
                var settings = access.Logins
                    .Where(candidate => string.Equals(candidate.Id, id, StringComparison.Ordinal))
                    .Concat(access.Oidc is { } legacy
                        && string.Equals(legacy.Id, id, StringComparison.Ordinal)
                            ? [legacy]
                            : [])
                    .Concat(access.PendingOidc is { } pending
                        && string.Equals(pending.Id, id, StringComparison.Ordinal)
                            ? [pending]
                            : [])
                    .ToList();
                var changed = false;
                var issuer = settings
                    .Select(candidate => candidate.OwnerIssuer)
                    .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
                foreach (var login in settings)
                {
                    changed |= ForgetAccounts(login, forgotten, issuer);
                }
                if (!changed)
                {
                    continue;
                }

                var revision = checked(access.Revision + 1);
                access.Revision = revision;
                foreach (var login in settings)
                {
                    login.Revision = revision;
                }
            }
        });
    }

    private static LoginSetupRequest? FromLegacy(OidcSetupRequest? request)
        => request is null
            ? null
            : new LoginSetupRequest
            {
                Kind = LoginKind.CustomOidc,
                Authority = request.Authority,
                ClientId = request.ClientId,
                ClientSecret = request.ClientSecret,
                DisplayName = request.DisplayName,
                AllowedSubjects = request.AllowedSubjects
            };

    private static bool ForgetAccounts(
        OidcSettings settings,
        HashSet<Guid> forgotten,
        string? issuer)
    {
        var identityKeys = settings.AccountIds
            .Where(entry => forgotten.Contains(entry.Value))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var key in identityKeys)
        {
            settings.AccountIds.Remove(key);
        }

        if (identityKeys.Count > 0 && !string.IsNullOrWhiteSpace(issuer))
        {
            settings.AllowedSubjects.RemoveAll(subject =>
                identityKeys.Contains(IdentityKey(issuer, subject)));
        }

        var ownerRemoved = settings.OwnerAccountId is { } ownerId && forgotten.Contains(ownerId);
        if (ownerRemoved)
        {
            settings.OwnerAccountId = null;
            settings.OwnerIssuer = null;
            settings.OwnerSubject = null;
        }
        return identityKeys.Count > 0 || ownerRemoved;
    }

    private static OidcSettings ValidateLogin(LoginSetupRequest request, Guid? ownerAccountId)
    {
        var id = LoginId(request.Kind);
        var authority = request.Kind switch
        {
            LoginKind.Google => "https://accounts.google.com",
            LoginKind.GitHub => string.Empty,
            LoginKind.Apple => "https://appleid.apple.com",
            LoginKind.Microsoft => MicrosoftAuthority(request.Tenant),
            LoginKind.CustomOidc => CustomAuthority(request.Authority),
            _ => throw new ValidationException("Unsupported login kind")
        };

        if (string.IsNullOrWhiteSpace(request.ClientId) || request.ClientId.Length > 512)
        {
            throw new ValidationException("Client ID is required");
        }
        if (request.Kind != LoginKind.Apple
            && (string.IsNullOrWhiteSpace(request.ClientSecret) || request.ClientSecret.Length > 4096))
        {
            throw new ValidationException("Client secret is required");
        }
        if (request.Kind == LoginKind.Apple)
        {
            ValidateApple(request);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? DisplayName(request.Kind)
            : request.DisplayName.Trim();
        if (displayName.Length > 80)
        {
            throw new ValidationException("Login display name cannot exceed 80 characters");
        }

        var allowedSubjects = request.AllowedSubjects ?? [];
        if (allowedSubjects.Count > 100 || allowedSubjects.Any(subject => subject?.Length > 512))
        {
            throw new ValidationException("Login subject allowlist is too large");
        }

        return new OidcSettings
        {
            Id = id,
            Kind = request.Kind,
            Authority = authority,
            ClientId = request.ClientId.Trim(),
            ClientSecret = request.Kind == LoginKind.Apple ? null : request.ClientSecret,
            DisplayName = displayName,
            Tenant = request.Kind == LoginKind.Microsoft ? request.Tenant?.Trim() : null,
            TeamId = request.Kind == LoginKind.Apple ? request.TeamId?.Trim() : null,
            KeyId = request.Kind == LoginKind.Apple ? request.KeyId?.Trim() : null,
            PrivateKey = request.Kind == LoginKind.Apple ? request.PrivateKey?.Trim() : null,
            AllowedSubjects = allowedSubjects
                .Where(subject => !string.IsNullOrWhiteSpace(subject))
                .Select(subject => subject.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            OwnerAccountId = ownerAccountId,
            IdentityVersion = 1
        };
    }

    private static string CustomAuthority(string? value)
    {
        var authorityValue = value?.Trim();
        if (string.IsNullOrWhiteSpace(authorityValue)
            || authorityValue.Length > 2048
            || !Uri.TryCreate(authorityValue, UriKind.Absolute, out var authority)
            || authority.Scheme != Uri.UriSchemeHttps)
        {
            throw new ValidationException("OIDC authority must be an absolute HTTPS URL");
        }
        return authority.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string MicrosoftAuthority(string? tenantValue)
    {
        var tenant = tenantValue?.Trim();
        if (!string.Equals(tenant, "consumers", StringComparison.OrdinalIgnoreCase)
            && (!Guid.TryParseExact(tenant, "D", out var tenantId) || tenantId == Guid.Empty))
        {
            throw new ValidationException("Microsoft tenant must be a tenant GUID or consumers");
        }
        var tenantName = string.Equals(tenant, "consumers", StringComparison.OrdinalIgnoreCase)
            ? "9188040d-6c67-4c5b-b112-36a304b66dad"
            : tenant!.ToLowerInvariant();
        return $"https://login.microsoftonline.com/{tenantName}/v2.0";
    }

    private static void ValidateApple(LoginSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TeamId)
            || request.TeamId.Length > 128
            || string.IsNullOrWhiteSpace(request.KeyId)
            || request.KeyId.Length > 128
            || string.IsNullOrWhiteSpace(request.PrivateKey)
            || request.PrivateKey.Length > 16384)
        {
            throw new ValidationException("Apple Team ID, Key ID, and private key are required");
        }

        try
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(request.PrivateKey);
            if (key.KeySize != 256)
            {
                throw new ValidationException("Apple private key must use the P-256 curve");
            }
        }
        catch (CryptographicException)
        {
            throw new ValidationException("Apple private key is not a valid EC private key");
        }
    }

    private static string LoginId(LoginKind kind)
        => kind switch
        {
            LoginKind.CustomOidc => "customOidc",
            LoginKind.Google => "google",
            LoginKind.GitHub => "github",
            LoginKind.Microsoft => "microsoft",
            LoginKind.Apple => "apple",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    private static string DisplayName(LoginKind kind)
        => kind switch
        {
            LoginKind.CustomOidc => "OpenID Connect",
            LoginKind.Google => "Google",
            LoginKind.GitHub => "GitHub",
            LoginKind.Microsoft => "Microsoft",
            LoginKind.Apple => "Apple",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    internal static string IdentityKey(string issuer, string subject)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}\0{subject}"));
        return Convert.ToHexString(bytes);
    }

    private static List<OidcSettings> ActiveSettings(AccessSettings access)
    {
        var active = (access.Logins ?? []).ToList();
        if (access.Oidc is { } legacy
            && active.All(settings => settings.Kind != LoginKind.CustomOidc))
        {
            legacy.Id = "customOidc";
            legacy.Kind = LoginKind.CustomOidc;
            active.Add(legacy);
        }
        return active;
    }
}
