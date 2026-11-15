using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// The edges of the account wipe that turning sign-in off performs: the save asks for a confirmation
/// that names the count before it deletes anything, an account an environment login allows is deleted
/// with the rest, no first administrator can be created while a finished setup runs with sign-in off, and
/// turning sign-in back on opens the hour the first administrator is created in.
/// </summary>
// Saving the mode touches the process-wide shared session (SessionService.cs:44).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SignInOffWipesAccountsTests : IDisposable
{
    private const string Authority = "https://login.example.test";
    private const string GoodPassword = "Correct-Horse-9";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-sign-in-off-wipe-{Guid.NewGuid():N}");

    /// <summary>The Steam, Epic and Xbox login stores the wipe clears each deleted account's game logins from.</summary>
    private readonly IntegrationFixture _logins = new();

    [Fact]
    public async Task UnconfirmedSwitch_AsksWithTheAccountCountAndDeletesNothingAsync()
    {
        await using var scene = await SceneAsync();

        var saved = await scene.Settings(scene.OwnerSession).SetUpAsync(new AccessSetupRequest
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey()
        });

        var conflict = Assert.IsType<ConflictObjectResult>(saved.Result);
        var body = Assert.IsType<AccountRefusalResponse>(conflict.Value);
        Assert.Equal(AccountRefusalResponse.WipeConfirmRequired, body.StageKey);
        Assert.Equal(2, body.Context!["count"]);
        Assert.True(scene.Sessions.IsAuthenticationEnabled(), "an unconfirmed switch turned sign-in off");
        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(2, await read.UserAccounts.CountAsync());
        Assert.Equal(2, await read.UserSessions.CountAsync(session => session.AccountId != null));
    }

    /// <summary>
    /// The confirm names a count. When the table holds another number by the time it is sent (an
    /// account made while the confirm was open), the switch asks again with the new number and deletes
    /// nothing; the matching count then goes through.
    /// </summary>
    [Fact]
    public async Task AConfirmNamingAnOldCount_AsksAgainWithTheNewCountAndDeletesNothingAsync()
    {
        await using var scene = await SceneAsync();
        AccessSetupRequest Confirmed(int count) => new()
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey(),
            ConfirmedAccountCount = count
        };

        var stale = await scene.Settings(scene.OwnerSession).SetUpAsync(Confirmed(1));

        var conflict = Assert.IsType<ConflictObjectResult>(stale.Result);
        var body = Assert.IsType<AccountRefusalResponse>(conflict.Value);
        Assert.Equal(AccountRefusalResponse.WipeConfirmRequired, body.StageKey);
        Assert.Equal(2, body.Context!["count"]);
        Assert.True(scene.Sessions.IsAuthenticationEnabled(), "a confirm naming an old count turned sign-in off");
        await using (var read = await scene.Database.Factory.CreateDbContextAsync())
            Assert.Equal(2, await read.UserAccounts.CountAsync());

        var current = await scene.Settings(scene.OwnerSession).SetUpAsync(Confirmed(2));

        Assert.True(current.Result is OkObjectResult, $"a confirm naming the current count was answered {(current.Result as ObjectResult)?.StatusCode}");
        await using var after = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(0, await after.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task FirstAdministrator_IsRefusedWhileAFinishedSetupRunsWithSignInOffAsync()
    {
        await using var scene = await SceneAsync(accounts: false);
        scene.State.UpdateAccess(access => access.Mode = AccountMode.Unauthenticated);

        var created = await scene.AccountSetup().CreateFirstAdminAsync(new AccountCredentialsRequest
        {
            Username = "admin",
            Password = GoodPassword,
            ApiKey = scene.ApiKey.GetApiKey()
        });

        var conflict = Assert.IsType<ConflictObjectResult>(created.Result);
        Assert.Equal(AccountSetupRefusalResponse.SignInOff, Assert.IsType<AccountSetupRefusalResponse>(conflict.Value).StageKey);
        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(0, await read.UserAccounts.CountAsync());
    }

    [Fact]
    public async Task FirstAdministrator_IsCreatedWhileSetupIsStillRequiredAsync()
    {
        await using var scene = await SceneAsync(accounts: false);
        scene.State.UpdateAccess(access =>
        {
            access.Mode = AccountMode.Unauthenticated;
            access.SetupVersion = 0;
        });
        Assert.True(scene.Access.IsSetupRequired(), "the setup is not marked as still required");

        var created = await scene.AccountSetup().CreateFirstAdminAsync(new AccountCredentialsRequest
        {
            Username = "admin",
            Password = GoodPassword,
            ApiKey = scene.ApiKey.GetApiKey()
        });

        Assert.True(created.Result is OkObjectResult, $"creating the first administrator was answered {(created.Result as ObjectResult)?.StatusCode}");
        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(1, await read.UserAccounts.CountAsync(account => account.IsMainAdmin));
    }

    [Fact]
    public async Task TurningSignInBackOnWithNoAccount_OpensTheFirstAdministratorClaimAsync()
    {
        await using var scene = await SceneAsync();
        var off = await scene.Settings(scene.OwnerSession).SetUpAsync(UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey()
        }, accountCount: 2));
        Assert.True(off.Result is OkObjectResult, $"turning sign-in off was answered {(off.Result as ObjectResult)?.StatusCode}");
        // The hour after the wipe has passed.
        scene.ClaimWindow.Expire();

        var on = await scene.Settings(session: null).SetUpAsync(new AccessSetupRequest
        {
            Mode = AccountMode.ApiKeyPassword,
            ApiKey = scene.ApiKey.GetApiKey()
        });

        Assert.True(on.Result is OkObjectResult, $"turning sign-in back on was answered {(on.Result as ObjectResult)?.StatusCode}");
        Assert.True(scene.Sessions.IsAuthenticationEnabled(), "the save did not turn sign-in on");
        Assert.True(scene.ClaimWindow.IsOpen, "the first-admin claim is still closed after sign-in was turned back on");
    }

    /// <summary>
    /// With sign-in off nobody can sign in to an account an environment login allows, so the confirmed
    /// switch deletes it with the rest instead of refusing; that login's next sign-in creates it again
    /// once sign-in is back on.
    /// </summary>
    [Fact]
    public async Task ConfirmedSwitch_DeletesAnAccountAnEnvironmentLoginAllowsWithTheRestAsync()
    {
        await using var scene = await SceneAsync(("Security:Oidc:AllowedSubjects", "member"));
        Assert.True(scene.Access.IsAllowedByEnvironment(scene.MemberId), "the member is not allowed by the environment login");

        var saved = await scene.Settings(scene.OwnerSession).SetUpAsync(UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey()
        }, accountCount: 2));

        Assert.True(saved.Result is OkObjectResult, $"the confirmed switch was answered {(saved.Result as ObjectResult)?.StatusCode}");
        Assert.False(scene.Sessions.IsAuthenticationEnabled(), "the confirmed switch left sign-in on");
        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(0, await read.UserAccounts.CountAsync());
    }

    /// <summary>The start-up wipe deletes an account an environment login allows too.</summary>
    [Fact]
    public async Task StartWithTheEnvironmentForcingSignInOff_DeletesAnAccountAnEnvironmentLoginAllowsAsync()
    {
        await using var scene = await SceneAsync(("Security:SignInMode", "unauthenticated"), ("Security:Oidc:AllowedSubjects", "member"));
        Assert.True(scene.Access.IsAllowedByEnvironment(scene.MemberId), "the member is not allowed by the environment login");

        await new FirstAdminSessionResetService(
                scene.Database.Factory,
                scene.App.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FirstAdminSessionResetService>.Instance)
            .StartAsync(CancellationToken.None);

        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(0, await read.UserAccounts.CountAsync());
    }

    /// <summary>
    /// An installation with sign-in on and a finished setup. With <paramref name="accounts"/>, an owner
    /// and a user account, each signed in; the user is the identity <c>member</c> of the environment
    /// login when the configuration completes one.
    /// </summary>
    private async Task<Scene> SceneAsync(params (string Key, string? Value)[] settings)
        => await SceneAsync(accounts: true, settings);

    private async Task<Scene> SceneAsync(bool accounts, params (string Key, string? Value)[] settings)
    {
        ResetSharedSession();
        Directory.CreateDirectory(_root);
        var database = await TestDatabase.CreateAsync();
        var values = new Dictionary<string, string?> { ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt") };
        var environmentLogin = settings.Any(setting => setting.Key == "Security:Oidc:AllowedSubjects");
        if (environmentLogin)
        {
            values["Security:Oidc:Authority"] = Authority;
            values["Security:Oidc:ClientId"] = "environment-client";
            values["Security:Oidc:ClientSecret"] = "environment-secret";
        }
        foreach (var (key, value) in settings)
            values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var state = StateTestMethods.CreateStateService(Path.Combine(_root, "state"));
        state.UpdateAccess(access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var access = new AccessService(state, configuration, database.Factory);
        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        var sessions = new SessionService(
            database.Factory,
            apiKey,
            NullLogger<SessionService>.Instance,
            state,
            notifications,
            configuration,
            connectionTrackingService: connections);
        var claimWindow = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);

        UserSession? ownerSession = null;
        var memberId = Guid.NewGuid();
        if (accounts)
        {
            if (environmentLogin)
                memberId = access.GetOrCreateAccountId("customOidc", Authority, "member", 0);
            var owner = Account(Guid.NewGuid(), "owner", SessionType.Admin, mainAdmin: true);
            var member = Account(memberId, "member", SessionType.User, mainAdmin: false);
            await using (var seed = await database.Factory.CreateDbContextAsync())
            {
                seed.UserAccounts.AddRange(owner, member);
                await seed.SaveChangesAsync();
            }
            (_, ownerSession) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
            await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member);
        }

        var app = new ServiceCollection()
            .AddOptions()
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton(state)
            .AddSingleton(apiKey)
            .AddSingleton(sessions)
            .AddSingleton(access)
            .AddSingleton(claimWindow)
            .AddSingleton(connections)
            .AddSingleton(notifications)
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .AddSingleton<SteamAuthStorageService>(_logins.Storage)
            .AddSingleton(_logins.Epic)
            .AddSingleton(_logins.Xbox)
            .BuildServiceProvider();
        return new Scene(database, app, state, sessions, access, apiKey, claimWindow, ownerSession, memberId);
    }

    private static UserAccount Account(Guid id, string username, SessionType role, bool mainAdmin) => new()
    {
        Id = id,
        Username = username,
        Role = role,
        IsMainAdmin = mainAdmin,
        CreatedAtUtc = DateTime.UtcNow,
        PasswordHash = "seeded"
    };

    /// <summary>The shared session is process-wide, so each run starts from a fresh process's state.</summary>
    private static void ResetSharedSession()
    {
        foreach (var (name, value) in new (string, object?)[] { ("_authDisabledAdminSession", null), ("_authDisabledRetryAfterUtc", DateTime.MinValue) })
        {
            (typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"SessionService no longer has a static {name}."))
                .SetValue(null, value);
        }
    }

    public void Dispose()
    {
        ResetSharedSession();
        _logins.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed record Scene(
        TestDatabase Database,
        ServiceProvider App,
        StateService State,
        SessionService Sessions,
        AccessService Access,
        ApiKeyService ApiKey,
        AccountClaimWindow ClaimWindow,
        UserSession? OwnerSession,
        Guid MemberId) : IAsyncDisposable
    {
        /// <summary>The access settings endpoint, called from a browser holding <paramref name="session"/>.</summary>
        public AccessController Settings(UserSession? session) => new(
            Access,
            ApiKey,
            ClaimWindow,
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>(),
            Sessions)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = App,
                    Items = { ["Session"] = session }
                }
            }
        };

        /// <summary>The first-admin endpoint, built the way the app's container builds it.</summary>
        public AccountSetupController AccountSetup() => new(
            Database.Factory,
            ApiKey,
            ClaimWindow,
            new PasswordHasher<UserAccount>(),
            new IdentityAuditService(Database.Factory, NullLogger<IdentityAuditService>.Instance),
            App.GetRequiredService<ISignalRNotificationService>(),
            NullLogger<AccountSetupController>.Instance,
            Access)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = App } }
        };

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
