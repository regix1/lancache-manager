using System.Reflection;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the session an X-Api-Key caller runs as. Authentication runs on every request, so the key branch
/// runs on every request carrying the header: anything polling with the key - a metrics scraper, a script -
/// would add a session row per request if the session were minted rather than reused.
/// </summary>
// The cached session this class asserts on is one static field shared by the whole process
// (SessionService.cs:50), while each test here reads a database of its own. A class running beside this
// one therefore points that field at a row this database does not hold, and the next call in the loop
// below creates a second session instead of reusing the first. Sharing the collection that already
// serializes the tests over these fields is what keeps the two apart.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ApiKeySessionTests : IDisposable
{
    private readonly string _root;
    private readonly IConfiguration _configuration;
    private readonly ApiKeyService _apiKeyService;

    public ApiKeySessionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-api-key-session-{Guid.NewGuid():N}");
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
    /// The property the whole design rests on. A test that only asserted a session came back would pass
    /// against a version that inserted a row per call, which is what the table grows by once a scraper
    /// starts polling with the key.
    /// </summary>
    [Fact]
    public async Task RepeatedCalls_ResolveOneStoredSession()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var apiKey = _apiKeyService.GetApiKey();

        var first = await service.GetOrCreateApiKeySessionAsync(apiKey, new DefaultHttpContext());
        Assert.NotNull(first);

        for (var call = 0; call < 25; call++)
        {
            var again = await service.GetOrCreateApiKeySessionAsync(apiKey, new DefaultHttpContext());
            Assert.NotNull(again);
            Assert.Equal(first!.Id, again!.Id);
        }

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(1, await context.UserSessions.CountAsync());
    }

    /// <summary>
    /// The id has to resolve to a stored row, because UserPreferences.SessionId is a foreign key to
    /// UserSession.Id: an id with no row behind it fails that write rather than the read.
    /// </summary>
    [Fact]
    public async Task ResolvedSession_IsAnAdminSessionThatExistsInTheDatabase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        var session = await service.GetOrCreateApiKeySessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());

        Assert.NotNull(session);
        Assert.NotEqual(Guid.Empty, session!.Id);
        Assert.Equal(SessionType.Admin, session.SessionType);
        Assert.NotNull(await service.GetSessionByIdAsync(session.Id));
    }

    /// <summary>
    /// Rotating the API key revokes every session, and an admin can revoke this one by hand. Either way the
    /// cached id now points at a row that must not be handed back out as a live credential.
    /// </summary>
    [Fact]
    public async Task RevokedSession_IsReplacedRatherThanReused()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);
        var apiKey = _apiKeyService.GetApiKey();

        var first = await service.GetOrCreateApiKeySessionAsync(apiKey, new DefaultHttpContext());
        Assert.NotNull(first);
        Assert.True(await service.RevokeSessionAsync(first!.Id));

        var replacement = await service.GetOrCreateApiKeySessionAsync(apiKey, new DefaultHttpContext());

        Assert.NotNull(replacement);
        Assert.NotEqual(first.Id, replacement!.Id);
        Assert.False(replacement.IsRevoked);
    }

    /// <summary>
    /// The key is checked again here rather than trusted from the caller, so this method cannot become a
    /// way to obtain an admin session without one.
    /// </summary>
    [Fact]
    public async Task WrongKey_ResolvesNoSessionAndStoresNothing()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        Assert.Null(await service.GetOrCreateApiKeySessionAsync("not-the-key", new DefaultHttpContext()));

        await using var context = database.Factory.CreateDbContext();
        Assert.Equal(0, await context.UserSessions.CountAsync());
    }

    /// <summary>
    /// The auth-disabled sibling refuses to run while authentication is enabled, and the key path runs in
    /// no other state. That guard is what keeps the two shared sessions from being interchangeable.
    /// </summary>
    [Fact]
    public async Task AuthDisabledSession_StillRefusesWhileAuthenticationIsEnabled()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()));
    }

    private SessionService NewSessionService(IDbContextFactory<AppDbContext> dbContextFactory) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            _configuration);

    /// <summary>
    /// The cached session and its hold-off are process-wide on purpose: the service is scoped, so anything
    /// that must survive a request has to be static. Each test therefore starts from the state a freshly
    /// started process has, instead of inheriting the previous test's leftovers.
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
}
