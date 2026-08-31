using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the shared admin session used while authentication is disabled: the one every cookie-less caller
/// is handed, including the hubs, which read a raw token off the request and validate it themselves rather
/// than going through the request handler. Two things can go wrong with it. Its token can be rotated out
/// from under the cache that hands it out, and it can be asked for over and over while the database is
/// unreachable.
/// </summary>
// Shares the process-wide auth-disabled session (SessionService.cs:44) with DisabledAuthenticationAccessTests,
// which is already in this collection, so it runs where that class cannot run beside it.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SessionAuthenticationHandlerTests
{
    [Fact]
    public async Task RotatedSharedSession_StillHandsOutATokenThatValidates()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, NullLogger<SessionService>.Instance);

        var first = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(first);

        // What GET /api/auth/status does to every session it resolves, the shared one included.
        var rotated = await service.RotateSessionTokenAsync(first!.Value.Session, new DefaultHttpContext());
        Assert.False(string.IsNullOrEmpty(rotated));

        await ExpirePreviousTokenGraceAsync(database, first.Value.Session.Id);

        // The token the previous holder was given is genuinely dead now, so the assertion below can only
        // pass because the cache was updated rather than because the 30-second grace is still running.
        Assert.Null(await service.ValidateSessionAsync(first.Value.RawToken));

        var reissued = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(reissued);
        Assert.Equal(rotated, reissued!.Value.RawToken);
        Assert.NotNull(await service.ValidateSessionAsync(reissued.Value.RawToken));
    }

    /// <summary>
    /// The hubs do not go through the request handler. All six read the cookie and call
    /// ValidateSessionAsync themselves, and that is the call this rotation used to break.
    /// </summary>
    [Fact]
    public async Task RotatedSharedSession_AuthenticatesAHubReadingTheCookie()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, NullLogger<SessionService>.Instance);

        var first = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(first);
        await service.RotateSessionTokenAsync(first!.Value.Session, new DefaultHttpContext());
        await ExpirePreviousTokenGraceAsync(database, first.Value.Session.Id);

        var current = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(current);

        var cookieRequest = new DefaultHttpContext();
        cookieRequest.Request.Headers.Cookie = $"LancacheManager.Session={current!.Value.RawToken}";
        var fromCookie = SessionService.TokenFromCookie(cookieRequest);
        Assert.NotNull(fromCookie);

        var cookieSession = await service.ValidateSessionAsync(fromCookie!);

        Assert.NotNull(cookieSession);
        Assert.Equal(SessionType.Admin, cookieSession!.SessionType);
    }

    /// <summary>
    /// The request handler's own path: a request arriving without a cookie is authenticated and handed a
    /// cookie, and that cookie is one the next request can be authenticated with. This is what the login
    /// and logout routes rely on when they validate the cookie the browser sends back.
    /// </summary>
    [Fact]
    public async Task RotatedSharedSession_LeavesTheHandlerIssuingAUsableCookie()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, NullLogger<SessionService>.Instance);

        var first = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(first);
        await service.RotateSessionTokenAsync(first!.Value.Session, new DefaultHttpContext());
        await ExpirePreviousTokenGraceAsync(database, first.Value.Session.Id);

        var (handler, context) = await CreateHandlerAsync(service, cookieHeader: null);
        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded);
        Assert.True(context.Items.ContainsKey("Session"));

        var issued = IssuedSessionCookie(context);
        Assert.NotNull(issued);
        Assert.NotNull(await service.ValidateSessionAsync(issued!));
    }

    /// <summary>
    /// While the database is unreachable every anonymous request lands on the shared session. Repeating the
    /// read for each one costs a round trip that cannot succeed and a copy of one error, so the outage
    /// turns into a log nobody can read. The first failure still carries its exception in full.
    /// </summary>
    [Fact]
    public async Task UnreachableDatabase_StopsRetryingAndReportingOnEveryAnonymousRequest()
    {
        ResetSharedAuthDisabledSession();
        var factory = new SwitchableDbContextFactory();
        var logger = new CapturingLogger<SessionService>();
        var service = NewSessionService(factory, logger);

        for (var i = 0; i < 25; i++)
        {
            Assert.Null(await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()));
        }

        Assert.Equal(1, factory.CreateCount);

        var reported = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.NotNull(reported.Exception);
    }

    /// <summary>
    /// A request can pass the fast hold-off check and then wait behind the request that discovers the
    /// outage. It must check again after entering the shared lock, otherwise it repeats the database call
    /// that just failed.
    /// </summary>
    [Fact]
    public async Task HoldOffThatStartsWhileRequestWaits_SkipsDatabaseAttempt()
    {
        ResetSharedAuthDisabledSession();
        var factory = new SwitchableDbContextFactory();
        var service = NewSessionService(factory, NullLogger<SessionService>.Instance);
        var gate = Assert.IsType<SemaphoreSlim>(
            SessionServiceField("_authDisabledAdminLock").GetValue(null));

        await gate.WaitAsync();
        try
        {
            var waiting = service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
            Assert.False(waiting.IsCompleted);

            SessionServiceField("_authDisabledRetryAfterUtc")
                .SetValue(null, DateTime.UtcNow.AddMinutes(1));
            gate.Release();

            Assert.Null(await waiting);
            Assert.Equal(0, factory.CreateCount);
        }
        finally
        {
            if (gate.CurrentCount == 0)
            {
                gate.Release();
            }
            ResetSharedAuthDisabledSession();
        }
    }

    /// <summary>
    /// Disabled authentication always resolves the one shared admin session. A stale cookie must not take
    /// a separate validation path that bypasses the shared outage hold-off on every request.
    /// </summary>
    [Fact]
    public async Task AuthDisabledCookieTraffic_UsesTheSharedOutageGate()
    {
        ResetSharedAuthDisabledSession();
        var factory = new SwitchableDbContextFactory();
        var logger = new CapturingLogger<SessionService>();
        var service = NewSessionService(factory, logger);

        for (var i = 0; i < 25; i++)
        {
            var (handler, _) = await CreateHandlerAsync(
                service, "LancacheManager.Session=stale-session-token");
            var result = await handler.AuthenticateAsync();
            Assert.True(result.None);
        }

        Assert.Equal(1, factory.CreateCount);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// The gate is a hold-off, not a latch: once the database answers again the service resolves the shared
    /// session on its own, with no restart. Advancing the hold-off stands in for waiting it out so the test
    /// does not spend real seconds sleeping.
    /// </summary>
    [Fact]
    public async Task DatabaseComingBack_ResolvesTheSharedSessionWithoutARestart()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var factory = new SwitchableDbContextFactory();
        var service = NewSessionService(factory, NullLogger<SessionService>.Instance);

        Assert.Null(await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()));

        factory.Recover(database.Factory);
        Assert.Null(await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()));

        ClearAuthDisabledRetryHoldOff();

        var resolved = await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(resolved);
        Assert.NotNull(await service.ValidateSessionAsync(resolved!.Value.RawToken));
    }

    /// <summary>
    /// The shared session must not turn a token nobody issued into a valid one. Direct validation stays
    /// fail-closed whatever the fallback does for cookie-less callers.
    /// </summary>
    [Fact]
    public async Task SharedSession_DoesNotMakeAnUnknownTokenValid()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var service = NewSessionService(database.Factory, NullLogger<SessionService>.Instance);

        Assert.NotNull(await service.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()));

        Assert.Null(await service.ValidateSessionAsync("a-token-nobody-issued"));
    }

    // A real StateService, in a directory of its own per service: the shared session's id is recorded
    // there so a restart can adopt the same session, and every test here starts from an empty one.
    private static SessionService NewSessionService(
        IDbContextFactory<AppDbContext> dbContextFactory, ILogger<SessionService> logger) =>
        new(
            dbContextFactory,
            apiKeyService: null!,
            logger,
            StateTestMethods.CreateStateService(
                Path.Combine(Path.GetTempPath(), $"lcm-session-handler-{Guid.NewGuid():N}")),
            signalR: null!,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:EnableAuthentication"] = "false"
                })
                .Build());

    private static async Task ExpirePreviousTokenGraceAsync(TestDatabase database, Guid sessionId)
    {
        await using var context = database.Factory.CreateDbContext();
        var session = await context.UserSessions.FirstAsync(s => s.Id == sessionId);
        session.PreviousTokenValidUntilUtc = DateTime.UtcNow.AddSeconds(-1);
        await context.SaveChangesAsync();
    }

    private static string? IssuedSessionCookie(HttpContext context) =>
        context.Response.Headers.SetCookie
            .Select(header => header ?? string.Empty)
            .Where(header => header.StartsWith("LancacheManager.Session=", StringComparison.Ordinal))
            .Select(header => header["LancacheManager.Session=".Length..].Split(';')[0])
            .LastOrDefault();

    private static async Task<(SessionAuthenticationHandler Handler, DefaultHttpContext Context)> CreateHandlerAsync(
        SessionService sessionService, string? cookieHeader)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(sessionService);
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        if (cookieHeader != null)
        {
            context.Request.Headers.Cookie = cookieHeader;
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
    /// The shared session and its hold-off are process-wide on purpose: the service is scoped, so anything
    /// that must survive a request has to be static. That also means one test's leftovers would decide the
    /// next test's answer, so each test starts from the state a freshly started process has.
    /// </summary>
    private static void ResetSharedAuthDisabledSession()
    {
        SessionServiceField("_authDisabledAdminSession").SetValue(null, null);
        ClearAuthDisabledRetryHoldOff();
    }

    private static void ClearAuthDisabledRetryHoldOff() =>
        SessionServiceField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);

    private static FieldInfo SessionServiceField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

    /// <summary>
    /// Refuses to hand out a context until <see cref="Recover"/> is called, and counts how many times it was
    /// asked. The count is what says whether the outage path stopped retrying.
    /// </summary>
    private sealed class SwitchableDbContextFactory : IDbContextFactory<AppDbContext>
    {
        private IDbContextFactory<AppDbContext>? _working;

        public int CreateCount { get; private set; }

        public void Recover(IDbContextFactory<AppDbContext> working) => _working = working;

        public AppDbContext CreateDbContext()
        {
            CreateCount++;
            return _working?.CreateDbContext()
                ?? throw new InvalidOperationException("context creation failed");
        }

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            CreateCount++;
            return _working?.CreateDbContextAsync(cancellationToken)
                ?? Task.FromException<AppDbContext>(
                    new InvalidOperationException("context creation failed"));
        }
    }

}
