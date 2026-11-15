using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// With sign-in off every browser runs on the one shared session. That row is remade on the next
/// request of any browser (<c>Api/LancacheManager/Security/Sessions/SessionAuthenticationHandler.cs:47</c>),
/// so revoking or deleting it can change nothing anyone sees, and the Users page offers neither: the
/// server refuses both. Turning sign-in off also ends the owner's account session, so the owner's
/// browser moves to the shared session and the Users page lists that one row.
/// </summary>
// Shares the process-wide shared session (SessionService.cs:44) with SessionAuthenticationHandlerTests.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SharedSessionRevokeWithSignInOffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-shared-revoke-{Guid.NewGuid():N}");

    /// <summary>The Steam, Epic and Xbox login stores the wipe clears each deleted account's game logins from.</summary>
    private readonly IntegrationFixture _logins = new();

    [Fact]
    public async Task WithSignInOff_RevokeAndDeleteOnTheSharedSessionAreRefusedAndItStaysLiveAsync()
    {
        await using var scene = await SceneAsync(AccountMode.Unauthenticated);
        var shared = await scene.Sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(shared);
        var sharedId = shared!.Value.Session.Id;

        var revoked = StatusOf(await scene.SessionsController(shared.Value.Session).RevokeAsync(sharedId));
        var deleted = StatusOf(await scene.SessionsController(shared.Value.Session).DeleteAsync(sharedId));

        var wrong = new List<string>();
        if (revoked is >= 200 and < 300)
            wrong.Add($"Revoke on the shared session answered {revoked}");
        if (deleted is >= 200 and < 300)
            wrong.Add($"delete on the shared session answered {deleted}");
        if (await scene.Sessions.ValidateSessionAsync(shared.Value.RawToken) is null)
            wrong.Add("the shared session no longer signs its browser in, so the next request makes a new shared row that looks the same");
        Assert.True(
            wrong.Count == 0,
            "with sign-in off the shared session's row still takes Revoke and delete: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task TurningSignInOff_EndsTheOwnersAccountSessionAndThePageListsOnlyTheSharedSessionAsync()
    {
        await using var scene = await SceneAsync(AccountMode.ApiKeyPassword);

        // The owner signs in with a password while sign-in is on.
        var (ownerToken, ownerSession) = await scene.Sessions.CreateAccountSessionAsync(new DefaultHttpContext(), scene.Owner);

        // The owner turns sign-in off in the access settings.
        var settings = new AccessController(
            scene.Access,
            scene.ApiKey,
            new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance),
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>(),
            scene.Sessions)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = scene.App, Items = { ["Session"] = ownerSession } }
            }
        };
        var saved = await settings.SetUpAsync(UnauthenticatedModeWipesAccountsTests.ConfirmingAccountWipe(new AccessSetupRequest
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey()
        }, accountCount: 1));
        Assert.IsType<OkObjectResult>(saved.Result);
        Assert.False(scene.Sessions.IsAuthenticationEnabled(), "saving the unauthenticated mode left sign-in on");

        // The owner's browser makes its next request with the cookie it still holds, and its Users page reads the list.
        var next = await scene.AuthenticateAsync($"LancacheManager.Session={ownerToken}");
        var browserSession = next.Items["Session"] as UserSession;
        var list = await scene.SessionsController(browserSession!, next).GetAllAsync();
        var listed = Assert.IsType<SessionListResponse>(Assert.IsAssignableFrom<ObjectResult>(list.Result).Value);
        var live = listed.Sessions.Where(row => !row.IsRevoked && !row.IsExpired).ToList();

        var wrong = new List<string>();
        if (await scene.Sessions.ValidateSessionAsync(ownerToken) is not null)
            wrong.Add("the owner's account session is still live");
        if (browserSession!.AccountId is not null || !scene.Sessions.IsSharedAdminSession(browserSession.Id))
            wrong.Add($"the owner's browser still runs on {(browserSession.AccountId is null ? "a session" : "the account session")}, not the shared session");
        if (live.Count != 1)
            wrong.Add($"the Users page lists {live.Count} live sessions ({string.Join(", ", live.Select(row => row.AccountId is null ? "shared" : "owner account"))})");
        Assert.True(
            wrong.Count == 0,
            "after sign-in was turned off: " + string.Join("; ", wrong));
    }

    private async Task<Scene> SceneAsync(AccountMode mode)
    {
        ResetSharedSession();
        Directory.CreateDirectory(_root);
        var database = await TestDatabase.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt")
            })
            .Build();
        var state = StateTestMethods.CreateStateService(Path.Combine(_root, "state"));
        state.UpdateState(current =>
        {
            current.Access.Mode = mode;
            current.Access.SetupVersion = AccessSettings.RequiredSetupVersion;
        });
        var owner = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "seeded"
        };
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserAccounts.Add(owner);
            await seed.SaveChangesAsync();
        }

        var apiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var sessions = new SessionService(database.Factory, apiKey, NullLogger<SessionService>.Instance, state, notifications, configuration);
        var access = new AccessService(state, configuration, database.Factory);
        var app = new ServiceCollection()
            .AddOptions()
            .AddSingleton(sessions)
            .AddSingleton(access)
            .AddSingleton(new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance))
            .AddSingleton(notifications)
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .AddSingleton<LancacheManager.Infrastructure.Services.SteamAuthStorageService>(_logins.Storage)
            .AddSingleton(_logins.Epic)
            .AddSingleton(_logins.Xbox)
            .BuildServiceProvider();
        return new Scene(database, app, sessions, access, apiKey, notifications, owner);
    }

    private static int StatusOf<T>(ActionResult<T> result) =>
        Assert.IsAssignableFrom<ObjectResult>(result.Result).StatusCode ?? StatusCodes.Status200OK;

    /// <summary>The shared session is process-wide, so each run starts from a fresh process's state.</summary>
    private static void ResetSharedSession()
    {
        StaticField("_authDisabledAdminSession").SetValue(null, null);
        StaticField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo StaticField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

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
        SessionService Sessions,
        AccessService Access,
        ApiKeyService ApiKey,
        ISignalRNotificationService Notifications,
        UserAccount Owner) : IAsyncDisposable
    {
        public SessionsController SessionsController(UserSession caller, HttpContext? request = null)
        {
            request ??= new DefaultHttpContext { Items = { ["Session"] = caller } };
            request.RequestServices = App;
            return new SessionsController(Sessions, Notifications, App.GetRequiredService<IServiceScopeFactory>(), stateService: null!)
            {
                ControllerContext = new ControllerContext { HttpContext = request }
            };
        }

        public async Task<HttpContext> AuthenticateAsync(string cookie)
        {
            var request = new DefaultHttpContext { RequestServices = App };
            request.Request.Headers.Cookie = cookie;
            var handler = new SessionAuthenticationHandler(
                App.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
                NullLoggerFactory.Instance,
                UrlEncoder.Default);
            await handler.InitializeAsync(
                new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
                request);
            var result = await handler.AuthenticateAsync();
            Assert.True(result.Succeeded, "the owner's browser was not signed in on its next request");
            return request;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
