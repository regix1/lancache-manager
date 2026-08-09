using System.Text.Encodings.Web;
using LancacheManager.Infrastructure.Data;
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
/// Authentication runs ahead of authorization, so SessionAuthenticationHandler sees every request
/// carrying a session cookie, including requests to endpoints that ask for no authorization at all.
/// A database that cannot be reached must therefore leave the request unauthenticated rather than
/// throw, because the screens a user needs in order to repair a broken installation are served by
/// exactly those endpoints.
/// </summary>
public class SessionAuthDatabaseOutageTests
{
    private const string SessionCookieHeader = "LancacheManager.Session=raw-session-token";

    [Fact]
    public async Task UnreachableDatabase_WithSessionCookie_LeavesTheRequestUnauthenticated()
    {
        var logger = new CapturingLogger();
        var (handler, context) = await CreateHandlerAsync(
            new ThrowingDbContextFactory(), logger, SessionCookieHeader);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        Assert.False(result.Succeeded);
        Assert.Null(result.Failure);
        Assert.False(context.Items.ContainsKey("Session"));
    }

    /// <summary>
    /// Nothing may stand in for the session the database could not confirm: no principal, no ticket,
    /// and in particular no admin claim, which is the one that opens the destructive endpoints.
    /// </summary>
    [Fact]
    public async Task UnreachableDatabase_WithSessionCookie_GrantsNoIdentityAndNoAdminClaim()
    {
        var logger = new CapturingLogger();
        var (handler, _) = await CreateHandlerAsync(
            new ThrowingDbContextFactory(), logger, SessionCookieHeader);

        var result = await handler.AuthenticateAsync();

        Assert.Null(result.Principal);
        Assert.Null(result.Ticket);
    }

    [Fact]
    public async Task UnreachableDatabase_WithSessionCookie_ReportsTheFailureAtErrorLevel()
    {
        var logger = new CapturingLogger();
        var (handler, _) = await CreateHandlerAsync(
            new ThrowingDbContextFactory(), logger, SessionCookieHeader);

        await handler.AuthenticateAsync();

        var reported = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.NotNull(reported.Exception);
    }

    /// <summary>
    /// A request with no session cookie returns before the database is ever consulted. Guards against
    /// the outage path widening into a log line on every anonymous poll, which during an outage is
    /// most of the traffic.
    /// </summary>
    [Fact]
    public async Task UnreachableDatabase_WithoutSessionCookie_ReturnsWithoutConsultingTheDatabase()
    {
        var logger = new CapturingLogger();
        var (handler, _) = await CreateHandlerAsync(
            new ThrowingDbContextFactory(), logger, cookieHeader: null);

        // Reaching this line at all is the proof: the factory throws the moment it is asked for a
        // context, so a database read on this path could not have gone unnoticed.
        var result = await handler.AuthenticateAsync();

        Assert.True(result.None);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    /// <summary>
    /// A reachable database that simply holds no matching row is a rejected session, not an outage.
    /// It must still come back as a failure so the browser is told to log in again.
    /// </summary>
    [Fact]
    public async Task ReachableDatabase_WithUnknownToken_StillRejectsTheSession()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"session_auth_outage_{Guid.NewGuid():N}")
            .Options;

        var logger = new CapturingLogger();
        var (handler, context) = await CreateHandlerAsync(
            new TestDbContextFactory(options), logger, SessionCookieHeader);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.None);
        Assert.NotNull(result.Failure);
        Assert.False(context.Items.ContainsKey("Session"));
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    private static async Task<(SessionAuthenticationHandler Handler, DefaultHttpContext Context)> CreateHandlerAsync(
        IDbContextFactory<AppDbContext> dbContextFactory,
        CapturingLogger logger,
        string? cookieHeader)
    {
        // The context factory and the configuration are the two that get exercised:
        // ValidateSessionAsync hashes the token itself and reads nothing else off the service, and the
        // handler asks whether authentication is enabled before deciding what an absent session means.
        // An empty configuration leaves that at its default of enabled, which is the mode these tests
        // are about. Every other argument is stored without being touched (SessionService.cs).
        var sessionService = new SessionService(
            dbContextFactory,
            apiKeyService: null!,
            NullLogger<SessionService>.Instance,
            stateService: null!,
            signalR: null!,
            configuration: new ConfigurationBuilder().Build());

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(sessionService);
        var provider = services.BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = provider };
        if (cookieHeader != null)
        {
            context.Request.Headers.Cookie = cookieHeader;
            // The cookie name is private to SessionService. If it ever moves, the handler would take
            // the no-token branch and these tests would pass without reaching the database at all.
            Assert.NotNull(SessionService.TokenFromCookie(context));
        }

        var handler = new SessionAuthenticationHandler(
            provider.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            logger,
            UrlEncoder.Default);

        await handler.InitializeAsync(
            new AuthenticationScheme(
                SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            context);

        return (handler, context);
    }

    /// <summary>
    /// Doubles as its own ILoggerFactory so the handler's base class resolves this exact instance for
    /// its protected Logger, which is where the handler reports the outage.
    /// </summary>
    private sealed class CapturingLogger : ILogger, ILoggerFactory
    {
        private readonly LancacheManager.Tests.CapturingLogger<object> _logger = new();

        public IReadOnlyList<LogEntry> Entries => _logger.Entries;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            _logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _logger.Log(logLevel, eventId, state, exception, formatter);

        public ILogger CreateLogger(string categoryName) => this;

        public void AddProvider(ILoggerProvider loggerProvider) { }

        public void Dispose() { }
    }

}
