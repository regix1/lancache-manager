using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Sign-in mode and sign-in services supplied through <c>Security__*</c> environment variables.
/// Each test hands the service an in-memory configuration in the shape ASP.NET builds from those
/// variables (<c>__</c> read as <c>:</c>), so no process variable is set.
/// </summary>
// Key rotation clears the process-wide shared sessions, so this class runs with the other classes
// that touch them.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SignInEnvironmentTests : IDisposable
{
    private const string EnvironmentSecret = "environment-secret-4f1c9a";
    private const string Authority = "https://login.example.test";
    private const string Password = "correct horse battery staple";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-sign-in-environment-{Guid.NewGuid():N}");

    public SignInEnvironmentTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp directory that will not delete is not worth failing a test over.
        }
    }

    [Fact]
    public void EnvironmentFieldsWinAndUnsetFieldsKeepTheSavedValue()
    {
        var state = CreateStateService(_root);
        var saved = new AccessService(state, Configuration(), dbContextFactory: null!);
        saved.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.CustomOidc,
            Authority = "https://saved.example.test",
            ClientId = "saved-client",
            ClientSecret = "saved-secret",
            DisplayName = "Saved name"
        }), null);
        var pending = Assert.IsType<OidcSettings>(saved.GetLoginSettings("customOidc", setup: true));
        Assert.True(saved.PromotePendingLogin("customOidc", pending.Revision, "https://saved.example.test", "owner"));

        var access = new AccessService(state, Configuration(
            ("Security:Oidc:ClientSecret", EnvironmentSecret),
            ("Security:Oidc:DisplayName", "From environment")), dbContextFactory: null!);

        var merged = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));
        Assert.Equal(EnvironmentSecret, merged.ClientSecret);
        Assert.Equal("From environment", merged.DisplayName);
        Assert.Equal("saved-client", merged.ClientId);
        Assert.Equal("https://saved.example.test", merged.Authority);
        Assert.Equal(pending.Revision, merged.Revision);
        Assert.Equal(
            [EnvironmentField.ClientSecret, EnvironmentField.DisplayName],
            Assert.Single(access.GetLoginServices()).EnvironmentFields);

        // A save from the settings screen leaves the environment fields out of its body; the
        // environment values stay in effect and are not stored.
        access.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.CustomOidc,
            Authority = "https://saved.example.test",
            ClientId = "edited-client"
        }), null);
        var edited = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.Equal(EnvironmentSecret, edited.ClientSecret);
        Assert.Equal("edited-client", edited.ClientId);
        Assert.Null(CreateStateService(_root).GetState().Access.PendingOidc?.ClientSecret);
    }

    [Fact]
    public void EnvironmentSecretsNeverReachTheStateFile()
    {
        var state = CreateStateService(_root);
        var access = new AccessService(state, CompleteOidc(("Security:Oidc:AllowedSubjects", "member")), dbContextFactory: null!);

        // First sign-in through the login the environment alone holds.
        var active = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));
        Assert.NotEqual(Guid.Empty, access.GetOrCreateAccountId("customOidc", Authority, "member", active.Revision));

        // A save, a test sign-in and its promotion.
        access.Apply(SsoRequest(new LoginSetupRequest { Kind = LoginKind.CustomOidc, DisplayName = "Mine" }), null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.Equal(EnvironmentSecret, pending.ClientSecret);
        Assert.True(access.PromotePendingLogin("customOidc", pending.Revision, Authority, "owner"));
        Assert.NotEqual(Guid.Empty, access.GetOrCreateAccountId("customOidc", Authority, "member", pending.Revision));

        AssertNoStoredSecret();
        Assert.Equal(EnvironmentSecret, access.GetLoginSettings("customOidc", setup: false)?.ClientSecret);
    }

    [Fact]
    public void IncompleteExternalModeRunsAsApiKeyAndPassword()
    {
        var logger = new CapturingLogger<AccessService>();
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(
                ("Security:SignInMode", "Oidc"),
                ("Security:Oidc:Authority", Authority),
                ("Security:Oidc:ClientSecret", EnvironmentSecret)),
            dbContextFactory: null!,
            logger);

        Assert.Equal(AccountMode.ApiKeyPassword, access.GetMode());
        Assert.Equal(new AccessModeState(AccountMode.ApiKeyPassword, true, false), access.GetModeState());
        Assert.True(access.IsAuthenticationEnabled());
        Assert.True(access.IsSetupRequired());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Security__SignInMode", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Client ID is required", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(EnvironmentSecret, StringComparison.Ordinal));

        access.Apply(new AccessSetupRequest { Mode = AccountMode.ApiKeyPassword, ApiKey = "checked-by-controller" }, null);
        Assert.Equal(AccountMode.ApiKeyPassword, access.GetMode());
        Assert.False(access.IsSetupRequired());
    }

    [Fact]
    public void IncompleteExternalModeRefusesAModeTheServerWouldNotRun()
    {
        var configuration = Configuration(
            ("Security:SignInMode", "Oidc"),
            ("Security:Oidc:Authority", Authority),
            ("Security:Oidc:ClientSecret", EnvironmentSecret));
        var access = new AccessService(CreateStateService(_root), configuration, dbContextFactory: null!);

        Assert.Throws<ValidationException>(() => access.Apply(
            new AccessSetupRequest { Mode = AccountMode.Password, ApiKey = "checked-by-controller" },
            null));
        Assert.Equal(AccountMode.ApiKeyPassword, access.GetMode());
        Assert.True(access.IsSetupRequired());
        var restarted = new AccessService(CreateStateService(_root), configuration, dbContextFactory: null!);
        Assert.Equal(AccountMode.ApiKeyPassword, restarted.GetMode());
        Assert.True(restarted.IsSetupRequired());

        access.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.CustomOidc,
            Authority = Authority,
            ClientId = "wizard-client"
        }), null);
        Assert.Equal("wizard-client", access.GetLoginSettings("customOidc", setup: true)?.ClientId);
    }

    [Fact]
    public void AppleKeyWithNoPemBlockIsLoggedAndNotUsed()
    {
        var logger = new CapturingLogger<AccessService>();
        var configuration = Configuration(
            ("Security:SignInMode", "Oidc"),
            ("Security:Apple:ClientId", "com.example.service"),
            ("Security:Apple:TeamId", "TEAM123456"),
            ("Security:Apple:KeyId", "KEY1234567"),
            ("Security:Apple:PrivateKey", "not a pem"));
        AccessService? access = null;

        Assert.Null(Record.Exception(() => access = new AccessService(CreateStateService(_root), configuration, dbContextFactory: null!, logger)));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("apple", StringComparison.Ordinal));
        Assert.Null(access!.GetLoginSettings("apple", setup: false));
        Assert.Equal(AccountMode.ApiKeyPassword, access.GetMode());
        Assert.Throws<ValidationException>(() => access.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.Apple,
            ClientId = "com.example.service",
            TeamId = "TEAM123456",
            KeyId = "KEY1234567",
            PrivateKey = "not a pem"
        }), null));
    }

    [Theory]
    [InlineData("Password", AccountMode.Password)]
    [InlineData("ApiKeyPassword", AccountMode.ApiKeyPassword)]
    public void PasswordModeFromTheEnvironmentSkipsTheAccessStep(string value, AccountMode mode)
    {
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(("Security:SignInMode", value)),
            dbContextFactory: null!);

        Assert.Equal(mode, access.GetMode());
        Assert.True(access.GetModeState().ModeFromEnvironment);
        Assert.False(access.IsSetupRequired());
        Assert.Throws<ValidationException>(() => access.Apply(
            new AccessSetupRequest { Mode = AccountMode.Unauthenticated, ApiKey = "x", AcknowledgeUnauthenticated = true },
            null));
        access.Apply(new AccessSetupRequest { Mode = mode, ApiKey = "x" }, null);
        Assert.Equal(mode, access.GetMode());
    }

    [Theory]
    [InlineData("Sso", AccountMode.Oidc)]
    [InlineData("sso", AccountMode.Oidc)]
    [InlineData("ApiKeyAndSso", AccountMode.ApiKeyOidc)]
    [InlineData("apikeyoidc", AccountMode.ApiKeyOidc)]
    [InlineData("Unauthenticated", AccountMode.Unauthenticated)]
    [InlineData("open", null)]
    [InlineData("1", null)]
    public void ModeNamesAndAliasesAreRead(string value, AccountMode? mode)
    {
        Assert.Equal(mode, SignInEnvironment.ReadMode(Configuration(("Security:SignInMode", value))));
    }

    [Fact]
    public void UnauthenticatedFromTheEnvironmentWarnsAtStart()
    {
        var logger = new CapturingLogger<AccessService>();
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(("Security:SignInMode", "Unauthenticated")),
            dbContextFactory: null!,
            logger);

        Assert.Equal(AccountMode.Unauthenticated, access.GetMode());
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Security__SignInMode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OwnerStartsSetupSignInThroughTheEnvironmentLogin()
    {
        var configuration = CompleteOidc(
            ("Security:SignInMode", "Oidc"),
            ("Security:ApiKeyPath", Path.Combine(_root, "setup-api-key.txt")),
            ("Runtime:DatabaseSetupPending", "true"));
        var access = new AccessService(CreateStateService(_root), configuration, dbContextFactory: null!);

        Assert.Equal(AccountMode.Oidc, access.GetMode());
        Assert.Equal(new AccessModeState(AccountMode.Oidc, true, true), access.GetModeState());
        Assert.True(access.IsSetupRequired());
        Assert.Equal("customOidc", access.GetCompatibleLoginId(setup: true));
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.Equal(0, settings.Revision);
        Assert.True(access.IdentityAllowed("customOidc", settings.Revision, setup: true, ownerOnly: false, Authority, "anyone"));

        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var controller = new AccessController(
            access,
            apiKey,
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Scheme = Uri.UriSchemeHttp;
        controller.Request.Host = new HostString("localhost");

        var started = await controller.StartOidcAsync(new OidcStartRequest { Setup = true, ApiKey = apiKey.GetApiKey() });

        Assert.IsType<OkObjectResult>(started.Result);
    }

    [Fact]
    public async Task EnvironmentOnlySetupCompletesWithTheOwnerAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = CompleteOidc(
            ("Security:SignInMode", "Oidc"),
            ("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var state = CreateStateService(_root);
        var access = new AccessService(state, configuration, database.Factory);
        var signIns = NewSignInService(access, database.Factory, state, configuration);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));

        var signIn = await signIns.TryCreateSessionAsync(settings, owner: true, Authority, "owner-subject", new DefaultHttpContext());
        Assert.NotNull(signIn);
        Assert.True(await signIns.CompleteSetupAsync(
            signIn,
            () => access.PromotePendingLogin("customOidc", settings.Revision, Authority, "owner-subject", signIn.AccountId)));

        Assert.False(access.IsSetupRequired());
        Assert.Equal(AccountMode.Oidc, access.GetMode());
        await using (var context = database.Factory.CreateDbContext())
        {
            Assert.Equal(signIn.AccountId, (await context.UserAccounts.SingleAsync(account => account.IsMainAdmin)).Id);
        }
        var stored = Assert.Single(CreateStateService(_root).GetState().Access.Logins);
        Assert.Equal("owner-subject", stored.OwnerSubject);
        Assert.Equal(signIn.AccountId, stored.OwnerAccountId);
        Assert.Equal(["customOidc"], access.GetOwnerLoginServices());
        AssertNoStoredSecret();
    }

    [Theory]
    [InlineData(LoginKind.CustomOidc)]
    [InlineData(LoginKind.GitHub)]
    public async Task EnvironmentOnlySetupSignInWaitsForTheDatabase(LoginKind kind)
    {
        var github = kind == LoginKind.GitHub;
        var loginId = github ? "github" : "customOidc";
        var configuration = github
            ? Configuration(
                ("Security:SignInMode", "Oidc"),
                ("Security:GitHub:ClientId", "github-client"),
                ("Security:GitHub:ClientSecret", EnvironmentSecret),
                ("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")),
                ("Runtime:DatabaseSetupPending", "true"))
            : CompleteOidc(
                ("Security:SignInMode", "Oidc"),
                ("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")),
                ("Runtime:DatabaseSetupPending", "true"));
        var state = CreateStateService(_root);
        // No database exists yet, so any attempt to open one fails the callback.
        var access = new AccessService(state, configuration, dbContextFactory: null!);
        var signIns = NewSignInService(access, null!, state, configuration);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings(loginId, setup: true));
        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = settings.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["login_id"] = loginId;
        const string subject = "environment-owner";
        var scheme = new AuthenticationScheme(
            AccessService.GetScheme(kind, setup: true),
            AccessService.GetScheme(kind, setup: true),
            github ? typeof(OAuthHandler<OAuthOptions>) : typeof(OpenIdConnectHandler));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)],
            scheme.Name));
        var httpContext = new DefaultHttpContext();

        Exception? error;
        if (github)
        {
            var context = new TicketReceivedContext(
                httpContext,
                scheme,
                new OAuthOptions(),
                new AuthenticationTicket(principal, properties, scheme.Name));
            var events = new GitHubEvents(
                access,
                signIns,
                new OptionsCache<OAuthOptions>(),
                TimeProvider.System,
                NullLogger<GitHubEvents>.Instance);
            error = await Record.ExceptionAsync(() => events.TicketReceived(context));
        }
        else
        {
            properties.Items["oidc_issuer"] = Authority;
            properties.Items["oidc_subject"] = subject;
            var context = new TicketReceivedContext(
                httpContext,
                scheme,
                new OpenIdConnectOptions(),
                new AuthenticationTicket(principal, properties, scheme.Name));
            var events = new OidcEvents(
                access,
                signIns,
                new OptionsCache<OpenIdConnectOptions>(),
                TimeProvider.System,
                NullLogger<OidcEvents>.Instance);
            error = await Record.ExceptionAsync(() => events.TicketReceived(context));
        }

        Assert.Null(error);
        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Contains("loginTest=success", httpContext.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.False(access.IsSetupRequired());
        Assert.Equal([loginId], access.GetOwnerLoginServices());
        var stored = Assert.Single(CreateStateService(_root).GetState().Access.Logins);
        Assert.Equal(subject, stored.OwnerSubject);
        Assert.Null(stored.OwnerAccountId);
        AssertNoStoredSecret();
    }

    [Fact]
    public void RemovingAnEnvironmentLoginIsRefusedEvenWhenItIsTheLast()
    {
        var access = new AccessService(
            CreateStateService(_root),
            CompleteOidc(("Security:SignInMode", "Oidc")),
            dbContextFactory: null!);

        var refusal = Assert.Throws<ConflictException>(() => access.RemoveLogin("customOidc"));

        Assert.Contains("environment variables", refusal.Message, StringComparison.Ordinal);
        Assert.Single(access.GetLoginServices());
    }

    [Fact]
    public void IncompleteEnvironmentLoginIsListedWithItsEnvironmentFieldsOnly()
    {
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(("Security:Google:ClientId", "from-env")),
            dbContextFactory: null!);

        Assert.Empty(access.GetLoginServices());
        var incomplete = Assert.Single(access.GetIncompleteLoginServices());
        Assert.Equal("google", incomplete.Id);
        Assert.Equal(LoginKind.Google, incomplete.Kind);
        Assert.Equal([EnvironmentField.ClientId], incomplete.EnvironmentFields);

        // The setup screen leaves the environment-set field out of the save, and the save fills it.
        access.Apply(SsoRequest(new LoginSetupRequest { Kind = LoginKind.Google, ClientSecret = "typed-secret" }), null);
        Assert.Equal("from-env", access.GetLoginSettings("google", setup: true)?.ClientId);
    }

    [Fact]
    public void CompleteEnvironmentLoginIsNotListedAsIncomplete()
    {
        var access = new AccessService(CreateStateService(_root), CompleteOidc(), dbContextFactory: null!);

        Assert.Single(access.GetLoginServices());
        Assert.Empty(access.GetIncompleteLoginServices());
    }

    [Fact]
    public void ClientSecretFileIsReadAndTrimmed()
    {
        var secretFile = Path.Combine(_root, "client-secret");
        File.WriteAllText(secretFile, $"  {EnvironmentSecret}\n");
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(
                ("Security:Oidc:Authority", Authority),
                ("Security:Oidc:ClientId", "environment-client"),
                ("Security:Oidc:ClientSecretFile", secretFile)),
            dbContextFactory: null!);

        Assert.Equal(EnvironmentSecret, access.GetLoginSettings("customOidc", setup: false)?.ClientSecret);
        Assert.Contains(EnvironmentField.ClientSecret, Assert.Single(access.GetLoginServices()).EnvironmentFields);
    }

    [Fact]
    public void UnreadableSecretFileLogsTheVariableAndNotTheSecret()
    {
        var logger = new CapturingLogger<AccessService>();
        var access = new AccessService(
            CreateStateService(_root),
            Configuration(
                ("Security:Oidc:Authority", Authority),
                ("Security:Oidc:ClientId", "environment-client"),
                ("Security:Oidc:ClientSecret", EnvironmentSecret),
                ("Security:Oidc:ClientSecretFile", Path.Combine(_root, "missing", "client-secret"))),
            dbContextFactory: null!,
            logger);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Message.Contains("Security__Oidc__ClientSecretFile", StringComparison.Ordinal));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("Security__Oidc__ClientSecret and", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(EnvironmentSecret, StringComparison.Ordinal));
        Assert.Equal(EnvironmentSecret, access.GetLoginSettings("customOidc", setup: false)?.ClientSecret);
    }

    [Fact]
    public void EnvironmentLoginSignInSurvivesAnUnrelatedSave()
    {
        var access = new AccessService(
            CreateStateService(_root),
            CompleteOidc(("Security:Oidc:AllowedSubjects", "member")),
            dbContextFactory: null!);
        var challengeRevision = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false)).Revision;

        access.Apply(new AccessSetupRequest { Mode = AccountMode.ApiKeyOidc, ApiKey = "x" }, null);

        Assert.Equal(challengeRevision, access.GetLoginSettings("customOidc", setup: false)?.Revision);
        Assert.True(access.IdentityAllowed("customOidc", challengeRevision, setup: false, ownerOnly: false, Authority, "member"));
        Assert.NotEqual(Guid.Empty, access.GetOrCreateAccountId("customOidc", Authority, "member", challengeRevision));
        Assert.Contains(CreateStateService(_root).GetState().Access.Logins, settings => settings.Id == "customOidc");
    }

    [Fact]
    public void StrippedRowIsNotListedOnceTheEnvironmentIsRemoved()
    {
        var state = CreateStateService(_root);
        var access = new AccessService(state, CompleteOidc(("Security:Oidc:AllowedSubjects", "member")), dbContextFactory: null!);
        access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
        Assert.Single(state.GetState().Access.Logins);

        var withoutEnvironment = new AccessService(state, Configuration(), dbContextFactory: null!);

        Assert.Empty(withoutEnvironment.GetLoginServices());
        Assert.Null(withoutEnvironment.GetLoginSettings("customOidc", setup: false));
    }

    [Fact]
    public void OwnerIsSetOnlyOnTheLoginItSignedInThrough()
    {
        var state = CreateStateService(_root);
        var access = new AccessService(state, CompleteOidc(
            ("Security:Oidc:AllowedSubjects", "member"),
            ("Security:Google:ClientId", "google-client"),
            ("Security:Google:ClientSecret", "google-secret"),
            ("Security:Google:AllowedSubjects", "member")), dbContextFactory: null!);
        access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
        access.GetOrCreateAccountId("google", "https://accounts.google.com", "member", 0);
        var owner = Guid.NewGuid();

        access.SetOidcOwnerAccount("google", owner, 0);

        var logins = state.GetState().Access.Logins;
        Assert.Equal(owner, logins.Single(settings => settings.Id == "google").OwnerAccountId);
        Assert.Null(logins.Single(settings => settings.Id == "customOidc").OwnerAccountId);
    }

    [Fact]
    public void ForgettingAnotherAccountKeepsTheEnvironmentLoginRevision()
    {
        var state = CreateStateService(_root);
        var access = new AccessService(state, CompleteOidc(("Security:Oidc:AllowedSubjects", "first,second")), dbContextFactory: null!);
        var challengeRevision = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false)).Revision;
        var first = access.GetOrCreateAccountId("customOidc", Authority, "first", challengeRevision);
        var second = access.GetOrCreateAccountId("customOidc", Authority, "second", challengeRevision);
        var storedRevision = state.GetState().Access.Logins.Single().Revision;

        access.ForgetAccounts([second]);

        var stored = state.GetState().Access.Logins.Single();
        Assert.Equal(storedRevision, stored.Revision);
        Assert.DoesNotContain(second, stored.AccountIds.Values);
        Assert.True(access.IdentityAllowed("customOidc", challengeRevision, setup: false, ownerOnly: false, Authority, "first"));
        access.SetOidcOwnerAccount("customOidc", first, challengeRevision);
        Assert.Equal(first, state.GetState().Access.Logins.Single().OwnerAccountId);
    }

    [Fact]
    public void EnvironmentAllowListAndOwnerCoverTheirAccounts()
    {
        var access = new AccessService(CreateStateService(_root), CompleteOidc(
            ("Security:Oidc:AllowedSubjects", "member"),
            ("Security:Oidc:OwnerSubject", "boss")), dbContextFactory: null!);
        var member = access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
        var stranger = access.GetOrCreateAccountId("customOidc", Authority, "stranger", 0);
        var owner = Guid.NewGuid();
        access.SetOidcOwnerAccount("customOidc", owner, 0);

        Assert.True(access.IsAllowedByEnvironment(member));
        Assert.True(access.IsAllowedByEnvironment(owner));
        Assert.False(access.IsAllowedByEnvironment(stranger));
        Assert.False(access.IsAllowedByEnvironment(Guid.NewGuid()));
        Assert.Equal(Authority, access.GetLoginSettings("customOidc", setup: false)?.OwnerIssuer);
    }

    [Fact]
    public void ExternalModeSaveAfterTheLastLoginIsRemovedIsRefused()
    {
        var state = CreateStateService(_root);
        var access = new AccessService(state, Configuration(), dbContextFactory: null!);
        access.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.CustomOidc,
            Authority = Authority,
            ClientId = "client",
            ClientSecret = "secret"
        }), null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.True(access.PromotePendingLogin("customOidc", pending.Revision, Authority, "owner"));
        access.Apply(new AccessSetupRequest { Mode = AccountMode.Password, ApiKey = "x" }, null);
        Assert.True(access.RemoveLogin("customOidc"));

        Assert.Throws<ValidationException>(() =>
            access.Apply(new AccessSetupRequest { Mode = AccountMode.Oidc, ApiKey = "x" }, null));

        Assert.Equal(AccountMode.Password, state.GetState().Access.Mode);
    }

    [Fact]
    public void EnvironmentFieldNamesTravelInCamelCase()
    {
        Assert.Equal("\"clientId\"", JsonSerializer.Serialize(EnvironmentField.ClientId));
        var json = JsonSerializer.Serialize(
            new LoginServiceResponse
            {
                Id = "customOidc",
                Kind = LoginKind.CustomOidc,
                DisplayName = "OpenID Connect",
                EnvironmentFields = [EnvironmentField.ClientId, EnvironmentField.ClientSecret]
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"environmentFields\":[\"clientId\",\"clientSecret\"]", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AccountMode.Oidc, false, StatusCodes.Status200OK)]
    [InlineData(AccountMode.ApiKeyOidc, false, StatusCodes.Status401Unauthorized)]
    [InlineData(AccountMode.ApiKeyOidc, true, StatusCodes.Status200OK)]
    public async Task OwnerScriptSignsInWithAPasswordInExternalModes(AccountMode mode, bool sendKey, int expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, apiKey) = await NewPasswordSignInAsync(database, mode);

        var result = await controller.LoginAsync(new LoginRequest
        {
            Username = "owner",
            Password = Password,
            ApiKey = sendKey ? apiKey.GetApiKey() : string.Empty
        });

        Assert.Equal(expected, StatusOf(result.Result));
    }

    [Fact]
    public async Task OtherAccountsCannotSignInWithAPasswordInExternalModes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var (controller, apiKey) = await NewPasswordSignInAsync(database, AccountMode.Oidc);

        var result = await controller.LoginAsync(new LoginRequest
        {
            Username = "member",
            Password = Password,
            ApiKey = apiKey.GetApiKey()
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusOf(result.Result));
    }

    [Fact]
    public async Task DeletingAnEnvironmentAllowedAccountIsRefused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = CompleteOidc(
            ("Security:Oidc:AllowedSubjects", "member"),
            ("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var access = new AccessService(CreateStateService(_root), configuration, database.Factory);
        var member = access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
        var owner = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(AccountResetTests.Account(owner, true), AccountResetTests.Account(member));
            await context.SaveChangesAsync();
        }
        var controller = NewAccountsController(database.Factory, configuration, access, owner);

        var deleted = await controller.DeleteAccountAsync(member);
        var wiped = await controller.WipeAccountsAsync();

        foreach (var result in new[] { deleted.Result, wiped.Result })
        {
            var refusal = Assert.IsType<ObjectResult>(result);
            Assert.Equal(StatusCodes.Status409Conflict, refusal.StatusCode);
            Assert.Equal(
                AccountRefusalResponse.EnvironmentManaged,
                Assert.IsType<AccountRefusalResponse>(refusal.Value).StageKey);
        }
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(2, await check.UserAccounts.CountAsync());
        Assert.Contains(member, access.GetLoginSettings("customOidc", setup: false)!.AccountIds.Values);
    }

    [Fact]
    public async Task KeyRotationKeepsEnvironmentAllowedAccounts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var rotation = new RotationHost(_root, CompleteOidc(("Security:Oidc:AllowedSubjects", "member")), database.Factory);
        var member = rotation.Access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
        var other = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(AccountResetTests.Account(member), AccountResetTests.Account(other));
            await context.SaveChangesAsync();
        }

        await rotation.RotateAsOwnerAsync(database.Factory);

        await using var check = database.Factory.CreateDbContext();
        Assert.True(await check.UserAccounts.AnyAsync(account => account.Id == member));
        Assert.False(await check.UserAccounts.AnyAsync(account => account.Id == other));
        Assert.Contains(member, rotation.Access.GetLoginSettings("customOidc", setup: false)!.AccountIds.Values);
    }

    [Fact]
    public async Task FailedRotationSaveLeavesEverySignInLink()
    {
        await using var database = await TestDatabase.CreateAsync();
        var rotation = new RotationHost(_root, Configuration(), database.Factory);
        rotation.Access.Apply(SsoRequest(new LoginSetupRequest
        {
            Kind = LoginKind.CustomOidc,
            Authority = Authority,
            ClientId = "client",
            ClientSecret = "secret",
            AllowedSubjects = ["member"]
        }), null);
        var pending = Assert.IsType<OidcSettings>(rotation.Access.GetLoginSettings("customOidc", setup: true));
        Assert.True(rotation.Access.PromotePendingLogin("customOidc", pending.Revision, Authority, "owner"));
        var member = rotation.Access.GetOrCreateAccountId("customOidc", Authority, "member", pending.Revision);
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(AccountResetTests.Account(member));
            await context.SaveChangesAsync();
        }
        var failing = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(new AccountDeleteFails())
            .Options);

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => rotation.RotateAsOwnerAsync(failing));

        Assert.Contains(member, rotation.State.GetState().Access.Logins.Single().AccountIds.Values);
        await using var check = database.Factory.CreateDbContext();
        Assert.True(await check.UserAccounts.AnyAsync(account => account.Id == member));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AccountResetKeepsEnvironmentAllowedAccounts(bool resumed)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var files = new IntegrationFixture();
        var primary = Guid.NewGuid();
        var member = files.Owner.AccountId!.Value;
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(AccountResetTests.Account(primary, true), AccountResetTests.Account(member));
            context.UserSessions.Add(AccountResetTests.Session(member, "live"));
            if (resumed)
            {
                // An unfinished reset stored before the environment allowed the account.
                context.AccountResets.Add(new AccountReset
                {
                    Id = 1,
                    PrimaryAccountId = primary,
                    AccountIds = [member],
                    StartedAtUtc = DateTime.UtcNow
                });
            }
            await context.SaveChangesAsync();
        }
        files.Seed();
        files.Epic.SaveAuthData(new EpicAuthData { OwnerAccountId = member, RefreshToken = "epic", AccountId = "epic-account" });
        files.Xbox.SaveAuthData(new XboxAuthData { OwnerAccountId = member, RefreshToken = "xbox", DeviceKeyPkcs8 = "device-key" });
        files.State.UpdateAccess(access => access.Logins =
        [
            new OidcSettings
            {
                Id = "customOidc",
                DisplayName = string.Empty,
                IdentityVersion = 1,
                AccountIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
                {
                    [AccessService.IdentityKey(Authority, "member")] = member
                }
            }
        ]);

        await using (var context = new AppDbContext(database.Options))
        {
            using var services = new ServiceCollection()
                .AddSingleton<SteamAuthStorageService>(files.Storage).AddSingleton(files.Epic).AddSingleton(files.Xbox)
                .AddSingleton(new AccessService(
                    files.State,
                    CompleteOidc(("Security:Oidc:AllowedSubjects", "member")),
                    database.Factory))
                .BuildServiceProvider();
            await new AccountResetService(context, services, NullLogger<AccountResetService>.Instance).RunAsync();
        }

        await using var check = database.Factory.CreateDbContext();
        Assert.True(await check.UserAccounts.AnyAsync(account => account.Id == member));
        Assert.True(await check.UserSessions.AnyAsync(session => session.AccountId == member));
        Assert.NotNull((await check.AccountResets.SingleAsync()).CompletedAtUtc);
        Assert.True(File.Exists(files.SavedPath(member)));
        Assert.True(File.Exists(files.Epic.GetCredentialsFilePath()));
        Assert.True(File.Exists(files.Xbox.GetCredentialsFilePath()));
        Assert.Contains(member, files.State.GetState().Access.Logins.Single().AccountIds.Values);
    }

    [Fact]
    public async Task TwoFirstSignInsForOneIdentityShareOneAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = CompleteOidc(
            ("Security:Oidc:AllowedSubjects", "newcomer"),
            ("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var state = CreateStateService(_root);
        var access = new AccessService(state, configuration, database.Factory);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));
        var accountId = access.GetOrCreateAccountId("customOidc", Authority, "newcomer", settings.Revision);
        // The other callback's account row lands between this one's read and its save.
        var racing = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options)
            .AddInterceptors(new AccountArrivesFirst(database.Options, accountId))
            .Options);
        var signIns = new ExternalSignInService(access, racing, NewSessions(database.Factory, state, configuration));

        var signIn = await signIns.TryCreateSessionAsync(settings, owner: false, Authority, "newcomer", new DefaultHttpContext());

        Assert.NotNull(signIn);
        Assert.Equal(accountId, signIn.AccountId);
        Assert.False(signIn.AccountCreated);
        await using var check = database.Factory.CreateDbContext();
        Assert.Equal(accountId, (await check.UserAccounts.SingleAsync()).Id);
    }

    [Fact]
    public async Task ForgettingAnotherAccountKeepsAStoredLoginSignIn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var state = CreateStateService(_root);
        var access = StoredLogin(state, configuration, database.Factory);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));
        var second = access.GetOrCreateAccountId("customOidc", Authority, "second", settings.Revision);

        access.ForgetAccounts([second]);

        Assert.NotEqual(settings.Revision, access.GetLoginSettings("customOidc", setup: false)?.Revision);
        var signIn = await NewSignInService(access, database.Factory, state, configuration)
            .TryCreateSessionAsync(settings, owner: false, Authority, "first", new DefaultHttpContext());
        Assert.NotNull(signIn);
        Assert.Contains(signIn.AccountId, state.GetState().Access.Logins.Single().AccountIds.Values);
    }

    [Fact]
    public async Task ChangedClientIdStillEndsAnOpenSignIn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var state = CreateStateService(_root);
        var access = StoredLogin(state, configuration, database.Factory);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));

        access.Apply(SsoRequest(StoredLoginRequest("other-client")), null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.True(access.PromotePendingLogin("customOidc", pending.Revision, Authority, "owner"));

        await Assert.ThrowsAsync<ValidationException>(() => NewSignInService(access, database.Factory, state, configuration)
            .TryCreateSessionAsync(settings, owner: false, Authority, "first", new DefaultHttpContext()));
    }

    [Fact]
    public async Task ForgottenIdentityStillCannotFinishAnOpenSignIn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var state = CreateStateService(_root);
        var access = StoredLogin(state, configuration, database.Factory);
        var settings = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: false));
        var first = access.GetOrCreateAccountId("customOidc", Authority, "first", settings.Revision);

        access.ForgetAccounts([first]);

        await Assert.ThrowsAsync<ValidationException>(() => NewSignInService(access, database.Factory, state, configuration)
            .TryCreateSessionAsync(settings, owner: false, Authority, "first", new DefaultHttpContext()));
    }

    [Fact]
    public async Task AnonymousStatusAfterSetupHidesTheEnvironmentMarkers()
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Security__SignInMode"] = "ApiKeyPassword",
            ["Security__Oidc__Authority"] = Authority,
            ["Security__Oidc__ClientId"] = "environment-client",
            ["Security__Oidc__ClientSecret"] = EnvironmentSecret,
            // A Google login with only its client ID set stays incomplete.
            ["Security__Google__ClientId"] = "environment-google-client"
        };
        var original = variables.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var (name, value) in variables)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            using var host = new EndpointAuthorizationHost();
            using var anonymous = host.Application.CreateClient();
            using var admin = await host.CreateAdminClientAsync();

            var anonymousStatus = await anonymous.GetFromJsonAsync<JsonElement>("/api/auth/status");
            var adminStatus = await admin.GetFromJsonAsync<JsonElement>("/api/auth/status");

            Assert.False(anonymousStatus.GetProperty("authenticationSetupRequired").GetBoolean());
            Assert.False(anonymousStatus.GetProperty("signInModeFromEnvironment").GetBoolean());
            var anonymousLogins = anonymousStatus.GetProperty("loginServices").EnumerateArray().ToList();
            Assert.Contains(anonymousLogins, login => login.GetProperty("id").GetString() == "customOidc");
            Assert.All(anonymousLogins, login => Assert.Empty(login.GetProperty("environmentFields").EnumerateArray()));
            Assert.Empty(anonymousStatus.GetProperty("incompleteLoginServices").EnumerateArray());
            Assert.True(adminStatus.GetProperty("signInModeFromEnvironment").GetBoolean());
            Assert.Contains(adminStatus.GetProperty("incompleteLoginServices").EnumerateArray(), login =>
                login.GetProperty("id").GetString() == "google");
            Assert.Contains(adminStatus.GetProperty("loginServices").EnumerateArray(), login =>
                login.GetProperty("environmentFields").EnumerateArray().Any(field => field.GetString() == "clientId"));
        }
        finally
        {
            foreach (var (name, value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private AccessService StoredLogin(
        StateService state,
        IConfiguration configuration,
        IDbContextFactory<AppDbContext> contexts)
    {
        var access = new AccessService(state, configuration, contexts);
        access.Apply(SsoRequest(StoredLoginRequest("stored-client")), null);
        var pending = Assert.IsType<OidcSettings>(access.GetLoginSettings("customOidc", setup: true));
        Assert.True(access.PromotePendingLogin("customOidc", pending.Revision, Authority, "owner"));
        return access;
    }

    private static LoginSetupRequest StoredLoginRequest(string clientId) => new()
    {
        Kind = LoginKind.CustomOidc,
        Authority = Authority,
        ClientId = clientId,
        ClientSecret = "stored-secret",
        AllowedSubjects = ["first", "second"]
    };

    private IConfiguration Configuration(params (string Key, string? Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(value => value.Key, value => value.Value))
            .Build();

    private IConfiguration CompleteOidc(params (string Key, string? Value)[] values)
        => Configuration(
        [
            ("Security:Oidc:Authority", Authority),
            ("Security:Oidc:ClientId", "environment-client"),
            ("Security:Oidc:ClientSecret", EnvironmentSecret),
            .. values
        ]);

    private static AccessSetupRequest SsoRequest(LoginSetupRequest login) => new()
    {
        Mode = AccountMode.Oidc,
        ApiKey = "checked-by-controller",
        Login = login
    };

    private void AssertNoStoredSecret()
    {
        var access = CreateStateService(_root).GetState().Access;
        Assert.All(
            access.Logins.Concat(access.Oidc is null ? [] : [access.Oidc]).Concat(access.PendingOidc is null ? [] : [access.PendingOidc]),
            settings => Assert.NotEqual(EnvironmentSecret, settings.ClientSecret));
        foreach (var file in Directory.EnumerateFiles(_root, "state.json", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(EnvironmentSecret, File.ReadAllText(file), StringComparison.Ordinal);
        }
    }

    private static int? StatusOf(IActionResult? result) => (result as IStatusCodeActionResult)?.StatusCode;

    private static SessionService NewSessions(
        IDbContextFactory<AppDbContext> contexts,
        StateService state,
        IConfiguration configuration)
        => new(
            contexts,
            new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!),
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);

    private static ExternalSignInService NewSignInService(
        AccessService access,
        IDbContextFactory<AppDbContext> contexts,
        StateService state,
        IConfiguration configuration)
        => new(access, contexts, NewSessions(contexts, state, configuration));

    private async Task<(AuthController Controller, ApiKeyService ApiKey)> NewPasswordSignInAsync(
        TestDatabase database,
        AccountMode mode)
    {
        var hasher = new PasswordHasher<UserAccount>();
        await using (var context = database.Factory.CreateDbContext())
        {
            foreach (var (username, mainAdmin) in new[] { ("owner", true), ("member", false) })
            {
                var account = AccountResetTests.Account(Guid.NewGuid(), mainAdmin);
                account.Username = username;
                account.PasswordHash = hasher.HashPassword(account, Password);
                context.UserAccounts.Add(account);
            }
            await context.SaveChangesAsync();
        }

        var state = CreateStateService(_root);
        state.UpdateState(current =>
        {
            current.Access.Mode = mode;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var configuration = Configuration(("Security:ApiKeyPath", Path.Combine(_root, "api-key.txt")));
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var controller = new AuthController(
            NewSessions(database.Factory, state, configuration),
            NullLogger<AuthController>.Instance,
            database.Factory,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            apiKey,
            hasher,
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance),
            new AccessService(state, configuration, database.Factory))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        return (controller, apiKey);
    }

    private static AccountsController NewAccountsController(
        IDbContextFactory<AppDbContext> contexts,
        IConfiguration configuration,
        AccessService access,
        Guid ownerId)
    {
        var caller = AccountResetTests.Session(ownerId, "live");
        caller.SessionType = SessionType.Admin;
        var controller = new AccountsController(
            contexts,
            new PasswordHasher<UserAccount>(),
            new SessionService(
                contexts,
                new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!),
                NullLogger<SessionService>.Instance,
                stateService: null!,
                signalR: null!,
                configuration),
            new IdentityAuditService(contexts, NullLogger<IdentityAuditService>.Instance),
            new AccountLockout(NullLogger<AccountLockout>.Instance),
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            NullLogger<AccountsController>.Instance,
            access);
        var httpContext = new DefaultHttpContext();
        httpContext.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    /// The services a key rotation reaches, built on real storage under the test's temp folder.
    /// The Steam, Xbox and Epic process services stay unbuilt: rotation logs them out best-effort.
    /// </summary>
    private sealed class RotationHost
    {
        private readonly ApiKeyService _apiKey;
        private readonly SteamAuthStorageService _steam;
        private readonly XboxAuthStorageService _xbox;
        private readonly EpicAuthStorageService _epic;
        private readonly IConfiguration _configuration;

        public RotationHost(string root, IConfiguration environment, IDbContextFactory<AppDbContext> contexts)
        {
            var paths = new AuthCredentialFormatTests.TempDirPathResolver(root);
            _configuration = new ConfigurationBuilder()
                .AddConfiguration(environment)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:ApiKeyPath"] = Path.Combine(root, "api_key.txt")
                })
                .Build();
            _apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, _configuration, paths);
            var encryption = new SecureStateEncryptionService(
                DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(root, "dp-keys"))),
                _apiKey,
                NullLogger<SecureStateEncryptionService>.Instance);
            _steam = new SteamAuthStorageService(NullLogger<SteamAuthStorageService>.Instance, paths, encryption);
            _xbox = new XboxAuthStorageService(NullLogger<XboxAuthStorageService>.Instance, paths, encryption);
            _epic = new EpicAuthStorageService(NullLogger<EpicAuthStorageService>.Instance, paths, encryption);
            State = new StateService(NullLogger<StateService>.Instance, paths, encryption, _steam);
            Access = new AccessService(State, _configuration, contexts);
        }

        public StateService State { get; }

        public AccessService Access { get; }

        public async Task RotateAsOwnerAsync(TestDbContextFactory contexts)
        {
            var ownerId = Guid.NewGuid();
            await using (var context = contexts.CreateDbContext())
            {
                context.UserAccounts.Add(AccountResetTests.Account(ownerId, true));
                await context.SaveChangesAsync();
            }
            var caller = AccountResetTests.Session(ownerId, "live");
            caller.SessionType = SessionType.Admin;
            var httpContext = new DefaultHttpContext();
            httpContext.Items["Session"] = caller;
            var sessions = new SessionService(
                contexts,
                _apiKey,
                NullLogger<SessionService>.Instance,
                State,
                signalR: null!,
                _configuration);
            var controller = new ApiKeysController(
                contexts,
                _apiKey,
                steamKit2Service: null!,
                xboxCatalogMappingService: null!,
                epicMappingService: null!,
                _steam,
                _xbox,
                _epic,
                State,
                sessions,
                new IdentityAuditService(contexts, NullLogger<IdentityAuditService>.Instance),
                _configuration,
                new AuthenticationHelper(_apiKey, NullLogger<AuthenticationHelper>.Instance),
                NullLogger<ApiKeysController>.Instance,
                Access)
            {
                ControllerContext = new ControllerContext { HttpContext = httpContext }
            };

            Assert.IsType<OkObjectResult>(await controller.RegenerateApiKeyAsync());
        }
    }

    /// <summary>Fails the save that deletes accounts, the way a lost database connection would.</summary>
    private sealed class AccountDeleteFails : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (saving.Context?.ChangeTracker.Entries<UserAccount>().Any(entry => entry.State == EntityState.Deleted) == true)
            {
                throw new DbUpdateException("The account delete could not be saved");
            }
            return base.SavingChangesAsync(saving, result, cancellationToken);
        }
    }

    /// <summary>
    /// Stores the same new account through another connection just before this save, the order two
    /// callbacks for one identity reach the database in.
    /// </summary>
    private sealed class AccountArrivesFirst(DbContextOptions<AppDbContext> options, Guid accountId) : SaveChangesInterceptor
    {
        private bool _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData saving,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (!_arrived
                && saving.Context?.ChangeTracker.Entries<UserAccount>().Any(entry => entry.State == EntityState.Added) == true)
            {
                _arrived = true;
                await using var other = new AppDbContext(options);
                var account = AccountResetTests.Account(accountId);
                account.Username = "arrived-first";
                other.UserAccounts.Add(account);
                await other.SaveChangesAsync(cancellationToken);
            }
            return await base.SavingChangesAsync(saving, result, cancellationToken);
        }
    }
}
