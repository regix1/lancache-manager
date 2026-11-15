using System.Reflection;
using System.Text.Encodings.Web;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
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

public sealed partial class PrefillContainerOrchestrationTests
{
    // With authentication off, a browser that still holds a User account cookie from before the switch is
    // handed the shared session on its next request, and the account session ends. That window's live
    // connection was opened under the account session. When the answer reaches the window before the old
    // connection is closed, the window applies the shared session, finds its connection still open and keeps
    // it; the close then lands and the header shows Reconnecting for about 10 s. So by the time the request
    // answers, the old session's live connection is closed and UserSessionRevoked has gone out. Only the
    // prefill's container kills may run after the answer.
    //
    // Both orders run: the work done in a scope of its own (where the detached end resolves its session
    // service) is held until the request has answered, and the same work let run at once.
    [Fact]
    public async Task SwapToTheSharedSession_ClosesTheOldSessionsLiveConnectionBeforeItAnswers()
    {
        foreach (var holdScopedWorkUntilAnswered in new[] { true, false })
        {
            var order = holdScopedWorkUntilAnswered
                ? "work in a scope of its own held until the request answered"
                : "work in a scope of its own let run at once";
            var scene = await StartAccountPrefillAsync(SessionType.User);
            await using var app = scene.App;
            var configuration = app.GetRequiredService<IConfiguration>();
            configuration["Security:EnableAuthentication"] = "false";

            // The window's live connection, opened under the account session while authentication was on.
            var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            connections.RegisterConnection(scene.Session.Id, "window-connection", () => closed.TrySetResult());
            var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
            var recorder = (EventRecordingNotificationsProxy)(object)notifications;
            var sessions = new SessionService(
                app.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                app.GetRequiredService<ApiKeyService>(),
                NullLogger<SessionService>.Instance,
                app.GetRequiredService<StateService>(),
                notifications,
                configuration,
                connectionTrackingService: connections);

            // The request resolves its session service from the request services; a scope of its own resolves
            // it again, and that resolution waits on the gate.
            using var scopedWork = new ManualResetEventSlim(initialState: !holdScopedWorkUntilAnswered);
            var resolutions = 0;
            var requestServices = new ServiceCollection();
            requestServices.AddOptions();
            requestServices.AddScoped(_ =>
            {
                if (Interlocked.Increment(ref resolutions) > 1)
                    scopedWork.Wait();
                return sessions;
            });
            requestServices.AddSingleton(new AccessService(
                app.GetRequiredService<StateService>(), configuration, app.GetRequiredService<IDbContextFactory<AppDbContext>>()));
            requestServices.AddSingleton(app.GetRequiredService<SteamDaemonService>());
            requestServices.AddSingleton(app.GetRequiredService<EpicPrefillDaemonService>());
            requestServices.AddSingleton(app.GetRequiredService<XboxPrefillDaemonService>());
            requestServices.AddSingleton(app.GetRequiredService<BattleNetDaemonService>());
            requestServices.AddSingleton(app.GetRequiredService<RiotDaemonService>());
            await using var request = requestServices.BuildServiceProvider();

            var statusRead = new DefaultHttpContext { RequestServices = request };
            statusRead.Request.Headers.Cookie = $"{SessionCookieName}={scene.Token}";
            var handler = new SessionAuthenticationHandler(
                request.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
                NullLoggerFactory.Instance,
                UrlEncoder.Default);
            await handler.InitializeAsync(
                new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
                statusRead);

            // What the window could see at the moment the request answered, read before the gate opens.
            var atAnswer = handler.AuthenticateAsync().ContinueWith(
                authenticate =>
                {
                    var seen = (
                        Result: authenticate.Result,
                        ConnectionClosed: closed.Task.IsCompleted,
                        RevokedSent: recorder.EventNames.Contains(SignalREvents.UserSessionRevoked));
                    scopedWork.Set();
                    return seen;
                },
                TaskContinuationOptions.ExecuteSynchronously);
            var seen = await atAnswer;

            Assert.True(seen.Result.Succeeded, $"{order}: the request carrying the User account cookie was not authenticated");
            Assert.True(
                sessions.IsSharedAdminSession(Assert.IsType<UserSession>(statusRead.Items["Session"]).Id),
                $"{order}: the request carrying the User account cookie was not handed the shared session");
            Assert.True(
                seen.ConnectionClosed,
                $"{order}: with authentication off, the request carrying a User account cookie answered while that session's live connection was still open, so the window keeps the old connection and shows Reconnecting when it closes");
            Assert.True(
                seen.RevokedSent,
                $"{order}: with authentication off, the request carrying a User account cookie answered before UserSessionRevoked went out for that session");

            // The rest of the end still runs after the answer.
            await closed.Task;
        }
    }

    // With authentication off every browser presents the shared session's own cookie. That session is
    // everyone's, so the request ends nothing: the shared row stays live, its live connections stay open and
    // no UserSessionRevoked goes out. Ending it would drop every window's connection on every request.
    [Fact]
    public async Task SharedSessionCookieWithAuthenticationOff_EndsNothing()
    {
        var scene = await StartAccountPrefillAsync(SessionType.User);
        await using var app = scene.App;
        var configuration = app.GetRequiredService<IConfiguration>();
        configuration["Security:EnableAuthentication"] = "false";

        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, EventRecordingNotificationsProxy>();
        var recorder = (EventRecordingNotificationsProxy)(object)notifications;
        var sessions = new SessionService(
            app.GetRequiredService<IDbContextFactory<AppDbContext>>(),
            app.GetRequiredService<ApiKeyService>(),
            NullLogger<SessionService>.Instance,
            app.GetRequiredService<StateService>(),
            notifications,
            configuration,
            connectionTrackingService: connections);
        var (sharedToken, shared) = (await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext()))!.Value;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connections.RegisterConnection(shared.Id, "window-connection", () => closed.TrySetResult());

        var requestServices = new ServiceCollection();
        requestServices.AddOptions();
        requestServices.AddSingleton(sessions);
        requestServices.AddSingleton(new AccessService(
            app.GetRequiredService<StateService>(), configuration, app.GetRequiredService<IDbContextFactory<AppDbContext>>()));
        requestServices.AddSingleton(app.GetRequiredService<SteamDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<EpicPrefillDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<XboxPrefillDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<BattleNetDaemonService>());
        requestServices.AddSingleton(app.GetRequiredService<RiotDaemonService>());
        await using var request = requestServices.BuildServiceProvider();

        var statusRead = new DefaultHttpContext { RequestServices = request };
        statusRead.Request.Headers.Cookie = $"{SessionCookieName}={sharedToken}";
        var handler = new SessionAuthenticationHandler(
            request.GetRequiredService<IOptionsMonitor<AuthenticationSchemeOptions>>(),
            NullLoggerFactory.Instance,
            UrlEncoder.Default);
        await handler.InitializeAsync(
            new AuthenticationScheme(SessionAuthenticationHandler.SchemeName, null, typeof(SessionAuthenticationHandler)),
            statusRead);

        var result = await handler.AuthenticateAsync();

        Assert.True(result.Succeeded, "the request carrying the shared session's cookie was not authenticated");
        Assert.Equal(shared.Id, Assert.IsType<UserSession>(statusRead.Items["Session"]).Id);
        Assert.False(
            (await sessions.GetSessionByIdAsync(shared.Id))!.IsRevoked,
            "with authentication off, a request carrying the shared session's own cookie revoked the shared session");
        Assert.False(
            closed.Task.IsCompleted,
            "with authentication off, a request carrying the shared session's own cookie closed the shared session's live connection");
        Assert.DoesNotContain(SignalREvents.UserSessionRevoked, recorder.EventNames);
    }
}
