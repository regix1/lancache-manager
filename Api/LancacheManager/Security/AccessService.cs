using System.Security.Cryptography;
using System.Text;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly SignInEnvironment _environment;

    public AccessService(
        StateService stateService,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<AccessService>? logger = null)
    {
        _stateService = stateService;
        _configuration = configuration;
        _dbContextFactory = dbContextFactory;
        var log = (ILogger?)logger ?? NullLogger.Instance;
        _environment = new SignInEnvironment(configuration, log);
        if (_environment.Logins.Count == 0 && _environment.Mode is null)
        {
            return;
        }

        var access = _stateService.GetState().Access;
        foreach (var (loginId, login) in _environment.Logins)
        {
            try
            {
                var stored = access.Logins.FirstOrDefault(settings =>
                        string.Equals(settings.Id, loginId, StringComparison.Ordinal))
                    ?? (string.Equals(access.Oidc?.Id, loginId, StringComparison.Ordinal) ? access.Oidc : null);
                Validated(Merge(stored, login, _environment.Fields[loginId]));
            }
            catch (ValidationException ex)
            {
                log.LogError(
                    "The {LoginId} sign-in service set by Security__ environment variables is incomplete and is not used: {Reason}",
                    loginId,
                    ex.Message);
            }
        }
        if (_environment.Mode is AccountMode.Oidc or AccountMode.ApiKeyOidc && ActiveSettings(access).Count == 0)
        {
            log.LogError(
                "Security__SignInMode is {Mode} but no sign-in service is complete; running as ApiKeyPassword",
                _environment.Mode);
        }
    }

    public AccountMode GetMode()
        => GetMode(_stateService.GetState().Access);

    /// <summary>
    /// The account mode, the setup flag and whether <c>Security__SignInMode</c> decides the mode, all
    /// computed from one state read. The environment flag is false when that variable is unset, unknown,
    /// or an external sign-in mode that fell back to API key + password.
    /// </summary>
    public AccessModeState GetModeState()
    {
        var access = _stateService.GetState().Access;
        var mode = GetMode(access);
        return new AccessModeState(
            mode,
            IsSetupRequired(access),
            _environment.Mode is { } environmentMode && mode == environmentMode);
    }

    private AccountMode GetMode(AccessSettings access)
    {
        if (_environment.Mode is { } environmentMode)
        {
            // An external sign-in mode with no usable sign-in service would lock everyone out, and
            // turning sign-in off instead would open the server, so it runs as API key + password.
            return environmentMode is AccountMode.Oidc or AccountMode.ApiKeyOidc
                && ActiveSettings(access).Count == 0
                    ? AccountMode.ApiKeyPassword
                    : environmentMode;
        }

        if (access.Mode.HasValue)
        {
            return access.Mode.Value;
        }

        return _configuration.GetValue<bool>("Security:EnableAuthentication", true)
            ? AccountMode.ApiKeyPassword
            : AccountMode.Unauthenticated;
    }

    public bool IsSetupRequired()
        => IsSetupRequired(_stateService.GetState().Access);

    private bool IsSetupRequired(AccessSettings access)
    {
        var mode = GetMode(access);
        return !(_environment.Mode is { } environmentMode && mode == environmentMode
                && mode is AccountMode.Password or AccountMode.ApiKeyPassword or AccountMode.Unauthenticated)
            && access.SetupVersion < AccessSettings.RequiredSetupVersion;
    }

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
        => GetMode() is AccountMode.Unauthenticated or AccountMode.Oidc or AccountMode.ApiKeyOidc
            && account?.IsMainAdmin == true;

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
                DisplayName = settings.DisplayName,
                EnvironmentFields = _environment.Fields.TryGetValue(settings.Id, out var fields)
                    ? fields.Order().ToList()
                    : []
            })
            .ToList();

    /// <summary>
    /// The logins environment variables set that are not complete enough to use. The setup screen
    /// still shows their environment-set fields read-only, because a save fills those from the
    /// environment whatever the screen sends.
    /// </summary>
    public List<LoginServiceResponse> GetIncompleteLoginServices()
    {
        var active = ActiveSettings(_stateService.GetState().Access);
        return _environment.Logins
            .Where(entry => active.All(settings => !string.Equals(settings.Id, entry.Key, StringComparison.Ordinal)))
            .Select(entry => new LoginServiceResponse
            {
                Id = entry.Key,
                Kind = entry.Value.Kind,
                DisplayName = entry.Value.DisplayName,
                EnvironmentFields = _environment.Fields[entry.Key].Order().ToList()
            })
            .ToList();
    }

    public List<string> GetOwnerLoginServices()
        => ActiveSettings(_stateService.GetState().Access)
            .Where(settings => settings.OwnerSubject is not null)
            .Select(settings => settings.Id)
            .ToList();

    public OidcSettings? GetLoginSettings(string loginId, bool setup)
    {
        var access = _stateService.GetState().Access;
        if (setup)
        {
            if (access.PendingOidc is { } pending
                && string.Equals(pending.Id, loginId, StringComparison.Ordinal))
            {
                return _environment.Logins.TryGetValue(loginId, out var login)
                    ? Usable(Merge(pending, login, _environment.Fields[loginId]))
                    : pending.Copy();
            }

            // With no pending login, the access step signs the owner in through a login the
            // environment completes on its own.
            return _environment.Logins.ContainsKey(loginId)
                ? ActiveSettings(access)
                    .FirstOrDefault(settings => string.Equals(settings.Id, loginId, StringComparison.Ordinal))
                    ?.Copy()
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
            if (access.PendingOidc is { } pending)
            {
                return pending.Id;
            }

            var environmentLogins = ActiveSettings(access)
                .Where(settings => _environment.Logins.ContainsKey(settings.Id))
                .ToList();
            return environmentLogins.Count == 1 ? environmentLogins[0].Id : null;
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

    public bool CanDeferOidcAccount(string loginId, long revision)
    {
        var state = _stateService.GetState();
        if (state.SetupCompleted || !_configuration.GetValue<bool>("Runtime:DatabaseSetupPending"))
        {
            return false;
        }

        if (state.Access.PendingOidc is { } pending)
        {
            return pending.OwnerAccountId is null && pending.Revision == revision;
        }

        // With no pending login the owner signs in through a login the environment completes, and
        // that sign-in waits for the database the same way.
        return GetLoginSettings(loginId, setup: true) is { OwnerAccountId: null } login
            && login.Revision == revision;
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
        // With no complete login the server runs API key + password until a login completes the
        // environment mode, so only those two modes can be saved.
        if (_environment.Mode is { } environmentMode && request.Mode != environmentMode && request.Mode != GetMode())
        {
            throw new ValidationException("Sign-in mode is set by environment variable");
        }
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

            var loginId = LoginId(loginRequest.Kind);
            if (_environment.Logins.TryGetValue(loginId, out var login))
            {
                // Validated with the environment's values in place, stored without them: the
                // environment fills them again on every read, and its secrets never reach disk.
                var fields = _environment.Fields[loginId];
                pending = Validated(Merge(new OidcSettings
                {
                    Id = loginId,
                    Kind = loginRequest.Kind,
                    Authority = loginRequest.Authority ?? string.Empty,
                    ClientId = loginRequest.ClientId,
                    ClientSecret = loginRequest.ClientSecret,
                    DisplayName = loginRequest.DisplayName,
                    Tenant = loginRequest.Tenant,
                    TeamId = loginRequest.TeamId,
                    KeyId = loginRequest.KeyId,
                    PrivateKey = loginRequest.PrivateKey,
                    AllowedSubjects = loginRequest.AllowedSubjects ?? [],
                    OwnerAccountId = ownerAccountId
                }, login, fields));
                pending.OwnerIssuer = null;
                pending.OwnerSubject = null;
                foreach (var field in fields)
                {
                    switch (field)
                    {
                        case EnvironmentField.ClientId: pending.ClientId = string.Empty; break;
                        case EnvironmentField.ClientSecret: pending.ClientSecret = null; break;
                        case EnvironmentField.Authority: pending.Authority = string.Empty; break;
                        case EnvironmentField.DisplayName: pending.DisplayName = string.Empty; break;
                        case EnvironmentField.AllowedSubjects: pending.AllowedSubjects = []; break;
                        case EnvironmentField.Tenant: pending.Tenant = null; break;
                        case EnvironmentField.TeamId: pending.TeamId = null; break;
                        case EnvironmentField.KeyId: pending.KeyId = null; break;
                        case EnvironmentField.PrivateKey: pending.PrivateKey = null; break;
                    }
                }
            }
            else
            {
                pending = ValidateLogin(loginRequest, ownerAccountId);
            }
        }

        var requiresLoginTest = pending is not null;
        _stateService.UpdateAccess(access =>
        {
            // Counted under the same state lock as the mode write, so a login removed in another
            // request cannot leave an external sign-in mode with no login service.
            if (pending is null
                && request.Mode is AccountMode.Oidc or AccountMode.ApiKeyOidc
                && ActiveSettings(access).Count == 0)
            {
                throw new ValidationException("At least one tested login service is required for this account mode");
            }

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
                && ActiveSettings(access).FirstOrDefault(settings =>
                    string.Equals(settings.Id, loginId, StringComparison.Ordinal)
                    && _environment.Logins.ContainsKey(settings.Id)) is { } environmentLogin
                && environmentLogin.Revision == revision)
            {
                // The owner signed in through a login the environment completes. Only the owner
                // and the account links are stored; every setting stays in the environment.
                var stored = access.Logins.FirstOrDefault(settings =>
                    string.Equals(settings.Id, loginId, StringComparison.Ordinal));
                if (stored is null)
                {
                    stored = SecretFreeLogin(environmentLogin);
                    access.Logins.Add(stored);
                }
                stored.OwnerIssuer = issuer;
                stored.OwnerSubject = subject;
                stored.OwnerAccountId = ownerAccountId;
                access.Mode = access.PendingMode ?? _environment.Mode ?? AccountMode.Oidc;
                access.SetupVersion = AccessSettings.RequiredSetupVersion;
                access.PendingMode = null;
                promoted = true;
                return;
            }
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

    public void SetOidcOwnerAccount(string loginId, Guid accountId, long revision)
    {
        _stateService.UpdateAccess(access =>
        {
            var settings = access.Logins.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, loginId, StringComparison.Ordinal)
                && candidate.Revision == revision);
            if (settings is not null)
            {
                settings.OwnerAccountId = accountId;
            }
            if (access.Oidc is { } legacy
                && string.Equals(legacy.Id, loginId, StringComparison.Ordinal)
                && legacy.Revision == revision)
            {
                legacy.OwnerAccountId = accountId;
            }
        });
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
            if (settings is null
                && access.Logins.All(candidate => !string.Equals(candidate.Id, loginId, StringComparison.Ordinal))
                && ActiveSettings(access).FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, loginId, StringComparison.Ordinal)
                    && _environment.Logins.ContainsKey(candidate.Id)
                    && candidate.Revision == revision) is { } environmentLogin)
            {
                // The first sign-in through a login only the environment holds: store its account
                // links on a row that carries no setting.
                settings = SecretFreeLogin(environmentLogin);
                access.Logins.Add(settings);
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
            if (_environment.Fields.ContainsKey(loginId))
            {
                throw new ConflictException("This login service is set by environment variables");
            }

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
                // An environment login's settings did not change, so a sign-in already started
                // through it stays valid.
                if (_environment.Logins.ContainsKey(id))
                {
                    continue;
                }
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
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new ValidationException("Apple private key is not a valid EC private key");
        }
    }

    internal static string LoginId(LoginKind kind)
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

    private List<OidcSettings> ActiveSettings(AccessSettings access)
    {
        var stored = (access.Logins ?? []).ToList();
        if (access.Oidc is { } legacy
            && stored.All(settings => settings.Kind != LoginKind.CustomOidc))
        {
            legacy.Id = "customOidc";
            legacy.Kind = LoginKind.CustomOidc;
            stored.Add(legacy);
        }

        // A stored row is listed only while it is complete: one stripped of environment settings
        // stays out once the environment no longer fills them.
        var active = new List<OidcSettings>();
        foreach (var settings in stored)
        {
            var usable = _environment.Logins.TryGetValue(settings.Id, out var login)
                ? Usable(Merge(settings, login, _environment.Fields[settings.Id]))
                : Usable(settings) is null ? null : settings;
            if (usable is not null)
            {
                active.Add(usable);
            }
        }
        foreach (var (loginId, login) in _environment.Logins)
        {
            if (stored.All(settings => !string.Equals(settings.Id, loginId, StringComparison.Ordinal))
                && Usable(login.Copy()) is { } usable)
            {
                active.Add(usable);
            }
        }
        return active;
    }

    /// <summary>
    /// True when an environment login's owner subject or subject allow list covers the account,
    /// so deleting it would only let the next sign-in create it again.
    /// </summary>
    public bool IsAllowedByEnvironment(Guid accountId)
    {
        foreach (var settings in ActiveSettings(_stateService.GetState().Access))
        {
            if (!_environment.Fields.TryGetValue(settings.Id, out var fields))
            {
                continue;
            }

            var owner = fields.Contains(EnvironmentField.OwnerSubject);
            if (owner && settings.OwnerAccountId == accountId)
            {
                return true;
            }

            var issuer = settings.OwnerIssuer ?? DefaultIssuer(settings);
            var subjects = (fields.Contains(EnvironmentField.AllowedSubjects) ? settings.AllowedSubjects : [])
                .Concat(owner && settings.OwnerSubject is { } ownerSubject ? [ownerSubject] : []);
            if (subjects.Any(subject =>
                    settings.AccountIds.TryGetValue(IdentityKey(issuer, subject), out var linked)
                    && linked == accountId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Starts from the stored row (or the environment row when nothing is stored) and puts every
    /// environment-set field over it. A stored row keeps its own revision and account links.
    /// </summary>
    private static OidcSettings Merge(
        OidcSettings? stored,
        OidcSettings environment,
        IReadOnlySet<EnvironmentField> fields)
    {
        var merged = (stored ?? environment).Copy();
        foreach (var field in fields)
        {
            switch (field)
            {
                case EnvironmentField.ClientId: merged.ClientId = environment.ClientId; break;
                case EnvironmentField.ClientSecret: merged.ClientSecret = environment.ClientSecret; break;
                case EnvironmentField.Authority: merged.Authority = environment.Authority; break;
                case EnvironmentField.DisplayName: merged.DisplayName = environment.DisplayName; break;
                case EnvironmentField.AllowedSubjects: merged.AllowedSubjects = [.. environment.AllowedSubjects]; break;
                case EnvironmentField.OwnerSubject: merged.OwnerSubject = environment.OwnerSubject; break;
                case EnvironmentField.OwnerIssuer: merged.OwnerIssuer = environment.OwnerIssuer; break;
                case EnvironmentField.Tenant: merged.Tenant = environment.Tenant; break;
                case EnvironmentField.TeamId: merged.TeamId = environment.TeamId; break;
                case EnvironmentField.KeyId: merged.KeyId = environment.KeyId; break;
                case EnvironmentField.PrivateKey: merged.PrivateKey = environment.PrivateKey; break;
            }
        }
        return merged;
    }

    /// <summary>
    /// Runs a merged row through the same validation a saved login passes and returns the
    /// normalized row with the merged owner, account links and revision. Throws
    /// <see cref="ValidationException"/> when a required field is missing or invalid.
    /// </summary>
    private static OidcSettings Validated(OidcSettings merged)
    {
        var valid = ValidateLogin(
            new LoginSetupRequest
            {
                Kind = merged.Kind,
                ClientId = merged.ClientId,
                ClientSecret = merged.ClientSecret ?? string.Empty,
                DisplayName = merged.DisplayName,
                Tenant = merged.Tenant,
                Authority = merged.Authority,
                TeamId = merged.TeamId,
                KeyId = merged.KeyId,
                PrivateKey = merged.PrivateKey,
                AllowedSubjects = merged.AllowedSubjects
            },
            merged.OwnerAccountId);
        valid.OwnerSubject = merged.OwnerSubject;
        valid.OwnerIssuer = merged.OwnerIssuer
            ?? (merged.OwnerSubject is null ? null : DefaultIssuer(valid));
        valid.AccountIds = merged.AccountIds;
        valid.Revision = merged.Revision;
        return valid;
    }

    private static OidcSettings? Usable(OidcSettings merged)
    {
        try
        {
            return Validated(merged);
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The stored half of a login whose settings all come from the environment: its id, kind,
    /// revision and account links, and no secret.
    /// </summary>
    private static OidcSettings SecretFreeLogin(OidcSettings login)
        => new()
        {
            Id = login.Id,
            Kind = login.Kind,
            DisplayName = string.Empty,
            AccountIds = new Dictionary<string, Guid>(login.AccountIds, StringComparer.Ordinal),
            IdentityVersion = 1,
            Revision = login.Revision
        };

    /// <summary>
    /// The issuer an identity from this login carries when no owner issuer is set. GitHub has no
    /// authority; its sign-in handler uses this issuer.
    /// </summary>
    private static string DefaultIssuer(OidcSettings settings)
        => settings.Kind == LoginKind.GitHub ? GitHubEvents.Issuer : settings.Authority;
}

public sealed record AccessModeState(AccountMode Mode, bool SetupRequired, bool ModeFromEnvironment);
