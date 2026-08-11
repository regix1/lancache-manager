using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// What the authentication ticket carries, for the two callers whose ticket changed: a request holding
/// only an X-Api-Key, which used to authenticate as an admin while carrying no session at all, and a
/// session belonging to an account, whose ticket now names it.
/// </summary>
// Shares the process-wide cached key session (SessionService.cs:50) with the other classes in this
// collection, so it runs where they cannot run beside it.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SessionTicketTests : IDisposable
{
    private const string CookieName = "LancacheManager.Session";

    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public SessionTicketTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-session-ticket-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "true"
            })
            .Build();
        _apiKeyService = new ApiKeyService(
            NullLogger<ApiKeyService>.Instance,
            _configuration,
            pathResolver: null!);

        ResetSharedApiKeySession();
    }

    /// <summary>
    /// The half of the key path that was missing: the request now carries the session every hand-rolled
    /// check reads, instead of being an admin to the policies and nobody to GetUserSession().
    /// </summary>
    [Fact]
    public async Task KeyOnlyRequest_CarriesASessionThatExistsInTheDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var (handler, context) = await CreateHandlerAsync(service, _apiKeyService.GetApiKey());

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        var session = Assert.IsType<UserSession>(context.Items["Session"]);
        Assert.NotEqual(Guid.Empty, session.Id);
        Assert.Equal(
            session.Id.ToString(),
            result.Principal!.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal("admin", result.Principal.FindFirstValue("SessionType"));

        await using var stored = database.Factory.CreateDbContext();
        Assert.NotNull(await stored.UserSessions.FirstOrDefaultAsync(s => s.Id == session.Id));
    }

    /// <summary>
    /// ⭐The criterion is not "it stopped throwing". UserPreferences.SessionId is a foreign key to
    /// UserSession.Id, so an id that resolves to no row fails the first write the caller makes rather
    /// than the read that produced it. The id the key path hands out has to own a row; the two ids it
    /// could have handed out instead must not be able to.
    /// </summary>
    [Fact]
    public async Task OnlyASessionIdThatResolvesCanOwnAPreferencesRow()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var (handler, context) = await CreateHandlerAsync(service, _apiKeyService.GetApiKey());
        await handler.AuthenticateAsync();
        var session = Assert.IsType<UserSession>(context.Items["Session"]);

        await SavePreferencesAsync(database, session.Id);

        await Assert.ThrowsAsync<DbUpdateException>(() => SavePreferencesAsync(database, Guid.Empty));
        await Assert.ThrowsAsync<DbUpdateException>(() => SavePreferencesAsync(database, Guid.NewGuid()));
    }

    /// <summary>
    /// Authentication runs on every request, so the key branch runs on every request carrying the
    /// header. A scraper polling with the key would add a session row per scrape if the handler minted
    /// one instead of resolving the shared one.
    /// </summary>
    [Fact]
    public async Task RepeatedKeyRequests_ReuseOneStoredSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var seen = new HashSet<Guid>();

        for (var request = 0; request < 10; request++)
        {
            var (handler, context) = await CreateHandlerAsync(service, _apiKeyService.GetApiKey());
            Assert.True((await handler.AuthenticateAsync()).Succeeded);
            seen.Add(Assert.IsType<UserSession>(context.Items["Session"]).Id);
        }

        Assert.Single(seen);
        await using var stored = database.Factory.CreateDbContext();
        Assert.Equal(1, await stored.UserSessions.CountAsync());
    }

    /// <summary>
    /// The input that reaches the null answer: the key is right and the database is not there. The
    /// request keeps the authentication its key earned, exactly as it did before a session was attached
    /// to this path, rather than an outage turning a valid key into a 401.
    /// </summary>
    [Fact]
    public async Task DatabaseThatCannotAnswer_LeavesTheKeyCallerAuthenticated()
    {
        var service = NewSessionService(new UnreachableDatabase());
        var (handler, context) = await CreateHandlerAsync(service, _apiKeyService.GetApiKey());

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("admin", result.Principal!.FindFirstValue("SessionType"));
        Assert.False(context.Items.ContainsKey("Session"));
    }

    /// <summary>
    /// A wrong key is still refused, and refusing it must not leave a session behind.
    /// </summary>
    [Fact]
    public async Task WrongKey_IsRefusedAndAttachesNoSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var (handler, context) = await CreateHandlerAsync(service, "not-the-key");

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(context.Items.ContainsKey("Session"));
        await using var stored = database.Factory.CreateDbContext();
        Assert.Equal(0, await stored.UserSessions.CountAsync());
    }

    /// <summary>
    /// The right key on an ordinary route authenticates nobody. The header is how a script used to be
    /// an admin on all 271 routes, with no session behind it and nothing to revoke; it now reaches the
    /// API reference and its document only. No session row is written either, so the ordinary route
    /// does not even resolve the shared key session on its way to refusing the caller.
    /// </summary>
    [Fact]
    public async Task KeyOnAnOrdinaryRoute_AuthenticatesNobody()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var (handler, context) = await CreateHandlerAsync(service, _apiKeyService.GetApiKey());
        context.Request.Path = "/api/system/permissions";

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.False(context.Items.ContainsKey("Session"));
        await using var stored = database.Factory.CreateDbContext();
        Assert.Equal(0, await stored.UserSessions.CountAsync());
    }

    /// <summary>
    /// The account behind the session reaches the ticket, and the session type is that account's role.
    /// A user is used rather than an admin because admin is the enum's zero value, so an id that never
    /// arrived and a role that was never copied would both still read as admin.
    /// </summary>
    [Fact]
    public async Task TicketForAnAccountSession_NamesTheAccountAndItsRole()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var accountId = Guid.NewGuid();
        var rawToken = await SignedInSessionAsync(database, service, accountId, SessionType.User);

        var (handler, _) = await CreateHandlerAsync(service, apiKey: null, rawToken);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(accountId.ToString(), result.Principal!.FindFirstValue("AccountId"));
        Assert.Equal("user", result.Principal.FindFirstValue("SessionType"));
    }

    /// <summary>
    /// A guest, an API-key caller and the disabled-authentication session all run without an account.
    /// The claim is absent for them rather than carrying a placeholder, so nothing downstream has to
    /// know which value means "no account".
    /// </summary>
    [Fact]
    public async Task TicketForASessionWithNoAccount_CarriesNoAccountId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var rawToken = await SignedInSessionAsync(database, service, accountId: null, SessionType.Guest);

        var (handler, _) = await CreateHandlerAsync(service, apiKey: null, rawToken);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.Null(result.Principal!.FindFirstValue("AccountId"));
        Assert.Equal("guest", result.Principal.FindFirstValue("SessionType"));
    }

    private static async Task SavePreferencesAsync(TestDatabase database, Guid sessionId)
    {
        await using var context = database.Factory.CreateDbContext();
        context.UserPreferences.Add(new UserPreferences { SessionId = sessionId });
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The state the login path leaves behind: an account row, and a session that names it and carries a
    /// copy of its role. The account row is not optional - SessionService.ValidateSessionAsync rejects
    /// a session pointing at an account that does not exist.
    /// </summary>
    private async Task<string> SignedInSessionAsync(
        TestDatabase database, SessionService service, Guid? accountId, SessionType role)
    {
        var created = await service.CreateAdminSessionAsync(_apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        await using var context = database.Factory.CreateDbContext();
        if (accountId is { } id)
        {
            context.UserAccounts.Add(new UserAccount
            {
                Id = id,
                Username = "operator",
                PasswordHash = "unused-by-this-test",
                Role = role,
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        var session = await context.UserSessions.FirstAsync(s => s.Id == created!.Value.Session.Id);
        session.AccountId = accountId;
        session.SessionType = role;
        await context.SaveChangesAsync();

        return created!.Value.RawToken;
    }

    private SessionService NewSessionService(IDbContextFactory<AppDbContext> dbContextFactory) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            _configuration);

    private async Task<(SessionAuthenticationHandler Handler, DefaultHttpContext Context)> CreateHandlerAsync(
        SessionService sessionService, string? apiKey, string? rawToken = null)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton(sessionService);
        services.AddSingleton(_apiKeyService);
        services.AddSingleton<AuthenticationHelper>();
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        if (apiKey != null)
        {
            context.Request.Headers["X-Api-Key"] = apiKey;

            // The key authenticates on the API reference and its document and nowhere else, so the
            // facts about what the key path carries are asked on a path where the key path runs.
            // The route that stays open is asserted below rather than assumed here.
            context.Request.Path = "/openapi/v1.json";
        }

        if (rawToken != null)
        {
            context.Request.Headers.Cookie = $"{CookieName}={rawToken}";
        }

        var handler = new SessionAuthenticationHandler(
            provider.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(
                SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            context);

        return (handler, context);
    }

    /// <summary>
    /// The shared key session is cached in a static, because SessionService is scoped and the cache has
    /// to survive a request. Each test starts from the state a freshly started process has instead of
    /// inheriting the previous test's session.
    /// </summary>
    private static void ResetSharedApiKeySession()
    {
        SessionServiceField("_apiKeySession").SetValue(null, null);
        SessionServiceField("_apiKeySessionRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo SessionServiceField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Stands in for a database that cannot be reached, which is the only way the key path resolves no
    /// session while holding a valid key.
    /// </summary>
    private sealed class UnreachableDatabase : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            throw new InvalidOperationException("context creation failed");

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<AppDbContext>(new InvalidOperationException("context creation failed"));
    }
}
