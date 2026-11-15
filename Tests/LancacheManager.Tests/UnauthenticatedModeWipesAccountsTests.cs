using System.Reflection;
using System.Text.Json;
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
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Unauthenticated mode keeps no accounts. Saving it in the app deletes every account, admins and
/// users, with their sessions and their sign-in links, and opens the first-admin claim again, so the
/// next switch to a signed-in mode starts by creating a new admin. A start that finds
/// <c>Security__SignInMode</c> forcing the mode does the same, since no save runs then.
/// </summary>
// Ending every session touches the process-wide shared session (SessionService.cs:44).
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class UnauthenticatedModeWipesAccountsTests : IDisposable
{
    private const string LinkedIssuer = "https://github.com";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lcm-unauth-wipe-{Guid.NewGuid():N}");

    /// <summary>The Steam, Epic and Xbox login stores the wipe clears each deleted account's game logins from.</summary>
    private readonly IntegrationFixture _logins = new();

    [Fact]
    public async Task SavingUnauthenticatedMode_LeavesNoAccountsSessionsOrSignInLinksAndReopensTheClaimAsync()
    {
        await using var scene = await SceneAsync(environmentMode: null);
        scene.ClaimWindow.Expire();

        // The owner saves unauthenticated mode, confirmed, in the access settings.
        var settings = new AccessController(
            scene.Access,
            scene.ApiKey,
            scene.ClaimWindow,
            new OidcChallengeStore(TimeProvider.System),
            new OptionsCache<OpenIdConnectOptions>(),
            new OptionsCache<OAuthOptions>(),
            scene.Sessions)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { RequestServices = scene.App, Items = { ["Session"] = scene.OwnerSession } }
            }
        };
        var saved = await settings.SetUpAsync(ConfirmingAccountWipe(new AccessSetupRequest
        {
            Mode = AccountMode.Unauthenticated,
            AcknowledgeUnauthenticated = true,
            ApiKey = scene.ApiKey.GetApiKey()
        }, accountCount: 2));
        Assert.True(
            saved.Result is OkObjectResult,
            $"saving the confirmed unauthenticated mode was answered {(saved.Result as ObjectResult)?.StatusCode}");
        Assert.False(scene.Sessions.IsAuthenticationEnabled(), "saving the unauthenticated mode left sign-in on");

        var wrong = await scene.LeftoversAsync();
        if (!scene.ClaimWindow.IsOpen)
            wrong.Add("the first-admin claim is still closed, so a new admin cannot be created");
        Assert.True(wrong.Count == 0, "after unauthenticated mode was saved: " + string.Join("; ", wrong));
    }

    [Fact]
    public async Task StartWithTheEnvironmentForcingUnauthenticatedMode_DeletesEveryAccountAsync()
    {
        await using var scene = await SceneAsync(environmentMode: "unauthenticated");
        Assert.False(scene.Sessions.IsAuthenticationEnabled(), "the environment setting did not turn sign-in off");

        // The start-up step that runs for an installation in this state.
        await new FirstAdminSessionResetService(
                scene.Database.Factory,
                scene.App.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FirstAdminSessionResetService>.Instance)
            .StartAsync(CancellationToken.None);

        var wrong = await scene.LeftoversAsync();
        Assert.True(
            wrong.Count == 0,
            "after a start with Security__SignInMode forcing unauthenticated mode: " + string.Join("; ", wrong));
    }

    /// <summary>
    /// A finished setup running a saved unauthenticated mode keeps no accounts either. An account can be
    /// left behind one with no in-app save: a restart under a temporary signed-in
    /// <c>Security__SignInMode</c>, an account made then, and the variable removed. The start deletes it.
    /// </summary>
    [Fact]
    public async Task StartWithASavedUnauthenticatedModeAndAFinishedSetup_DeletesEveryAccountAsync()
    {
        await using var scene = await SceneAsync(environmentMode: null);
        scene.State.UpdateAccess(access => access.Mode = AccountMode.Unauthenticated);
        Assert.False(scene.Sessions.IsAuthenticationEnabled(), "the saved mode did not turn sign-in off");

        await new FirstAdminSessionResetService(
                scene.Database.Factory,
                scene.App.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FirstAdminSessionResetService>.Instance)
            .StartAsync(CancellationToken.None);

        var wrong = await scene.LeftoversAsync();
        Assert.True(
            wrong.Count == 0,
            "after a start with a saved unauthenticated mode and a finished setup: " + string.Join("; ", wrong));
    }

    /// <summary>
    /// A setup still in progress keeps its accounts: an installation upgraded from a version that ran
    /// with sign-in off and has not chosen a mode yet must not lose them on its first start.
    /// </summary>
    [Fact]
    public async Task StartWithSignInOffWhileSetupIsInProgress_KeepsTheAccountsAsync()
    {
        await using var scene = await SceneAsync(environmentMode: null);
        scene.State.UpdateAccess(access =>
        {
            access.Mode = AccountMode.Unauthenticated;
            access.SetupVersion = 0;
        });
        Assert.True(scene.Access.IsSetupRequired(), "the setup is not marked as still in progress");
        Assert.False(scene.Access.IsAuthenticationEnabled(), "the saved mode did not turn sign-in off");

        await new FirstAdminSessionResetService(
                scene.Database.Factory,
                scene.App.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FirstAdminSessionResetService>.Instance)
            .StartAsync(CancellationToken.None);

        await using var read = await scene.Database.Factory.CreateDbContextAsync();
        Assert.Equal(2, await read.UserAccounts.CountAsync());
    }

    /// <summary>
    /// The request the access settings send once the person has confirmed that the shown count of
    /// accounts, every account, will be deleted. It goes through JSON the way the browser's request
    /// does, so the confirmation binds by its wire name.
    /// </summary>
    internal static AccessSetupRequest ConfirmingAccountWipe(AccessSetupRequest request, int accountCount)
    {
        var body = JsonSerializer.SerializeToNode(request, JsonSerializerOptions.Web)!.AsObject();
        body["confirmedAccountCount"] = accountCount;
        return body.Deserialize<AccessSetupRequest>(JsonSerializerOptions.Web)!;
    }

    /// <summary>
    /// An installation with sign-in on: an owner and a user account, each signed in and each linked to a
    /// GitHub sign-in, the owner also as that login's owner.
    /// </summary>
    private async Task<Scene> SceneAsync(string? environmentMode)
    {
        ResetSharedSession();
        Directory.CreateDirectory(_root);
        var database = await TestDatabase.CreateAsync();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api-key.txt"),
                ["Security:SignInMode"] = environmentMode
            })
            .Build();
        var owner = Account("owner", SessionType.Admin, mainAdmin: true);
        var user = Account("member", SessionType.User, mainAdmin: false);
        await using (var seed = await database.Factory.CreateDbContextAsync())
        {
            seed.UserAccounts.AddRange(owner, user);
            await seed.SaveChangesAsync();
        }

        var state = StateTestMethods.CreateStateService(Path.Combine(_root, "state"));
        state.UpdateAccess(access =>
        {
            access.Mode = AccountMode.ApiKeyPassword;
            access.SetupVersion = AccessSettings.RequiredSetupVersion;
            access.Logins.Add(new OidcSettings
            {
                Id = "github",
                Kind = LoginKind.GitHub,
                ClientId = "github-client",
                ClientSecret = "secret",
                DisplayName = "GitHub",
                OwnerIssuer = LinkedIssuer,
                OwnerSubject = "owner-subject",
                OwnerAccountId = owner.Id,
                AccountIds = new Dictionary<string, Guid>(StringComparer.Ordinal)
                {
                    ["github-owner-subject"] = owner.Id,
                    ["github-member-subject"] = user.Id
                }
            });
        });

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
        var access = new AccessService(state, configuration, database.Factory);
        var claimWindow = new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance);
        var (_, ownerSession) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), owner);
        await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), user);

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
        return new Scene(database, app, state, sessions, access, apiKey, claimWindow, ownerSession, [owner.Id, user.Id]);
    }

    private static UserAccount Account(string username, SessionType role, bool mainAdmin) => new()
    {
        Id = Guid.NewGuid(),
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
        UserSession OwnerSession,
        Guid[] AccountIds) : IAsyncDisposable
    {
        /// <summary>Every account, account session and sign-in link of the seeded accounts still present.</summary>
        public async Task<List<string>> LeftoversAsync()
        {
            var wrong = new List<string>();
            await using var read = await Database.Factory.CreateDbContextAsync();
            var accounts = await read.UserAccounts.Select(account => account.Username).ToListAsync();
            if (accounts.Count > 0)
                wrong.Add($"{accounts.Count} accounts remain ({string.Join(", ", accounts)})");
            var accountSessions = await read.UserSessions.CountAsync(session => session.AccountId != null);
            if (accountSessions > 0)
                wrong.Add($"{accountSessions} account sessions remain");
            var links = State.GetState().Access.Logins
                .SelectMany(login => login.AccountIds.Values.Concat(login.OwnerAccountId is { } ownerId ? [ownerId] : []))
                .Count(linked => AccountIds.Contains(linked));
            if (links > 0)
                wrong.Add($"{links} sign-in links still name a deleted account");
            return wrong;
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            await Database.DisposeAsync();
        }
    }
}
