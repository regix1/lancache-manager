using System.Reflection;
using System.Text.Json;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Covers who GET /api/auth/status hands a session cookie to. It rotates the token of the session it
/// resolves and writes the new one back as a cookie, which is what keeps a browser's cookie fresh. An
/// X-Api-Key caller resolves the one session every key caller shares (SessionService.cs:50) and holds no
/// token to it, so the same rotation would hand it a cookie for a session it shares with the others and
/// invalidate the cookie an earlier key caller was given.
/// </summary>
// Resolving the two shared sessions writes process-wide static fields while each test here reads a
// database of its own, so this runs in the collection that already serializes the classes touching them.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AuthStatusSessionCookieTests : IDisposable
{
    private const string CookieName = "LancacheManager.Session";

    /// <summary>
    /// The status endpoint also hands out the antiforgery token, which needs the framework's own
    /// antiforgery services rather than the null stand-in the unexercised dependencies get. Nothing
    /// here reads the token; it is the real one so the call under test runs the way it runs in the app.
    /// </summary>
    private static readonly AntiforgeryToken _antiforgeryToken = new ServiceCollection()
        .AddLogging()
        .AddDataProtection().Services
        .AddAntiforgery()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
        .AddSingleton<AntiforgeryToken>()
        .BuildServiceProvider()
        .GetRequiredService<AntiforgeryToken>();

    private readonly string _root;
    private readonly ApiKeyService _apiKeyService;
    private readonly StateService _stateService;

    public AuthStatusSessionCookieTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"lcm-auth-status-cookie-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);

        var pathResolver = new TempDirPathResolver(_root);
        var configuration = Configuration(authenticationEnabled: true);
        _apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
        var encryption = new SecureStateEncryptionService(
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys"))),
            _apiKeyService,
            NullLogger<SecureStateEncryptionService>.Instance);
        _stateService = new StateService(
            NullLogger<StateService>.Instance,
            pathResolver,
            encryption,
            new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption));

        ResetSharedSessions();
    }

    /// <summary>
    /// The key caller's session is shared with every other key caller and nobody holds its token, so a
    /// cookie for it is both a credential handed to the wrong caller and one the next status call breaks.
    /// The key authenticates each request on its own, so nothing is lost by withholding it.
    /// </summary>
    [Fact]
    public async Task KeyCaller_IsHandedNoSessionCookieAndLeavesTheSharedTokenAlone()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: true);
        var session = await service.GetOrCreateApiKeySessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(session);
        var storedToken = await StoredTokenHashAsync(database, session!.Id);

        var context = RequestFor(session);
        context.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();

        await StatusAsync(service, database, context);

        Assert.Null(IssuedSessionCookie(context));
        Assert.Equal(storedToken, await StoredTokenHashAsync(database, session.Id));
    }

    /// <summary>
    /// Rotation is what keeps a browser's cookie moving, and the cookie carrying the new token is the
    /// whole point of it. This is the regression the key-caller rule above could cause.
    /// </summary>
    [Fact]
    public async Task AdminCookieCaller_IsHandedTheRotatedCookie()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: true);
        var created = await service.CreateAdminSessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        await AssertCookieCallerIsRotatedAsync(service, database, created!.Value.RawToken, created.Value.Session);
    }

    /// <summary>
    /// A guest browser holds the same kind of cookie and depends on the same rotation.
    /// </summary>
    [Fact]
    public async Task GuestCookieCaller_IsHandedTheRotatedCookie()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: true);
        var created = await service.CreateGuestSessionAsync(new DefaultHttpContext());
        Assert.NotNull(created);

        await AssertCookieCallerIsRotatedAsync(service, database, created!.Value.RawToken, created.Value.Session);
    }

    /// <summary>
    /// A request carrying both a cookie and the key runs as its cookie session, because the request handler
    /// reads the key only once the cookie has produced nothing (SessionAuthenticationHandler.cs:65). The
    /// call site cannot tell those two apart without revalidating the cookie, so it treats the header as the
    /// answer and leaves the session alone. The caller keeps the cookie and the token it arrived with.
    /// </summary>
    [Fact]
    public async Task CookieCallerSendingTheKeyToo_KeepsTheTokenItArrivedWith()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: true);
        var created = await service.CreateAdminSessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        var context = RequestFor(created!.Value.Session);
        context.Request.Headers.Cookie = $"{CookieName}={created.Value.RawToken}";
        context.Request.Headers["X-Api-Key"] = _apiKeyService.GetApiKey();

        await StatusAsync(service, database, context);

        Assert.Null(IssuedSessionCookie(context));
        Assert.NotNull(await service.ValidateSessionAsync(created.Value.RawToken));
    }

    /// <summary>
    /// With authentication disabled the shared admin session's cookie is the credential every browser
    /// runs on: the hubs and the preferences routes read it, and the request handler sets it on every
    /// response (SessionAuthenticationHandler.cs:122). A request arriving before the browser has the
    /// cookie still has to be handed one, so this path keeps rotating and keeps writing the cookie.
    /// </summary>
    [Fact]
    public async Task AuthDisabledCaller_IsHandedACookieWithoutSendingOne()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: false);
        var shared = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(shared);

        var context = RequestFor(shared!.Value.Session);
        await StatusAsync(service, database, context);

        var issued = IssuedSessionCookie(context);
        Assert.NotNull(issued);
        Assert.NotNull(await service.ValidateSessionAsync(issued!));
    }

    /// <summary>
    /// The cookie is HttpOnly so that script cannot read the session token. Answering the same token in
    /// the body of this response undoes that, so the body is checked for the token as a value rather than
    /// for a field by name: a token that reappears under any name is the same leak. [77b]
    /// </summary>
    [Fact]
    public async Task CookieCaller_IsAnsweredABodyCarryingNoRawToken()
    {
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, authenticationEnabled: true);
        var created = await service.CreateAdminSessionAsync(
            _apiKeyService.GetApiKey(), new DefaultHttpContext());
        Assert.NotNull(created);

        var context = RequestFor(created!.Value.Session);
        context.Request.Headers.Cookie = $"{CookieName}={created.Value.RawToken}";

        var status = await StatusAsync(service, database, context);
        var body = JsonSerializer.Serialize(status);

        var issued = IssuedSessionCookie(context);
        Assert.NotNull(issued);
        Assert.DoesNotContain(issued!, body, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Value.RawToken, body, StringComparison.Ordinal);
    }

    private async Task AssertCookieCallerIsRotatedAsync(
        SessionService service, TestDatabase database, string rawToken, UserSession session)
    {
        var context = RequestFor(session);
        context.Request.Headers.Cookie = $"{CookieName}={rawToken}";

        await StatusAsync(service, database, context);

        var issued = IssuedSessionCookie(context);
        Assert.NotNull(issued);
        Assert.NotEqual(rawToken, issued);
        Assert.NotNull(await service.ValidateSessionAsync(issued!));
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

    private async Task<AuthStatusResponse> StatusAsync(
        SessionService service, TestDatabase database, HttpContext context)
    {
        var controller = new AuthController(
            service,
            NullLogger<AuthController>.Instance,
            database.Factory,
            _stateService,
            signalR: null!,
            apiKeyService: null!,
            passwordHasher: null!,
            accountLockout: null!,
            identityAuditService: null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var result = await controller.GetStatusAsync(_antiforgeryToken);
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<AuthStatusResponse>(ok.Value);
    }

    private static async Task<string> StoredTokenHashAsync(TestDatabase database, Guid sessionId)
    {
        await using var context = database.Factory.CreateDbContext();
        return (await context.UserSessions.AsNoTracking().FirstAsync(s => s.Id == sessionId)).SessionTokenHash;
    }

    private static string? IssuedSessionCookie(HttpContext context) =>
        context.Response.Headers.SetCookie
            .Select(header => header ?? string.Empty)
            .Where(header => header.StartsWith($"{CookieName}=", StringComparison.Ordinal))
            .Select(header => header[$"{CookieName}=".Length..].Split(';')[0])
            .LastOrDefault();

    private SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory, bool authenticationEnabled) =>
        new(
            dbContextFactory,
            _apiKeyService,
            NullLogger<SessionService>.Instance,
            _stateService,
            signalR: null!,
            Configuration(authenticationEnabled));

    private IConfiguration Configuration(bool authenticationEnabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(_root, "api_key.txt"),
                ["Security:EnableAuthentication"] = authenticationEnabled ? "true" : "false"
            })
            .Build();

    /// <summary>
    /// Both shared sessions are held in process-wide statics, so each test starts from the state a freshly
    /// started process has instead of inheriting the previous test's leftovers.
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
