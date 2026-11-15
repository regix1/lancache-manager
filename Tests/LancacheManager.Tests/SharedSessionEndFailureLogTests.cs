using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// While authentication is off, the session a browser's cookie held ends before the shared session's
/// cookie replaces it. When the database refuses that write, the cookie was already validated against a
/// database that answered, so the log has to name the step that failed, not an unreachable database.
/// </summary>
// Shares the process-wide auth-disabled session (SessionService.cs:44) with SharedSessionReplacesCookieTests,
// so it runs in their collection.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SharedSessionEndFailureLogTests
{
    [Fact]
    public async Task AuthenticationOff_TheHeldSessionsEndIsRefused_LogsTheEndNotAnUnreachableDatabase()
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var refusal = new RevokeRefusal();
        var contexts = new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>(database.Options).AddInterceptors(refusal).Options);
        var root = Path.Combine(Path.GetTempPath(), $"lcm-shared-end-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKeyPath"] = Path.Combine(root, "api_key.txt"),
                ["Security:EnableAuthentication"] = "false"
            })
            .Build();
        var state = StateTestMethods.CreateStateService(root);
        var apiKeys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver: null!);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, CacheScanDetectionPhaseTests.RecordingNotifications>();
        var sessions = new SessionService(
            contexts, apiKeys, NullLogger<SessionService>.Instance, state, notifications, configuration);

        var member = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "member",
            Role = SessionType.User,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        await using (var context = contexts.CreateDbContext())
        {
            context.UserAccounts.Add(member);
            await context.SaveChangesAsync();
        }
        var (heldToken, heldSession) = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member);

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(sessions);
        services.AddSingleton(new AccessService(state, configuration, contexts));
        await using var requestServices = services.BuildServiceProvider();
        var request = new DefaultHttpContext { RequestServices = requestServices };
        request.Request.Headers.Cookie = $"LancacheManager.Session={heldToken}";
        var logger = new CapturingLogger<SessionAuthenticationHandler>();
        var handler = new SessionAuthenticationHandler(
            requestServices.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            new HandlerLogs(logger),
            UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            request);

        try
        {
            var result = await handler.AuthenticateAsync();

            Assert.True(refusal.Refused, "the held session's end never reached the database, so this run proves nothing about its log line");
            Assert.True(result.None);
            Assert.False((await sessions.GetSessionByIdAsync(heldSession.Id))!.IsRevoked);
            Assert.DoesNotContain(
                logger.Entries,
                entry => entry.Message.StartsWith("Could not reach the database to validate a session cookie", StringComparison.Ordinal));
            Assert.Contains(
                logger.Entries,
                entry => entry.Level == LogLevel.Error && ReferenceEquals(entry.Exception, refusal.Thrown));
        }
        finally
        {
            ResetSharedAuthDisabledSession();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void ResetSharedAuthDisabledSession()
    {
        SessionServiceField("_authDisabledAdminSession").SetValue(null, null);
        SessionServiceField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo SessionServiceField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");

    // The handler's base class asks its factory for the logger it reports through.
    private sealed class HandlerLogs(ILogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;

        void ILoggerFactory.AddProvider(ILoggerProvider _) { }

        public void Dispose() { }
    }

    // Refuses the write that marks a session revoked; every other write goes through.
    private sealed class RevokeRefusal : SaveChangesInterceptor
    {
        public bool Refused { get; private set; }

        public InvalidOperationException Thrown { get; } = new("The database refused the revoke");

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventContext, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventContext.Context!.ChangeTracker.Entries<UserSession>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.IsRevoked))
            {
                Refused = true;
                throw Thrown;
            }
            return ValueTask.FromResult(result);
        }
    }
}
