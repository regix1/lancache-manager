using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
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
/// One installation for the sign-in mode tests: its database, its state file, its API key, the session
/// and access services, and the Steam, Epic and Xbox login stores. Every controller is built from the one
/// service list the app would resolve it from, so a controller that takes a new dependency still binds.
/// A restart keeps the database, the state file and the login files and builds new services over them.
/// </summary>
internal sealed class SignInModeScene : IAsyncDisposable
{
    private readonly bool _ownsInstallation;

    private SignInModeScene(
        string root,
        TestDatabase database,
        IntegrationFixture logins,
        IReadOnlyDictionary<string, string?> settings,
        bool ownsInstallation)
    {
        Root = root;
        Database = database;
        Logins = logins;
        _ownsInstallation = ownsInstallation;
        ResetSharedSession();

        var values = new Dictionary<string, string?>(settings, StringComparer.Ordinal)
        {
            ["Security:ApiKeyPath"] = Path.Combine(root, "api-key.txt")
        };
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        State = StateTestMethods.CreateStateService(Path.Combine(root, "state"));
        ApiKey = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        Sessions = new SessionService(
            database.Factory,
            ApiKey,
            NullLogger<SessionService>.Instance,
            State,
            notifications,
            configuration,
            connectionTrackingService: connections);
        Access = new AccessService(State, configuration, database.Factory);
        App = new ServiceCollection()
            .AddOptions()
            .AddLogging()
            .AddSingleton(configuration)
            .AddSingleton(State)
            .AddSingleton(ApiKey)
            .AddSingleton(Sessions)
            .AddSingleton(Access)
            .AddSingleton(new AccountClaimWindow(NullLogger<AccountClaimWindow>.Instance))
            .AddSingleton(connections)
            .AddSingleton(notifications)
            .AddSingleton(new OidcChallengeStore(TimeProvider.System))
            .AddSingleton<IPasswordHasher<UserAccount>>(new PasswordHasher<UserAccount>())
            .AddSingleton(new IdentityAuditService(database.Factory, NullLogger<IdentityAuditService>.Instance))
            .AddSingleton(new AccountLockout(NullLogger<AccountLockout>.Instance))
            .AddSingleton<SteamAuthStorageService>(logins.Storage)
            .AddSingleton(logins.Epic)
            .AddSingleton(logins.Xbox)
            .AddSingleton<IDbContextFactory<AppDbContext>>(database.Factory)
            .BuildServiceProvider();
    }

    public string Root { get; }
    public TestDatabase Database { get; }
    public IntegrationFixture Logins { get; }
    public StateService State { get; }
    public ApiKeyService ApiKey { get; }
    public SessionService Sessions { get; }
    public AccessService Access { get; }
    public ServiceProvider App { get; }

    /// <summary>
    /// A new installation whose process reads <paramref name="settings"/> as its configuration and whose
    /// state file starts with the access settings <paramref name="access"/> writes.
    /// </summary>
    public static async Task<SignInModeScene> CreateAsync(
        IReadOnlyDictionary<string, string?> settings, Action<AccessSettings> access)
    {
        var root = Path.Combine(Path.GetTempPath(), $"lcm-sign-in-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scene = new SignInModeScene(root, await TestDatabase.CreateAsync(), new IntegrationFixture(), settings, ownsInstallation: true);
        scene.State.UpdateAccess(access);
        return scene;
    }

    /// <summary>
    /// The same installation after the container restarts with <paramref name="settings"/>: the process
    /// memory is new, the database, the state file and the login files are kept.
    /// </summary>
    public SignInModeScene Restart(IReadOnlyDictionary<string, string?> settings)
        => new(Root, Database, Logins, settings, ownsInstallation: false);

    public static UserAccount Account(Guid id, string username, SessionType role, bool mainAdmin) => new()
    {
        Id = id,
        Username = username,
        Role = role,
        IsMainAdmin = mainAdmin,
        CreatedAtUtc = DateTime.UtcNow,
        PasswordHash = "seeded"
    };

    public async Task SeedAsync(params UserAccount[] accounts)
    {
        await using var seed = await Database.Factory.CreateDbContextAsync();
        seed.UserAccounts.AddRange(accounts);
        await seed.SaveChangesAsync();
    }

    /// <summary>The start-up step every start runs before the first request.</summary>
    public Task StartAsync()
        => new FirstAdminSessionResetService(
                Database.Factory,
                App.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<FirstAdminSessionResetService>.Instance)
            .StartAsync(CancellationToken.None);

    /// <summary>A controller answering a request made on <paramref name="caller"/>.</summary>
    public T Controller<T>(UserSession? caller) where T : ControllerBase
    {
        var controller = ActivatorUtilities.CreateInstance<T>(App);
        var request = new DefaultHttpContext { RequestServices = App };
        if (caller is not null)
            request.Items["Session"] = caller;
        controller.ControllerContext = new ControllerContext { HttpContext = request };
        return controller;
    }

    /// <summary>The shipped cookie check on a request that carries <paramref name="cookie"/>.</summary>
    public async Task<(AuthenticateResult Result, HttpContext Request)> AuthenticateAsync(string cookie)
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
        return (await handler.AuthenticateAsync(), request);
    }

    /// <summary>The shared session is process memory, so a new process starts without it.</summary>
    public static void ResetSharedSession()
    {
        foreach (var (name, value) in new (string, object?)[] { ("_authDisabledAdminSession", null), ("_authDisabledRetryAfterUtc", DateTime.MinValue) })
        {
            (typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException($"SessionService no longer has a static {name}."))
                .SetValue(null, value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await App.DisposeAsync();
        if (!_ownsInstallation)
            return;
        ResetSharedSession();
        await Database.DisposeAsync();
        Logins.Dispose();
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}
