using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
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
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed class SocialLoginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-social-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(LoginKind.Google, "google")]
    [InlineData(LoginKind.GitHub, "github")]
    [InlineData(LoginKind.Microsoft, "microsoft")]
    [InlineData(LoginKind.Apple, "apple")]
    [InlineData(LoginKind.CustomOidc, "customOidc")]
    public void LoginKindsHaveStableWireNames(LoginKind kind, string wireName)
    {
        Assert.Equal($"\"{wireName}\"", JsonSerializer.Serialize(kind));
        Assert.Equal(kind, JsonSerializer.Deserialize<LoginKind>($"\"{wireName}\""));
    }

    [Theory]
    [InlineData(LoginKind.Google)]
    [InlineData(LoginKind.GitHub)]
    public async Task DeferredOwnerCallbacksDoNotOpenTheAccountDatabase(LoginKind kind)
    {
        var configuration = Configuration(databaseSetupPending: true);
        var state = CreateState();
        var access = new AccessService(state, configuration, dbContextFactory: null!);
        var pending = Stage(access, kind, AccountMode.Oidc);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(
            null!,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = new ExternalSignInService(access, null!, sessions);
        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = pending.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["login_id"] = kind == LoginKind.GitHub ? "github" : "google";
        var subject = "deferred-owner";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject)],
            kind == LoginKind.GitHub ? AccessService.GitHubSetupScheme : AccessService.GoogleSetupScheme));
        var httpContext = new DefaultHttpContext();

        if (kind == LoginKind.GitHub)
        {
            var scheme = new AuthenticationScheme(
                AccessService.GitHubSetupScheme,
                AccessService.GitHubSetupScheme,
                typeof(OAuthHandler<OAuthOptions>));
            var context = new TicketReceivedContext(
                httpContext,
                scheme,
                new OAuthOptions(),
                new AuthenticationTicket(principal, properties, scheme.Name));
            var events = new GitHubEvents(
                access,
                signIns,
                new OptionsCache<OAuthOptions>(),
                NullLogger<GitHubEvents>.Instance);

            await events.TicketReceived(context);
        }
        else
        {
            properties.Items["oidc_issuer"] = "https://accounts.google.com";
            properties.Items["oidc_subject"] = subject;
            var scheme = new AuthenticationScheme(
                AccessService.GoogleSetupScheme,
                AccessService.GoogleSetupScheme,
                typeof(OpenIdConnectHandler));
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

            await events.TicketReceived(context);
        }

        Assert.Equal(StatusCodes.Status302Found, httpContext.Response.StatusCode);
        Assert.Contains("loginTest=success", httpContext.Response.Headers.Location.ToString(), StringComparison.Ordinal);
        Assert.Null(access.GetLoginSettings(properties.Items["login_id"]!, setup: true));
    }

    [Fact]
    public async Task DormantOwnerSignInRequiresTheKeyInApiKeyPasswordMode()
    {
        var (controller, access, _) = NewController(databaseSetupPending: true);
        var pending = Stage(access, LoginKind.Google, AccountMode.Oidc);
        Assert.True(access.PromotePendingLogin(
            "google",
            pending.Revision,
            "https://accounts.google.com",
            "owner"));
        access.Apply(
            new AccessSetupRequest { Mode = AccountMode.ApiKeyPassword, ApiKey = "checked-by-controller" },
            null);
        controller.Request.Scheme = Uri.UriSchemeHttps;
        controller.Request.Host = new HostString("localhost");

        var result = await controller.StartLoginAsync(new LoginStartRequest
        {
            LoginId = "google",
            Owner = true
        });

        Assert.IsType<UnauthorizedObjectResult>(result.Result);
    }

    [Fact]
    public void PromotionMergesIdentityLinksLearnedWhileReplacementWasPending()
    {
        var access = NewAccessService();
        var active = Stage(access, LoginKind.CustomOidc, AccountMode.Oidc);
        Assert.True(access.PromotePendingLogin(
            "customOidc",
            active.Revision,
            "https://login.example.test",
            "owner"));
        var replacement = Stage(access, LoginKind.CustomOidc, AccountMode.Oidc);
        var linkedDuringTest = access.GetOrCreateAccountId(
            "customOidc",
            "https://login.example.test",
            "late-member",
            active.Revision);

        Assert.True(access.PromotePendingLogin(
            "customOidc",
            replacement.Revision,
            "https://login.example.test",
            "owner"));
        Assert.Equal(
            linkedDuringTest,
            access.GetOrCreateAccountId(
                "customOidc",
                "https://login.example.test",
                "late-member",
                replacement.Revision));
    }

    [Fact]
    public void StagedLoginKeepsExistingServicesAvailableUntilPromotion()
    {
        var service = NewAccessService();
        var google = Stage(service, LoginKind.Google, AccountMode.Oidc);
        Assert.True(service.PromotePendingLogin("google", google.Revision, "https://accounts.google.com", "owner-google"));

        var github = Stage(service, LoginKind.GitHub, AccountMode.ApiKeyOidc);

        Assert.Equal(AccountMode.Oidc, service.GetMode());
        Assert.Equal("github", Assert.IsType<OidcSettings>(service.GetLoginSettings("github", setup: true)).Id);
        Assert.Equal("google", Assert.Single(service.GetLoginServices()).Id);
        Assert.True(service.IdentityAllowed("google", google.Revision, setup: false, ownerOnly: true, "https://accounts.google.com", "owner-google"));
        Assert.False(service.PromotePendingLogin("github", github.Revision + 1, "https://github.com", "owner-github"));
        Assert.Equal("google", Assert.Single(service.GetLoginServices()).Id);

        Assert.True(service.PromotePendingLogin("github", github.Revision, "https://github.com", "owner-github"));
        Assert.Equal(AccountMode.ApiKeyOidc, service.GetMode());
        Assert.Equal(new[] { "google", "github" }, service.GetLoginServices().Select(login => login.Id));
    }

    [Fact]
    public void ExternalAccountIdsAreNamespacedByLoginAndIssuer()
    {
        var service = NewAccessService();
        var google = Stage(service, LoginKind.Google, AccountMode.Oidc);
        Assert.True(service.PromotePendingLogin("google", google.Revision, "https://accounts.google.com", "owner"));
        var custom = Stage(service, LoginKind.CustomOidc, AccountMode.Oidc);
        Assert.True(service.PromotePendingLogin("customOidc", custom.Revision, "https://login.example.test", "owner"));

        var googleId = service.GetOrCreateAccountId("google", "https://accounts.google.com", "42", google.Revision);
        var customId = service.GetOrCreateAccountId("customOidc", "https://login.example.test", "42", custom.Revision);

        Assert.NotEqual(googleId, customId);
        Assert.Equal(googleId, service.GetOrCreateAccountId("google", "https://accounts.google.com", "42", google.Revision));
        Assert.Throws<ValidationException>(() =>
            service.GetOrCreateAccountId("google", "https://accounts.google.com", "42", google.Revision + 1));
    }

    [Fact]
    public void RetestingAServiceKeepsExistingIdentityLinks()
    {
        var service = NewAccessService();
        var first = Stage(service, LoginKind.CustomOidc, AccountMode.Oidc);
        Assert.True(service.PromotePendingLogin("customOidc", first.Revision, "https://login.example.test", "owner"));
        var accountId = service.GetOrCreateAccountId(
            "customOidc",
            "https://login.example.test",
            "member",
            first.Revision);

        var replacement = Stage(service, LoginKind.CustomOidc, AccountMode.Oidc);
        Assert.True(service.PromotePendingLogin("customOidc", replacement.Revision, "https://login.example.test", "owner"));

        Assert.Equal(
            accountId,
            service.GetOrCreateAccountId(
                "customOidc",
                "https://login.example.test",
                "member",
                replacement.Revision));
    }

    [Fact]
    public void LegacySubjectMappingsAreMigratedWithoutChangingAccounts()
    {
        var accountId = Guid.NewGuid();
        var state = CreateState();
        state.UpdateState(current => current.Access.Oidc = new OidcSettings
        {
            Authority = "https://login.example.test",
            ClientId = "client",
            ClientSecret = "secret",
            OwnerIssuer = "https://login.example.test",
            OwnerSubject = "owner",
            Revision = 7,
            AccountIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
            {
                ["member"] = accountId
            }
        });

        var loaded = CreateStateService(_root);
        var service = new AccessService(loaded, Configuration(databaseSetupPending: true), dbContextFactory: null!);
        var settings = Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: false));

        Assert.Equal(1, settings.IdentityVersion);
        Assert.Equal(
            accountId,
            service.GetOrCreateAccountId(
                "customOidc",
                "https://login.example.test",
                "member",
                settings.Revision));
    }

    [Fact]
    public void DeletingAnExternalAccountRemovesItsLoginPermissionAndInvalidatesChallenges()
    {
        var service = NewAccessService();
        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = LoginKind.CustomOidc,
                    Authority = "https://login.example.test",
                    ClientId = "client",
                    ClientSecret = "secret",
                    AllowedSubjects = ["member"]
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: true));
        Assert.True(service.PromotePendingLogin("customOidc", pending.Revision, "https://login.example.test", "owner"));
        var accountId = service.GetOrCreateAccountId(
            "customOidc",
            "https://login.example.test",
            "member",
            pending.Revision);

        service.ForgetAccounts([accountId]);

        var active = Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: false));
        Assert.NotEqual(pending.Revision, active.Revision);
        Assert.DoesNotContain("member", active.AllowedSubjects);
        Assert.False(service.IdentityAllowed("customOidc", active.Revision, setup: false, ownerOnly: false, "https://login.example.test", "member"));
        Assert.Throws<ValidationException>(() => service.GetOrCreateAccountId(
            "customOidc",
            "https://login.example.test",
            "member",
            pending.Revision));
    }

    [Fact]
    public void DeletingAnExternalAccountAlsoRemovesItFromAPendingReplacement()
    {
        var service = NewAccessService();
        AccessSetupResponse StageAllowed()
            => service.Apply(
                new AccessSetupRequest
                {
                    Mode = AccountMode.Oidc,
                    ApiKey = "checked-by-controller",
                    Login = new LoginSetupRequest
                    {
                        Kind = LoginKind.CustomOidc,
                        Authority = "https://login.example.test",
                        ClientId = "client",
                        ClientSecret = "secret",
                        AllowedSubjects = ["member"]
                    }
                },
                null);

        StageAllowed();
        var first = Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: true));
        Assert.True(service.PromotePendingLogin("customOidc", first.Revision, "https://login.example.test", "owner"));
        var accountId = service.GetOrCreateAccountId(
            "customOidc",
            "https://login.example.test",
            "member",
            first.Revision);
        StageAllowed();
        Assert.Contains("member", Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: true)).AllowedSubjects);

        service.ForgetAccounts([accountId]);

        var pending = Assert.IsType<OidcSettings>(service.GetLoginSettings("customOidc", setup: true));
        Assert.DoesNotContain("member", pending.AllowedSubjects);
        Assert.True(service.PromotePendingLogin("customOidc", pending.Revision, "https://login.example.test", "owner"));
        Assert.False(service.IdentityAllowed(
            "customOidc",
            pending.Revision,
            setup: false,
            ownerOnly: false,
            "https://login.example.test",
            "member"));
    }

    [Fact]
    public void AppleUsesItsFixedEndpointFormPostAndServerGeneratedSecret()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var privateKey = key.ExportPkcs8PrivateKeyPem();
        var service = NewAccessService();
        var pending = Stage(service, LoginKind.Apple, AccountMode.Oidc, privateKey);
        var stateFile = Path.Combine(
            _root,
            nameof(LancacheManager.Core.Interfaces.IPathResolver.GetStateDirectory),
            "state.json");
        Assert.DoesNotContain(privateKey, File.ReadAllText(stateFile), StringComparison.Ordinal);
        Assert.True(service.PromotePendingLogin("apple", pending.Revision, "https://appleid.apple.com", "owner"));

        var options = new OpenIdConnectOptions();
        new OidcOptionsSetup(service).Configure(AccessService.AppleScheme, options);

        Assert.Equal("https://appleid.apple.com", options.Authority);
        Assert.Equal("/api/auth/login/apple/callback", options.CallbackPath);
        Assert.Equal(OpenIdConnectResponseMode.FormPost, options.ResponseMode);
        Assert.False(options.UsePkce);
        Assert.Equal(new[] { "openid" }, options.Scope);
        Assert.Equal(AccessService.AppleCookieScheme, options.SignInScheme);

        var secret = AppleClientSecret.Create(Assert.IsType<OidcSettings>(service.GetLoginSettings("apple", setup: false)), new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var token = new JwtSecurityTokenHandler().ReadJwtToken(secret);
        Assert.Equal("TEAM123456", token.Issuer);
        Assert.Equal("https://appleid.apple.com", Assert.Single(token.Audiences));
        Assert.Equal("com.example.web", token.Subject);
        Assert.Equal("KEY1234567", token.Header.Kid);
        Assert.InRange(token.ValidTo - token.ValidFrom, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public void GitHubUsesOAuthWithPkceAndNoRequestedScopes()
    {
        var service = NewAccessService();
        Stage(service, LoginKind.GitHub, AccountMode.Oidc);
        var options = new OAuthOptions();

        new GitHubOptionsSetup(service).Configure(AccessService.GitHubSetupScheme, options);

        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.Empty(options.Scope);
        Assert.Equal("https://github.com/login/oauth/authorize", options.AuthorizationEndpoint);
        Assert.Equal("https://github.com/login/oauth/access_token", options.TokenEndpoint);
        Assert.Equal("https://api.github.com/user", options.UserInformationEndpoint);
        Assert.Equal("/api/auth/login/github/setup-callback", options.CallbackPath);
    }

    [Fact]
    public void MicrosoftConsumerLoginUsesTheExactConsumerTenantIssuer()
    {
        var service = NewAccessService();
        Stage(service, LoginKind.Microsoft, AccountMode.Oidc);
        var options = new OpenIdConnectOptions();

        new OidcOptionsSetup(service).Configure(AccessService.MicrosoftSetupScheme, options);

        Assert.Equal(
            "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0",
            options.Authority);
        Assert.Throws<ValidationException>(() => service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = LoginKind.Microsoft,
                    ClientId = "client",
                    ClientSecret = "secret",
                    Tenant = "common"
                }
            },
            null));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"id\":0}")]
    [InlineData("{\"id\":\"42\"}")]
    public async Task GitHubRejectsInvalidUserResponses(string body)
    {
        var service = NewAccessService();
        var pending = Stage(service, LoginKind.GitHub, AccountMode.Oidc);
        var options = new OAuthOptions
        {
            UserInformationEndpoint = "https://api.github.com/user"
        };
        var properties = new AuthenticationProperties();
        properties.Items["access_revision"] = pending.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        properties.Items["access_setup"] = "true";
        properties.Items["access_owner"] = "false";
        properties.Items["login_id"] = "github";
        var principal = new ClaimsPrincipal(new ClaimsIdentity(AccessService.GitHubSetupScheme));
        using var response = JsonDocument.Parse("{\"access_token\":\"token\",\"token_type\":\"bearer\"}");
        using var tokens = OAuthTokenResponse.Success(response);
        using var client = new HttpClient(new StaticHttpHandler(body));
        using var emptyUser = JsonDocument.Parse("{}");
        var context = new OAuthCreatingTicketContext(
            principal,
            properties,
            new DefaultHttpContext(),
            new AuthenticationScheme(AccessService.GitHubSetupScheme, AccessService.GitHubSetupScheme, typeof(OAuthHandler<OAuthOptions>)),
            options,
            client,
            tokens,
            emptyUser.RootElement);
        var events = new GitHubEvents(
            service,
            signInService: null!,
            new OptionsCache<OAuthOptions>(),
            NullLogger<GitHubEvents>.Instance);

        await events.CreatingTicket(context);

        Assert.NotNull(context.Result?.Failure);
        Assert.Empty(principal.Claims);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("192.0.2.20")]
    [InlineData("[2001:db8::20]")]
    public async Task AppleStartRejectsHostsWithoutADomainEvenWhenHttps(string host)
    {
        var (controller, service, apiKey) = NewController(databaseSetupPending: true);
        Stage(service, LoginKind.Apple, AccountMode.Oidc, NewAppleKey());
        controller.Request.Scheme = Uri.UriSchemeHttps;
        controller.Request.Host = new HostString(host);

        var result = await controller.StartLoginAsync(new LoginStartRequest
        {
            LoginId = "apple",
            ApiKey = apiKey,
            Setup = true
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task ExistingExternalOwnerCannotSwitchToPasswordBeforeSettingCredentials()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ownerId = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = ownerId,
                Username = "oidc-owner",
                PasswordHash = string.Empty,
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var state = CreateState();
        state.UpdateState(current =>
        {
            current.Access.Mode = AccountMode.Oidc;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var configuration = Configuration(databaseSetupPending: false);
        var access = new AccessService(state, configuration, database.Factory);
        var key = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var controller = CreateController(access, key);
        controller.HttpContext.Items["Session"] = new UserSession
        {
            Id = Guid.NewGuid(),
            AccountId = ownerId,
            SessionType = SessionType.Admin
        };

        var result = await controller.SetUpAsync(new AccessSetupRequest
        {
            Mode = AccountMode.Password,
            ApiKey = key.GetApiKey()
        });

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Equal(AccountMode.Oidc, access.GetMode());
    }

    [Fact]
    public async Task ExternalOnlyAccountUsesTheDummyHashAndDoesNotAccumulatePasswordLockout()
    {
        await using var database = await TestDatabase.CreateAsync();
        var accountId = Guid.NewGuid();
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = accountId,
                Username = "external-owner",
                PasswordHash = string.Empty,
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var configuration = Configuration(databaseSetupPending: false);
        var state = CreateState();
        state.UpdateState(current =>
        {
            current.Access.Mode = AccountMode.Oidc;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var access = new AccessService(state, configuration, database.Factory);
        var key = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>();
        var sessions = new SessionService(
            database.Factory,
            key,
            NullLogger<SessionService>.Instance,
            state,
            notifications,
            configuration);
        var hasher = new CountingHasher();
        var lockout = new AccountLockout(NullLogger<AccountLockout>.Instance);
        var controller = new AuthController(
            sessions,
            NullLogger<AuthController>.Instance,
            database.Factory,
            state,
            notifications,
            key,
            hasher,
            lockout,
            new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance),
            access)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await controller.LoginAsync(new LoginRequest
            {
                Username = "external-owner",
                Password = "not-a-local-password"
            });
            Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        }

        Assert.Equal(6, hasher.VerifyCalls);
        Assert.All(hasher.VerifiedHashes, hash => Assert.False(string.IsNullOrEmpty(hash)));
        Assert.False(lockout.IsLocked(accountId));
    }

    [Fact]
    public async Task SupersededSetupDoesNotIssueACookieOrLeaveItsCreatedOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(databaseSetupPending: false);
        var state = CreateState();
        var access = new AccessService(state, configuration, database.Factory);
        var pending = Stage(access, LoginKind.Google, AccountMode.Oidc);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var connections = new LancacheManager.Core.Services.ConnectionTrackingService(
            NullLogger<LancacheManager.Core.Services.ConnectionTrackingService>.Instance);
        var sharedSessionId = Guid.NewGuid();
        var sharedConnectionAborted = false;
        state.SetSharedAdminSessionId(sharedSessionId);
        connections.RegisterConnection(
            sharedSessionId,
            "stale-setup-shared-connection",
            () => sharedConnectionAborted = true);
        var sessions = new SessionService(
            database.Factory,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration,
            connectionTrackingService: connections);
        var signIns = new ExternalSignInService(access, database.Factory, sessions);
        var httpContext = new DefaultHttpContext();

        var signIn = await signIns.TryCreateSessionAsync(
            pending,
            owner: true,
            "https://accounts.google.com",
            "owner",
            httpContext);
        Assert.NotNull(signIn);
        Assert.False(httpContext.Response.Headers.ContainsKey("Set-Cookie"));

        Stage(access, LoginKind.GitHub, AccountMode.Oidc);
        Assert.False(await signIns.CompleteSetupAsync(
            signIn,
            () => access.PromotePendingLogin("google", pending.Revision, "https://accounts.google.com", "owner", signIn.AccountId)));

        await using var context = database.Factory.CreateDbContext();
        Assert.False(await context.UserAccounts.AnyAsync());
        Assert.True(await context.UserSessions.Where(session => session.Id == signIn.SessionId).Select(session => session.IsRevoked).SingleAsync());
        Assert.False(httpContext.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.False(sharedConnectionAborted);
        Assert.Equal(sharedSessionId, state.GetSharedAdminSessionId());
    }

    [Fact]
    public async Task FailedPromotionSaveRollsBackAccessAndCompensatesTheNewSignIn()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(databaseSetupPending: false);
        var state = CreateState();
        var access = new AccessService(state, configuration, database.Factory);
        var pending = Stage(access, LoginKind.Google, AccountMode.Oidc);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(
            database.Factory,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = new ExternalSignInService(access, database.Factory, sessions);
        var signIn = await signIns.TryCreateSessionAsync(
            pending,
            owner: true,
            "https://accounts.google.com",
            "owner",
            new DefaultHttpContext());
        Assert.NotNull(signIn);
        var before = state.GetState().Access;
        var revision = before.Revision;
        Directory.CreateDirectory(Path.Combine(
            _root,
            nameof(LancacheManager.Core.Interfaces.IPathResolver.GetStateDirectory),
            "state.json.tmp"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => signIns.CompleteSetupAsync(
            signIn,
            () => access.PromotePendingLogin("google", pending.Revision, "https://accounts.google.com", "owner", signIn.AccountId)));

        var after = state.GetState().Access;
        Assert.Equal(revision, after.Revision);
        Assert.Equal(before.Mode, after.Mode);
        Assert.Equal(before.PendingMode, after.PendingMode);
        Assert.Equal(before.PendingOidc?.Revision, after.PendingOidc?.Revision);
        Assert.Empty(after.Logins);
        await using var context = database.Factory.CreateDbContext();
        Assert.False(await context.UserAccounts.AnyAsync());
        Assert.True(await context.UserSessions.Where(session => session.Id == signIn.SessionId).Select(session => session.IsRevoked).SingleAsync());
    }

    [Fact]
    public async Task RetirementFailureDoesNotCompensateAPromotedOwner()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(databaseSetupPending: false);
        var state = CreateState();
        var access = new AccessService(state, configuration, database.Factory);
        var pending = Stage(access, LoginKind.Google, AccountMode.Oidc);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var workingSessions = new SessionService(
            database.Factory,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = new ExternalSignInService(access, database.Factory, workingSessions);
        var signIn = await signIns.TryCreateSessionAsync(
            pending,
            owner: true,
            "https://accounts.google.com",
            "owner",
            new DefaultHttpContext());
        Assert.NotNull(signIn);

        state.SetSharedAdminSessionId(Guid.NewGuid());
        var failingSessions = new SessionService(
            new ThrowingDbContextFactory(),
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var completingSignIns = new ExternalSignInService(access, database.Factory, failingSessions);

        Assert.True(await completingSignIns.CompleteSetupAsync(
            signIn,
            () => access.PromotePendingLogin("google", pending.Revision, "https://accounts.google.com", "owner", signIn.AccountId)));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(account => account.Id == signIn.AccountId));
        Assert.False(await context.UserSessions.Where(session => session.Id == signIn.SessionId).Select(session => session.IsRevoked).SingleAsync());
    }

    [Fact]
    public async Task CompensationKeepsAnOwnerThatACompletedSetupAlreadyAdopted()
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration(databaseSetupPending: false);
        var state = CreateState();
        var access = new AccessService(state, configuration, database.Factory);
        var pending = Stage(access, LoginKind.Google, AccountMode.Oidc);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var sessions = new SessionService(
            database.Factory,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            DispatchProxy.Create<ISignalRNotificationService, AccountLoginTests.SilentNotifications>(),
            configuration);
        var signIns = new ExternalSignInService(access, database.Factory, sessions);
        var signIn = await signIns.TryCreateSessionAsync(
            pending,
            owner: true,
            "https://accounts.google.com",
            "owner",
            new DefaultHttpContext());
        Assert.NotNull(signIn);
        Assert.True(access.PromotePendingLogin(
            "google",
            pending.Revision,
            "https://accounts.google.com",
            "owner",
            signIn.AccountId));

        await signIns.CancelAsync(signIn);

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(account => account.Id == signIn.AccountId));
    }

    [Fact]
    public void NewSecretTakingRoutesAreThrottledAndOnlySetupStartsIgnoreAntiforgery()
    {
        AssertRoute<AccessController>(nameof(AccessController.StartLoginAsync), ignoresAntiforgery: true);
        AssertRoute<AccessController>(nameof(AccessController.RemoveLoginAsync), ignoresAntiforgery: false);
        AssertRoute<AccountSetupController>(nameof(AccountSetupController.SetMainAdminPasswordAsync), ignoresAntiforgery: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertRoute<TController>(string methodName, bool ignoresAntiforgery)
    {
        var method = typeof(TController).GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<EnableRateLimitingAttribute>());
        Assert.Equal(ignoresAntiforgery, method.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>() is not null);
    }

    private OidcSettings Stage(
        AccessService service,
        LoginKind kind,
        AccountMode mode,
        string? privateKey = null)
    {
        var response = service.Apply(
            new AccessSetupRequest
            {
                Mode = mode,
                ApiKey = "checked-by-controller",
                Login = new LoginSetupRequest
                {
                    Kind = kind,
                    ClientId = kind == LoginKind.Apple ? "com.example.web" : $"{kind}-client",
                    ClientSecret = kind == LoginKind.Apple ? string.Empty : "secret",
                    Tenant = kind == LoginKind.Microsoft ? "consumers" : null,
                    Authority = kind == LoginKind.CustomOidc ? "https://login.example.test" : null,
                    TeamId = kind == LoginKind.Apple ? "TEAM123456" : null,
                    KeyId = kind == LoginKind.Apple ? "KEY1234567" : null,
                    PrivateKey = privateKey
                }
            },
            null);
        Assert.True(response.RequiresLoginTest);
        return Assert.IsType<OidcSettings>(service.GetLoginSettings(kind.ToString() == nameof(LoginKind.CustomOidc) ? "customOidc" : kind.ToString().ToLowerInvariant(), setup: true));
    }

    private AccessService NewAccessService()
        => new(CreateState(), Configuration(databaseSetupPending: true), dbContextFactory: null!);

    private StateService CreateState()
    {
        Directory.CreateDirectory(_root);
        return CreateStateService(_root);
    }

    private IConfiguration Configuration(bool databaseSetupPending)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt"),
                ["Runtime:DatabaseSetupPending"] = databaseSetupPending.ToString()
            })
            .Build();

    private (AccessController Controller, AccessService Access, string ApiKey) NewController(bool databaseSetupPending)
    {
        var configuration = Configuration(databaseSetupPending);
        var access = new AccessService(CreateState(), configuration, dbContextFactory: null!);
        var key = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        return (CreateController(access, key), access, key.GetApiKey());
    }

    private static AccessController CreateController(AccessService access, ApiKeyService key)
        => new(
            access,
            key,
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static string NewAppleKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return key.ExportPkcs8PrivateKeyPem();
    }

    private sealed class StaticHttpHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body)
            });
    }

    private sealed class CountingHasher : IPasswordHasher<UserAccount>
    {
        private readonly PasswordHasher<UserAccount> _inner = new();
        public int VerifyCalls { get; private set; }
        public List<string> VerifiedHashes { get; } = [];

        public string HashPassword(UserAccount user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(
            UserAccount user,
            string hashedPassword,
            string providedPassword)
        {
            VerifyCalls++;
            VerifiedHashes.Add(hashedPassword);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
