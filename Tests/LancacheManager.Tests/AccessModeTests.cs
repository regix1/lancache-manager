using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using LancacheManager.Middleware;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OAuth;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

public sealed class AccessModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-access-{Guid.NewGuid():N}");

    [Theory]
    [InlineData(AccountMode.Password, "password")]
    [InlineData(AccountMode.ApiKeyPassword, "apiKeyPassword")]
    [InlineData(AccountMode.ApiKeyOidc, "apiKeyOidc")]
    [InlineData(AccountMode.Oidc, "oidc")]
    [InlineData(AccountMode.Unauthenticated, "unauthenticated")]
    public void AccountModesHaveStableWireNames(AccountMode mode, string wireName)
    {
        Assert.Equal($"\"{wireName}\"", JsonSerializer.Serialize(mode));
        Assert.Equal(mode, JsonSerializer.Deserialize<AccountMode>($"\"{wireName}\""));
    }

    [Fact]
    public void UnknownAccountModeIsRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AccountMode>("\"open\""));
    }

    [Fact]
    public void OidcChangeStaysPendingUntilTheExpectedIdentityReturns()
    {
        Directory.CreateDirectory(_root);
        var state = CreateStateService(_root);
        var service = NewAccessService(state, authenticationEnabled: true);

        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Password,
                ApiKey = "checked-by-controller"
            },
            Guid.NewGuid());

        var response = service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://login.example.test/tenant/",
                    ClientId = "lancache",
                    ClientSecret = "top-secret",
                    DisplayName = "Example"
                }
            },
            Guid.NewGuid());

        Assert.True(response.RequiresOidcTest);
        Assert.Equal(AccountMode.Password, service.GetMode());
        Assert.True(service.HasPendingOidc());

        var pending = Assert.IsType<OidcSettings>(service.GetOidcSettings(setup: true));
        Assert.False(service.IdentityAllowed(pending.Revision + 1, setup: true, ownerOnly: false, "issuer", "owner"));
        Assert.True(service.IdentityAllowed(pending.Revision, setup: true, ownerOnly: false, "issuer", "owner"));

        var stateFile = Path.Combine(
            _root,
            nameof(LancacheManager.Core.Interfaces.IPathResolver.GetStateDirectory),
            "state.json");
        Assert.DoesNotContain("top-secret", File.ReadAllText(stateFile), StringComparison.Ordinal);
        Assert.Equal("top-secret", CreateStateService(_root).GetState().Access.PendingOidc?.ClientSecret);

        Assert.True(service.PromotePendingOidc(pending.Revision, "issuer", "owner"));
        Assert.Equal(AccountMode.Oidc, service.GetMode());
        Assert.False(service.HasPendingOidc());
        Assert.True(service.IdentityAllowed(pending.Revision, setup: false, ownerOnly: false, "issuer", "owner"));
        Assert.False(service.IdentityAllowed(pending.Revision, setup: false, ownerOnly: false, "other-issuer", "owner"));
    }

    [Fact]
    public void AdditionalSubjectsDoNotRestrictTheSetupOwner()
    {
        Directory.CreateDirectory(_root);
        var service = NewAccessService(CreateStateService(_root), authenticationEnabled: true);
        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.ApiKeyOidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://login.example.test",
                    ClientId = "lancache",
                    ClientSecret = "secret",
                    AllowedSubjects = ["expected", "colleague"]
                }
            },
            null);

        var pending = Assert.IsType<OidcSettings>(service.GetOidcSettings(setup: true));
        Assert.True(service.IdentityAllowed(pending.Revision, setup: true, ownerOnly: false, "issuer", "expected"));
        Assert.True(service.IdentityAllowed(pending.Revision, setup: true, ownerOnly: false, "issuer", "unexpected"));
        Assert.True(service.PromotePendingOidc(pending.Revision, "issuer", "unexpected"));
        Assert.True(service.IdentityAllowed(pending.Revision, setup: false, ownerOnly: false, "issuer", "colleague"));
        Assert.False(service.IdentityAllowed(pending.Revision, setup: false, ownerOnly: true, "issuer", "colleague"));
    }

    [Fact]
    public void LocalModeOverridesTheLegacyEnvironmentSetting()
    {
        Directory.CreateDirectory(_root);
        var service = NewAccessService(CreateStateService(_root), authenticationEnabled: false);
        Assert.Equal(AccountMode.Unauthenticated, service.GetMode());
        Assert.True(service.IsSetupRequired());

        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Password,
                ApiKey = "checked-by-controller"
            },
            null);

        Assert.Equal(AccountMode.Password, service.GetMode());
        Assert.True(service.IsAuthenticationEnabled());
        Assert.False(service.IsSetupRequired());
    }

    [Fact]
    public async Task CompletedSetupNeedsAnOwnerOnlyAfterAnAccountExists()
    {
        Directory.CreateDirectory(_root);
        await using var database = await TestDatabase.CreateAsync();
        var state = CreateStateService(_root);
        state.UpdateState(current =>
        {
            current.Access.Mode = AccountMode.Unauthenticated;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var configuration = new ConfigurationBuilder().Build();
        var service = new AccessService(state, configuration, database.Factory);

        Assert.False(await service.RequiresMainAdminAsync());

        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                PasswordHash = "unused",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        Assert.True(await service.RequiresMainAdminAsync());
    }

    [Fact]
    public void PendingOidcUsesASeparateSchemeFromTheActiveLogin()
    {
        Directory.CreateDirectory(_root);
        var service = NewAccessService(CreateStateService(_root), authenticationEnabled: true);
        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://active.example.test",
                    ClientId = "active",
                    ClientSecret = "secret"
                }
            },
            null);
        var first = Assert.IsType<OidcSettings>(service.GetOidcSettings(setup: true));
        Assert.True(service.PromotePendingOidc(first.Revision, "active-issuer", "owner"));

        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.ApiKeyOidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://pending.example.test",
                    ClientId = "pending",
                    ClientSecret = "new-secret"
                }
            },
            null);

        var setup = new OidcOptionsSetup(service);
        var activeOptions = new OpenIdConnectOptions();
        var pendingOptions = new OpenIdConnectOptions();
        setup.Configure(AccessService.OidcScheme, activeOptions);
        setup.Configure(AccessService.OidcSetupScheme, pendingOptions);

        Assert.Equal("https://active.example.test", activeOptions.Authority);
        Assert.Equal("/api/auth/oidc/callback", activeOptions.CallbackPath);
        Assert.Equal("https://pending.example.test", pendingOptions.Authority);
        Assert.Equal("/api/auth/oidc/setup-callback", pendingOptions.CallbackPath);
        Assert.Equal("code", activeOptions.ResponseType);
        Assert.True(activeOptions.UsePkce);
        Assert.True(activeOptions.ProtocolValidator.RequireNonce);
        Assert.True(activeOptions.ProtocolValidator.RequireStateValidation);
        Assert.Equal(CookieSecurePolicy.Always, activeOptions.CorrelationCookie.SecurePolicy);
        Assert.Equal(CookieSecurePolicy.Always, activeOptions.NonceCookie.SecurePolicy);
    }

    [Fact]
    public void OidcAccountIdsAreStableAndRevisionBound()
    {
        Directory.CreateDirectory(_root);
        var service = NewAccessService(CreateStateService(_root), authenticationEnabled: true);
        service.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://login.example.test",
                    ClientId = "lancache",
                    ClientSecret = "secret"
                }
            },
            null);
        var pending = Assert.IsType<OidcSettings>(service.GetOidcSettings(setup: true));
        Assert.True(service.PromotePendingOidc(pending.Revision, "issuer", "owner"));

        var first = service.GetOrCreateOidcAccountId("colleague", pending.Revision);
        Assert.Equal(first, service.GetOrCreateOidcAccountId("colleague", pending.Revision));
        Assert.Throws<ValidationException>(() =>
            service.GetOrCreateOidcAccountId("colleague", pending.Revision + 1));
    }

    [Fact]
    public void OidcChallengeIsShortLivedAndSingleUse()
    {
        var time = new TestTime(new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));
        var challenges = new OidcChallengeStore(time);

        var first = challenges.Create(12, setup: true);
        Assert.Equal(12, challenges.Take(first)?.Revision);
        Assert.Null(challenges.Take(first));

        var expired = challenges.Create(13, setup: false);
        time.Advance(TimeSpan.FromMinutes(6));
        challenges.Create(14, setup: false);
        Assert.Equal(1, challenges.Count);
        Assert.Null(challenges.Take(expired));
    }

    [Fact]
    public async Task OidcStartRequiresHttpsOutsideLoopback()
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "oidc-api-key.txt"),
                ["Runtime:DatabaseSetupPending"] = "true"
            })
            .Build();
        var state = CreateStateService(_root);
        var access = new AccessService(state, configuration, dbContextFactory: null!);
        access.Apply(
            new AccessSetupRequest
            {
                Mode = AccountMode.Oidc,
                ApiKey = "checked-by-controller",
                Oidc = new OidcSetupRequest
                {
                    Authority = "https://login.example.test",
                    ClientId = "lancache",
                    ClientSecret = "secret"
                }
            },
            null);
        var apiKey = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver: null!);
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
        var request = new OidcStartRequest { Setup = true, ApiKey = apiKey.GetApiKey() };

        controller.Request.Scheme = Uri.UriSchemeHttp;
        controller.Request.Host = new HostString("192.0.2.20");
        Assert.IsType<ConflictObjectResult>((await controller.StartOidcAsync(request)).Result);

        controller.Request.Host = new HostString("localhost");
        Assert.IsType<OkObjectResult>((await controller.StartOidcAsync(request)).Result);
    }

    [Fact]
    public async Task SetupGateAllowsOnlyTheBootstrapReadsAndAuthExit()
    {
        Directory.CreateDirectory(_root);
        var access = NewAccessService(CreateStateService(_root), authenticationEnabled: true);
        var middleware = new AccessSetupMiddleware(context =>
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });

        foreach (var path in new[] { "/api/system/setup", "/api/system/config" })
        {
            var allowed = new DefaultHttpContext();
            allowed.Request.Method = HttpMethods.Get;
            allowed.Request.Path = path;
            await middleware.InvokeAsync(allowed, access);
            Assert.Equal(StatusCodes.Status204NoContent, allowed.Response.StatusCode);
        }

        var logout = new DefaultHttpContext();
        logout.Request.Method = HttpMethods.Post;
        logout.Request.Path = "/api/auth/logout";
        await middleware.InvokeAsync(logout, access);
        Assert.Equal(StatusCodes.Status204NoContent, logout.Response.StatusCode);

        foreach (var (method, path) in new[]
                 {
                     (HttpMethods.Post, "/api/system/config"),
                     (HttpMethods.Get, "/api/system/operations")
                 })
        {
            var refused = new DefaultHttpContext();
            refused.Response.Body = new MemoryStream();
            refused.Request.Method = method;
            refused.Request.Path = path;
            await middleware.InvokeAsync(refused, access);
            Assert.Equal(StatusCodes.Status428PreconditionRequired, refused.Response.StatusCode);
        }
    }

    [Fact]
    public async Task KeyRecoveryRequiresTheServerRecoveryWindow()
    {
        Directory.CreateDirectory(_root);
        await using var database = await TestDatabase.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:EnableAuthentication"] = "false",
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt")
            })
            .Build();
        var state = CreateStateService(_root);
        state.UpdateState(current =>
        {
            current.Access.Mode = AccountMode.Unauthenticated;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = "owner",
                PasswordHash = "unused",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var access = new AccessService(state, configuration, database.Factory);
        var apiKey = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            configuration,
            pathResolver: null!);
        var window = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        var controller = new AccessController(
            access,
            apiKey,
            window,
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        var request = new AccessSetupRequest
        {
            Mode = AccountMode.Password,
            ApiKey = apiKey.GetApiKey(),
            Recovery = true
        };

        var refused = await controller.SetUpAsync(request);
        Assert.Equal(
            StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(refused.Result).StatusCode);

        Assert.True(window.OpenRecovery());
        Assert.IsType<OkObjectResult>((await controller.SetUpAsync(request)).Result);
        Assert.False(window.IsRecoveryOpen);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static AccessService NewAccessService(StateService stateService, bool authenticationEnabled)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:EnableAuthentication"] = authenticationEnabled.ToString()
            })
            .Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Password=unused")
            .Options;
        return new AccessService(stateService, configuration, new TestDbContextFactory(options));
    }

    private sealed class TestTime(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
