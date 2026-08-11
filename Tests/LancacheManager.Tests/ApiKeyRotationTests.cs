using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What rotating the installation's API key does to the people signed in with it. Every session was
/// opened against the key that has just stopped being valid, so every one of them ends, guests
/// included, and the answer the caller receives is the last thing that reaches them on that session:
/// it has to carry the key they need to get back in. The accounts themselves are left alone, which
/// is what "sign in again" means.
/// </summary>
// The two shared sessions are held in process-wide statics that this class resets, so it runs in the
// collection that already serializes the classes touching them.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ApiKeyRotationTests : IDisposable
{
    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;
    private readonly SteamAuthStorageService _steamAuthStorage;
    private readonly AuthenticationHelper _authenticationHelper;

    public ApiKeyRotationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-api-key-rotation-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var pathResolver = new TempDirPathResolver(_root);
        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance, Configuration(authenticationEnabled: true), pathResolver);
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys"))),
            _apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        _steamAuthStorage = new SteamAuthStorageService(
            NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);
        _stateService = new StateService(
            NullLogger<StateService>.Instance, pathResolver, encryption, _steamAuthStorage);
        _authenticationHelper = new AuthenticationHelper(
            _apiKeyService, NullLogger<AuthenticationHelper>.Instance);

        ResetSharedSessions();
    }

    /// <summary>
    /// The three kinds of session a running installation holds all end together, and the account
    /// behind the signed-in one is still there afterwards and can open a fresh session with the key
    /// the rotation handed back. [48]
    /// </summary>
    [Fact]
    public async Task RotationEndsTheAdminTheUserAndTheGuestSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var oldKey = _apiKeyService.GetApiKey();

        var admin = await sessions.CreateAdminSessionAsync(oldKey, new DefaultHttpContext());
        Assert.NotNull(admin);
        var accountId = await SeedAccountAsync(database.Factory, "alice");
        var user = await SignedInAsAccountAsync(sessions, database.Factory, accountId);
        var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
        Assert.NotNull(guest);

        var response = await RotateAsync(
            NewController(sessions, NewAuditService(database.Factory), RequestFor(admin!.Value.Session)));

        Assert.Null(await sessions.ValidateSessionAsync(admin.Value.RawToken));
        Assert.Null(await sessions.ValidateSessionAsync(user.RawToken));
        Assert.Null(await sessions.ValidateSessionAsync(guest!.Value.RawToken));

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Id == accountId));

        Assert.Null(await sessions.CreateAdminSessionAsync(oldKey, new DefaultHttpContext()));
        Assert.NotNull(await sessions.CreateAdminSessionAsync(response.ApiKey, new DefaultHttpContext()));
    }

    /// <summary>
    /// The caller rotating the key is signed out by its own request, so the key it now needs has to
    /// travel in the answer to that request. Anything less leaves the admin outside the screen that
    /// was going to show it to them. [49]
    /// </summary>
    [Fact]
    public async Task RotationHandsTheCallerTheNewKeyAsItEndsTheirSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var oldKey = _apiKeyService.GetApiKey();

        var admin = await sessions.CreateAdminSessionAsync(oldKey, new DefaultHttpContext());
        Assert.NotNull(admin);

        var response = await RotateAsync(
            NewController(sessions, NewAuditService(database.Factory), RequestFor(admin!.Value.Session)));

        Assert.NotEqual(oldKey, response.ApiKey);
        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);
        Assert.True(_apiKeyService.ValidateApiKey(response.ApiKey));
        Assert.Null(await sessions.ValidateSessionAsync(admin.Value.RawToken));
    }

    /// <summary>
    /// Who rotated the key, recorded against the session and the account that asked for it. [49h]
    /// </summary>
    [Fact]
    public async Task RotationIsRecordedAgainstTheSessionThatAskedForIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var accountId = await SeedAccountAsync(database.Factory, "alice");
        var caller = await SignedInAsAccountAsync(sessions, database.Factory, accountId);
        var before = DateTime.UtcNow;

        await RotateAsync(NewController(sessions, NewAuditService(database.Factory), RequestFor(caller.Session)));

        var after = DateTime.UtcNow;
        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.ApiKeyRotated, entry.Event);
        Assert.Equal(accountId, entry.PerformedByAccountId);
        Assert.Equal(caller.Session.Id, entry.PerformedBySessionId);
        Assert.Null(entry.TargetAccountId);
        Assert.InRange(entry.PerformedAtUtc, before, after);
    }

    /// <summary>
    /// A caller holding only the API key runs as the one session every key caller shares, and that
    /// session belongs to no account. The row still has to be written, with the account half empty
    /// rather than the write refusing it. [29c]
    /// </summary>
    [Fact]
    public async Task RotationByAKeyCallerIsRecordedWithNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var keySession = await sessions.GetOrCreateApiKeySessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(keySession);

        await RotateAsync(NewController(sessions, NewAuditService(database.Factory), RequestFor(keySession!)));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Null(entry.PerformedByAccountId);
        Assert.Equal(keySession.Id, entry.PerformedBySessionId);
    }

    /// <summary>
    /// And a caller with no session row at all. A right key that arrives while the database cannot
    /// answer is authenticated with nothing in Items (SessionAuthenticationHandler.cs:96-98), so
    /// reading the actor must not be the thing that throws. [29c]
    /// </summary>
    [Fact]
    public async Task RotationWithNoSessionResolvedIsRecordedWithNoActorAtAll()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);

        var response = await RotateAsync(
            NewController(sessions, NewAuditService(database.Factory), new DefaultHttpContext()));

        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.ApiKeyRotated, entry.Event);
        Assert.Null(entry.PerformedByAccountId);
        Assert.Null(entry.PerformedBySessionId);
    }

    /// <summary>
    /// A trail that cannot be written must not take the rotation down with it: the key still
    /// changes, the answer still carries it, and the sessions still end. [29d]
    /// </summary>
    [Fact]
    public async Task RotationCompletesWhenTheAuditWriteFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var oldKey = _apiKeyService.GetApiKey();
        var admin = await sessions.CreateAdminSessionAsync(oldKey, new DefaultHttpContext());
        Assert.NotNull(admin);

        var audit = new IdentityAuditService(
            new UnreachableDbContextFactory(), NullLogger<IdentityAuditService>.Instance);
        var response = await RotateAsync(NewController(sessions, audit, RequestFor(admin!.Value.Session)));

        Assert.NotEqual(oldKey, response.ApiKey);
        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);
        Assert.Null(await sessions.ValidateSessionAsync(admin.Value.RawToken));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.IdentityAuditEntries.ToListAsync());
    }

    /// <summary>
    /// With authentication disabled every cookie-less caller runs as one shared session, and that
    /// session is now one of the rows a rotation deletes. It has to come back on the next request
    /// rather than leaving the installation without one. [27e]
    /// </summary>
    [Fact]
    public async Task RotationLeavesTheSharedAuthDisabledSessionResolvable()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: false);
        var shared = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(shared);

        var request = RequestFor(shared!.Value.Session);
        request.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        await RotateAsync(NewController(
            sessions, NewAuditService(database.Factory), request, authenticationEnabled: false));

        var first = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        var second = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Value.Session.Id, second!.Value.Session.Id);
        Assert.NotNull(await sessions.ValidateSessionAsync(first.Value.RawToken));
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

    private static async Task<ApiKeyRegenerateResponse> RotateAsync(ApiKeysController controller)
    {
        var result = await controller.RegenerateApiKeyAsync();
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<ApiKeyRegenerateResponse>(ok.Value);
    }

    /// <summary>
    /// The session the request handler resolved, published the way the handler publishes it.
    /// </summary>
    private static DefaultHttpContext RequestFor(UserSession session)
    {
        var context = new DefaultHttpContext();
        context.Items["Session"] = session;
        return context;
    }

    /// <summary>
    /// Steam is left unbuilt: clearing it is the one step the endpoint already treats as best effort
    /// (ApiKeysController.cs:119-127) because a rotation has to finish without it, and its ten
    /// dependencies are not what any of this is about. The storage and state services behind it are
    /// real, because the endpoint reads both before that point.
    /// </summary>
    private ApiKeysController NewController(
        SessionService sessionService,
        IdentityAuditService identityAuditService,
        HttpContext httpContext,
        bool authenticationEnabled = true) =>
        new(
            _apiKeyService,
            steamKit2Service: null!,
            _steamAuthStorage,
            _stateService,
            sessionService,
            identityAuditService,
            Configuration(authenticationEnabled),
            _authenticationHelper,
            NullLogger<ApiKeysController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };

    private SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory, bool authenticationEnabled) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            _stateService,
            signalR: null!,
            Configuration(authenticationEnabled));

    private static IdentityAuditService NewAuditService(IDbContextFactory<AppDbContext> dbContextFactory) =>
        new(dbContextFactory, NullLogger<IdentityAuditService>.Instance);

    private IConfiguration Configuration(bool authenticationEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled ? "true" : "false"
            })
            .Build();

    private static async Task<Guid> SeedAccountAsync(TestDbContextFactory dbContextFactory, string username)
    {
        var accountId = Guid.NewGuid();

        await using var context = dbContextFactory.CreateDbContext();
        context.UserAccounts.Add(new UserAccount
        {
            Id = accountId,
            Username = username,
            PasswordHash = "hash",
            Role = SessionType.User,
            CreatedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        return accountId;
    }

    /// <summary>
    /// A session in the shape signing in with an account leaves behind: the account's role and the
    /// account it signed in as. Built from the session creator and adjusted, because that is what
    /// hands back a usable token, and the token is what shows the session stopped working.
    /// </summary>
    private async Task<(string RawToken, UserSession Session)> SignedInAsAccountAsync(
        SessionService sessions, TestDbContextFactory dbContextFactory, Guid accountId)
    {
        var created = await sessions.CreateAdminSessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        var sessionId = created!.Value.Session.Id;
        await using var context = dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FirstAsync(s => s.Id == sessionId);
        session.SessionType = SessionType.User;
        session.AccountId = accountId;
        await context.SaveChangesAsync();

        return (created.Value.RawToken, session);
    }

    /// <summary>
    /// Both shared sessions are held in process-wide statics, so each test starts from the state a
    /// freshly started process has instead of inheriting the previous test's leftovers.
    /// </summary>
    private static void ResetSharedSessions()
    {
        SessionServiceField("_apiKeySession").SetValue(null, null);
        SessionServiceField("_apiKeySessionRetryAfterUtc").SetValue(null, DateTime.MinValue);
        SessionServiceField("_authDisabledAdminSession").SetValue(null, null);
        SessionServiceField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo SessionServiceField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
