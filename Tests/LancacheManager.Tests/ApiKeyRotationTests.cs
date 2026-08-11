using System.Net;
using System.Net.Http.Json;
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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// What rotating the installation's API key does to the people signed in with it. Every session was
/// opened against the key that has just stopped being valid, so every one of them ends, guests
/// included, and the answer the caller receives is the last thing that reaches them on that session:
/// it has to carry the key they need to get back in.
///
/// With authentication on it is also how the owner takes the installation back, so every account but
/// the one that owns it goes with the sessions. That row is left exactly as it was, because signing
/// back in with the username and password it already had is what the rotation is for.
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
    /// The three kinds of session a running installation holds all end together, the account behind
    /// the signed-in one goes with them, and the key the rotation handed back opens a fresh session
    /// while the old one no longer does.
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
        var user = await SignedInAsAccountAsync(sessions, database.Factory, accountId, SessionType.User);
        var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
        Assert.NotNull(guest);
        var owner = await SignedInAsMainAdminAsync(sessions, database.Factory);

        var response = await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), RequestFor(owner.Session)));

        Assert.Null(await sessions.ValidateSessionAsync(admin.Value.RawToken));
        Assert.Null(await sessions.ValidateSessionAsync(user.RawToken));
        Assert.Null(await sessions.ValidateSessionAsync(guest!.Value.RawToken));

        await using var context = database.Factory.CreateDbContext();
        Assert.False(await context.UserAccounts.AnyAsync(a => a.Id == accountId));
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Id == owner.Session.AccountId));

        Assert.Null(await sessions.CreateAdminSessionAsync(oldKey, new DefaultHttpContext()));
        Assert.NotNull(await sessions.CreateAdminSessionAsync(response.ApiKey, new DefaultHttpContext()));
    }

    /// <summary>
    /// Two other accounts go and the owner's row stays, with the username and the password hash it
    /// already had. Reading the hash is the point of the assertion: the owner signs back in with what
    /// they already know plus the new key, so a rotation that reset the password would be the wrong
    /// behaviour even though the row survived.
    /// </summary>
    [Fact]
    public async Task RotationRemovesEveryAccountExceptTheOneThatOwnsTheInstallation()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var owner = await SignedInAsMainAdminAsync(sessions, database.Factory);
        await SeedAccountAsync(database.Factory, "alice");
        await SeedAccountAsync(database.Factory, "bob");

        UserAccount before;
        await using (var seeded = database.Factory.CreateDbContext())
        {
            Assert.Equal(3, await seeded.UserAccounts.CountAsync());
            before = await seeded.UserAccounts.SingleAsync(a => a.IsMainAdmin);
        }

        await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), RequestFor(owner.Session)));

        await using var context = database.Factory.CreateDbContext();
        var remaining = Assert.Single(await context.UserAccounts.ToListAsync());

        Assert.Equal(before.Id, remaining.Id);
        Assert.Equal(before.Username, remaining.Username);
        Assert.Equal(before.PasswordHash, remaining.PasswordHash);
        Assert.False(remaining.IsDisabled);
    }

    /// <summary>
    /// Each removed account is recorded against the session and the account that rotated the key, the
    /// way every other deletion path records one. Accounts that disappear with nothing written down
    /// leave the owner unable to tell a rotation from a database fault.
    /// </summary>
    [Fact]
    public async Task EveryAccountRemovedByARotationIsRecorded()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var owner = await SignedInAsMainAdminAsync(sessions, database.Factory);
        var alice = await SeedAccountAsync(database.Factory, "alice");
        var bob = await SeedAccountAsync(database.Factory, "bob");

        await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), RequestFor(owner.Session)));

        await using var context = database.Factory.CreateDbContext();
        var removals = await context.IdentityAuditEntries
            .Where(e => e.Event == IdentityAuditEvent.AccountDeleted)
            .ToListAsync();

        Assert.Equal(2, removals.Count);
        Assert.Contains(removals, e => e.TargetAccountId == alice);
        Assert.Contains(removals, e => e.TargetAccountId == bob);
        Assert.All(removals, e =>
        {
            Assert.Equal(owner.Session.AccountId, e.PerformedByAccountId);
            Assert.Equal(owner.Session.Id, e.PerformedBySessionId);
        });
    }

    /// <summary>
    /// The other branch. With authentication off the caller has presented the key and nothing else and
    /// is nobody in particular, so the accounts on the installation are not theirs to remove. The key
    /// still rotates.
    /// </summary>
    [Fact]
    public async Task RotationByAKeyCallerLeavesTheAccountsAlone()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: false);
        var accountId = await SeedAccountAsync(database.Factory, "alice");
        var oldKey = _apiKeyService.GetApiKey();

        var request = new DefaultHttpContext();
        request.Request.Headers["X-Api-Key"] = oldKey;
        var response = await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), request,
            authenticationEnabled: false));

        Assert.NotEqual(oldKey, response.ApiKey);

        await using var context = database.Factory.CreateDbContext();
        Assert.True(await context.UserAccounts.AnyAsync(a => a.Id == accountId));
        Assert.Empty(await context.IdentityAuditEntries
            .Where(e => e.Event == IdentityAuditEvent.AccountDeleted)
            .ToListAsync());
    }

    /// <summary>
    /// The caller rotating the key is signed out by its own request, so the key it now needs has to
    /// travel in the answer to that request. Anything less leaves the admin outside the screen that
    /// was going to show it to them.
    /// </summary>
    [Fact]
    public async Task RotationHandsTheCallerTheNewKeyAsItEndsTheirSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var oldKey = _apiKeyService.GetApiKey();

        var owner = await SignedInAsMainAdminAsync(sessions, database.Factory);

        var response = await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), RequestFor(owner.Session)));

        Assert.NotEqual(oldKey, response.ApiKey);
        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);
        Assert.True(_apiKeyService.ValidateApiKey(response.ApiKey));
        Assert.Null(await sessions.ValidateSessionAsync(owner.RawToken));
    }

    /// <summary>
    /// Who rotated the key, recorded against the session and the account that asked for it.
    /// </summary>
    [Fact]
    public async Task RotationIsRecordedAgainstTheSessionThatAskedForIt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var caller = await SignedInAsMainAdminAsync(sessions, database.Factory);
        var before = DateTime.UtcNow;

        await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), RequestFor(caller.Session)));

        var after = DateTime.UtcNow;
        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.ApiKeyRotated, entry.Event);
        Assert.Equal(caller.Session.AccountId, entry.PerformedByAccountId);
        Assert.Equal(caller.Session.Id, entry.PerformedBySessionId);
        Assert.Null(entry.TargetAccountId);
        Assert.InRange(entry.PerformedAtUtc, before, after);
    }

    /// <summary>
    /// A caller holding only the API key runs as the one session every key caller shares, and that
    /// session belongs to no account. The row still has to be written, with the account half empty
    /// rather than the write refusing it.
    /// </summary>
    /// <remarks>
    /// Authentication is off here because that is where an account-less caller can still rotate. With
    /// it on the route asks for the account that owns the installation, so a caller with no account
    /// is refused before any of this runs.
    /// </remarks>
    [Fact]
    public async Task RotationByAKeyCallerIsRecordedWithNoAccount()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: false);
        var keySession = await sessions.GetOrCreateApiKeySessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(keySession);

        var request = RequestFor(keySession!);
        request.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), request,
            authenticationEnabled: false));

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Null(entry.PerformedByAccountId);
        Assert.Equal(keySession.Id, entry.PerformedBySessionId);
    }

    /// <summary>
    /// And a caller with no session row at all. A right key that arrives while the database cannot
    /// answer is authenticated with nothing in Items (SessionAuthenticationHandler.cs:96-98), so
    /// reading the actor must not be the thing that throws.
    /// </summary>
    /// <remarks>
    /// Authentication is off for the same reason as the test above it.
    /// </remarks>
    [Fact]
    public async Task RotationWithNoSessionResolvedIsRecordedWithNoActorAtAll()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: false);

        var request = new DefaultHttpContext();
        request.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();
        var response = await RotateAsync(NewController(
            database.Factory, sessions, NewAuditService(database.Factory), request,
            authenticationEnabled: false));

        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);

        await using var context = database.Factory.CreateDbContext();
        var entry = await context.IdentityAuditEntries.SingleAsync();

        Assert.Equal(IdentityAuditEvent.ApiKeyRotated, entry.Event);
        Assert.Null(entry.PerformedByAccountId);
        Assert.Null(entry.PerformedBySessionId);
    }

    /// <summary>
    /// A trail that cannot be written must not take the rotation down with it: the key still
    /// changes, the answer still carries it, and the sessions still end.
    /// </summary>
    [Fact]
    public async Task RotationCompletesWhenTheAuditWriteFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessions = NewSessionService(database.Factory, authenticationEnabled: true);
        var oldKey = _apiKeyService.GetApiKey();
        var owner = await SignedInAsMainAdminAsync(sessions, database.Factory);

        var audit = new IdentityAuditService(
            new UnreachableDbContextFactory(), NullLogger<IdentityAuditService>.Instance);
        var response = await RotateAsync(
            NewController(database.Factory, sessions, audit, RequestFor(owner.Session)));

        Assert.NotEqual(oldKey, response.ApiKey);
        Assert.Equal(_apiKeyService.GetApiKey(), response.ApiKey);
        Assert.Null(await sessions.ValidateSessionAsync(owner.RawToken));

        await using var context = database.Factory.CreateDbContext();
        Assert.Empty(await context.IdentityAuditEntries.ToListAsync());
    }

    /// <summary>
    /// With authentication disabled every cookie-less caller runs as one shared session, and that
    /// session is now one of the rows a rotation deletes. It has to come back on the next request
    /// rather than leaving the installation without one.
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
            database.Factory, sessions, NewAuditService(database.Factory), request,
            authenticationEnabled: false));

        var first = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        var second = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Value.Session.Id, second!.Value.Session.Id);
        Assert.NotNull(await sessions.ValidateSessionAsync(first.Value.RawToken));
    }

    /// <summary>
    /// The whole thing over HTTP on the running application, which is the only way the route's own
    /// gates are exercised: claim the installation, sign in as the account that owns it, take the
    /// antiforgery token again because signing in changed who the caller is, then rotate. Calling the
    /// action directly, as every test above does, reaches the handler without meeting the policy on
    /// the controller, the claim the session carries or the antiforgery filter.
    /// </summary>
    [Fact]
    public async Task TheOwnerSignsInAndRotatesOverHttp()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var apiKeyService = host.Application.Services.GetRequiredService<ApiKeyService>();
        var oldKey = apiKeyService.GetApiKey();
        var owner = await ClaimInstallationAsync(host, oldKey);

        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
        using (var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { ApiKey = oldKey, Username = owner.Username, Password = owner.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        // The token belongs to the caller it was issued to, and the caller has just changed from
        // nobody to this account, so the one taken above no longer matches.
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);

        using var rotated = await client.PostAsync("/api/api-keys/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var body = await rotated.Content.ReadFromJsonAsync<ApiKeyRegenerateResponse>();
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.ApiKey));
        Assert.NotEqual(oldKey, body.ApiKey);
        Assert.Equal(apiKeyService.GetApiKey(), body.ApiKey);

        // The request that rotated the key ended the session that made it, so the answer above was the
        // last thing this caller was told and the key had to travel in it.
        using var afterwards = await client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);

        await ClearAccountsAsync(host);
    }

    /// <summary>
    /// The owner's way back in, over HTTP on the running application: rotate, then sign in again with
    /// the username and password the account already had plus the key the rotation handed back. This
    /// is the requirement the whole change exists for, so it is asserted rather than read off the
    /// account row, and the other account is gone by then.
    /// </summary>
    [Fact]
    public async Task TheOwnerSignsBackInWithTheSameCredentialsAfterRotating()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var apiKeyService = host.Application.Services.GetRequiredService<ApiKeyService>();
        var oldKey = apiKeyService.GetApiKey();
        var owner = await ClaimInstallationAsync(host, oldKey);
        var other = await host.NewAccountAsync();

        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
        using (var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { ApiKey = oldKey, Username = owner.Username, Password = owner.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
        using var rotated = await client.PostAsync("/api/api-keys/regenerate", null);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        var body = await rotated.Content.ReadFromJsonAsync<ApiKeyRegenerateResponse>();
        Assert.NotNull(body);

        // A client of its own, because the one above was signed out by the request it made.
        using var returning = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(returning);
        using (var signedBackIn = await returning.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { ApiKey = body!.ApiKey, Username = owner.Username, Password = owner.Password }))
        {
            Assert.Equal(HttpStatusCode.OK, signedBackIn.StatusCode);
        }

        // Read rather than signed in with: sign-in spends one of the five permits a caller address
        // gets in a minute, and this test has already spent four of them.
        var dbContextFactory = host.Application.Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var context = await dbContextFactory.CreateDbContextAsync())
        {
            Assert.False(await context.UserAccounts.AnyAsync(a => a.Username == other.Username));
        }

        await ClearAccountsAsync(host);
    }

    /// <summary>
    /// An ordinary administrator, signed in the same way on the same installation, is refused. The key
    /// is still the one it was and the caller is still signed in, so the refusal landed before anything
    /// was rotated or revoked rather than after.
    /// </summary>
    [Fact]
    public async Task AnAdministratorWhoDoesNotOwnTheInstallationIsRefused()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var apiKeyService = host.Application.Services.GetRequiredService<ApiKeyService>();
        var key = apiKeyService.GetApiKey();
        await ClaimInstallationAsync(host, key);

        using var client = await host.CreateAdminClientAsync();

        using var refused = await client.PostAsync("/api/api-keys/regenerate", null);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(key, apiKeyService.GetApiKey());

        using var stillSignedIn = await client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, stillSignedIn.StatusCode);

        await ClearAccountsAsync(host);
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
        IDbContextFactory<AppDbContext> dbContextFactory,
        SessionService sessionService,
        IdentityAuditService identityAuditService,
        HttpContext httpContext,
        bool authenticationEnabled = true) =>
        new(
            dbContextFactory,
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
        SessionService sessions, TestDbContextFactory dbContextFactory, Guid accountId, SessionType role)
    {
        var created = await sessions.CreateAdminSessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        var sessionId = created!.Value.Session.Id;
        await using var context = dbContextFactory.CreateDbContext();
        var session = await context.UserSessions.FirstAsync(s => s.Id == sessionId);
        session.SessionType = role;
        session.AccountId = accountId;
        await context.SaveChangesAsync();

        return (created.Value.RawToken, session);
    }

    /// <summary>
    /// The account that owns the installation, signed in. With authentication on it is the only
    /// caller the route admits, so every test that has to reach the handler starts here.
    /// </summary>
    private async Task<(string RawToken, UserSession Session)> SignedInAsMainAdminAsync(
        SessionService sessions, TestDbContextFactory dbContextFactory)
    {
        var accountId = Guid.NewGuid();

        await using (var context = dbContextFactory.CreateDbContext())
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = accountId,
                Username = "owner",
                PasswordHash = "hash",
                Role = SessionType.Admin,
                IsMainAdmin = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        return await SignedInAsAccountAsync(sessions, dbContextFactory, accountId, SessionType.Admin);
    }

    /// <summary>
    /// An installation with an owner, claimed the way a scripted install claims one, and the
    /// credentials to sign in as it.
    /// </summary>
    /// <remarks>
    /// The account that owns an installation is one row by database constraint and the suite's
    /// database outlives a run, so an owner left behind by an earlier run would refuse this claim.
    /// Clearing first is what makes the test repeatable. Every class sharing the database runs in this
    /// collection, so none of them is mid-flight while those rows go.
    /// </remarks>
    private static async Task<(string Username, string Password)> ClaimInstallationAsync(
        EndpointAuthorizationHost host, string apiKey)
    {
        await ClearAccountsAsync(host);

        var credentials = (Username: $"owner-{Guid.NewGuid():N}", Password: "Correct-Horse-9");
        using var client = host.Application.CreateClient();

        // No antiforgery token: this endpoint is the way in before any session exists, so it takes the
        // key in the request body instead.
        using var claimed = await client.PostAsJsonAsync(
            "/api/account-setup/first-admin",
            new AccountCredentialsRequest
            {
                Username = credentials.Username,
                Password = credentials.Password,
                ApiKey = apiKey
            });

        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        return credentials;
    }

    private static async Task ClearAccountsAsync(EndpointAuthorizationHost host)
    {
        var dbContextFactory = host.Application.Services
            .GetRequiredService<IDbContextFactory<AppDbContext>>();

        await using var context = await dbContextFactory.CreateDbContextAsync();
        await context.UserAccounts.ExecuteDeleteAsync();
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
