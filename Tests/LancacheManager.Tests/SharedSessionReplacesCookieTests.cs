using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
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
/// While authentication is off, a browser whose cookie holds any session other than the shared one or
/// the main administrator's is handed the shared session's cookie instead. From then on no browser
/// holds the old cookie, so nothing can sign that session out, and an administrator or account session
/// never expires: it has to end there, and an open Users page has to be told so it stops listing it.
/// </summary>
// Shares the process-wide auth-disabled session (SessionService.cs:44) with SessionAuthenticationHandlerTests
// and DisabledAuthenticationAccessTests, so it runs in their collection.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class SharedSessionReplacesCookieTests
{
    // held null is a session that proved the API key and has no account behind it.
    [Theory]
    [InlineData(SessionType.Admin)]
    [InlineData(SessionType.User)]
    [InlineData(SessionType.Guest)]
    [InlineData(null)]
    public async Task AuthenticationOff_ACookieOfAnotherSession_EndsThatSessionAndTellsAnOpenUsersPage(SessionType? held)
    {
        ResetSharedAuthDisabledSession();
        await using var database = await TestDatabase.CreateAsync();
        var root = Path.Combine(Path.GetTempPath(), $"lcm-shared-cookie-{Guid.NewGuid():N}");
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
        var sent = (CacheScanDetectionPhaseTests.RecordingNotifications)(object)notifications;
        var sessions = new SessionService(
            database.Factory, apiKeys, NullLogger<SessionService>.Instance, state, notifications, configuration);

        var owner = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "owner",
            Role = SessionType.Admin,
            IsMainAdmin = true,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var deputy = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "deputy",
            Role = SessionType.Admin,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        var member = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = "member",
            Role = SessionType.User,
            CreatedAtUtc = DateTime.UtcNow,
            PasswordHash = "not-used-here"
        };
        await using (var context = database.Factory.CreateDbContext())
        {
            context.UserAccounts.AddRange(owner, deputy, member);
            await context.SaveChangesAsync();
        }

        // A session from before authentication was turned off, still in the browser's cookie.
        var (heldToken, heldSession) = held switch
        {
            SessionType.Admin => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), deputy),
            SessionType.User => await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), member),
            SessionType.Guest => (await sessions.CreateGuestSessionAsync(new DefaultHttpContext()))!.Value,
            _ => (await sessions.CreateAdminSessionAsync(apiKeys.GetApiKey(), new DefaultHttpContext()))!.Value
        };
        var kind = held is { } heldRole ? $"{heldRole} session" : "API key session";
        Assert.NotNull(await sessions.ValidateSessionAsync(heldToken));

        // Whether an open Users page, reading the list again after each list event, still sees the
        // held session as live (useUserSessions.ts RELOAD_EVENTS, useUserAccounts.ts).
        var heldListedAsLive = new ConcurrentQueue<bool>();
        sent.OnSent = (eventName, _) =>
        {
            if (eventName is not (SignalREvents.AccountsChanged or SignalREvents.UserSessionRevoked or SignalREvents.UserSessionsCleared))
                return;
            using var context = database.Factory.CreateDbContext();
            heldListedAsLive.Enqueue(context.UserSessions.AsNoTracking().Any(s => s.Id == heldSession.Id && !s.IsRevoked));
        };

        var services = new ServiceCollection();
        services.AddOptions();
        services.AddLogging();
        services.AddSingleton(sessions);
        services.AddSingleton(new AccessService(state, configuration, database.Factory));
        await using var requestServices = services.BuildServiceProvider();
        var request = new DefaultHttpContext { RequestServices = requestServices };
        request.Request.Headers.Cookie = $"LancacheManager.Session={heldToken}";
        var handler = new SessionAuthenticationHandler(
            requestServices.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            request);

        try
        {
            var result = await handler.AuthenticateAsync();

            Assert.True(result.Succeeded);
            var shared = Assert.IsType<UserSession>(request.Items["Session"]);
            Assert.NotEqual(heldSession.Id, shared.Id);
            Assert.Contains(
                request.Response.Headers.SetCookie,
                header => header?.StartsWith("LancacheManager.Session=", StringComparison.Ordinal) == true);
            // The held session is revoked before the request answers, so the wait below, 10 s at most, only
            // bounds how long a regression takes to fail.
            var waitUntil = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < waitUntil
                && !((await sessions.GetSessionByIdAsync(heldSession.Id))!.IsRevoked && heldListedAsLive.Any(live => !live)))
                await Task.Delay(50);
            Assert.True(
                (await sessions.GetSessionByIdAsync(heldSession.Id))!.IsRevoked,
                $"the {kind} this browser held is still live after the shared session's cookie replaced it");
            Assert.True(
                heldListedAsLive.Any(live => !live),
                $"the {kind} replaced by the shared session's cookie sent no list event after it ended, so an open Users page keeps listing it as live");
        }
        finally
        {
            ResetSharedAuthDisabledSession();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The shared session and its hold-off are process-wide (SessionService.cs:44), so each run starts
    /// from the state a freshly started process has, as SessionAuthenticationHandlerTests does.
    /// </summary>
    private static void ResetSharedAuthDisabledSession()
    {
        SessionServiceField("_authDisabledAdminSession").SetValue(null, null);
        SessionServiceField("_authDisabledRetryAfterUtc").SetValue(null, DateTime.MinValue);
    }

    private static FieldInfo SessionServiceField(string name) =>
        typeof(SessionService).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException($"SessionService no longer has a static {name}.");
}
