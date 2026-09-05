using System.Reflection;
using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LancacheManager.Tests;

/// <summary>
/// The hubs decide group membership and admission themselves, outside the route authorization the
/// endpoint contract test covers. Getting a user into the wrong group is silent: the page renders and
/// then never updates, because the group is the broadcast fan-out.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountHolderHubAccessTests
{
    [Fact]
    public async Task SharedManagementAccessEndsOnlyAfterASecureModeIsSaved()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var scope = host.Application.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var shared = await sessions.GetOrCreateAuthDisabledAdminSessionAsync(new DefaultHttpContext());
        Assert.NotNull(shared);
        Assert.True(sessions.CanManage(shared.Value.Session));

        host.Application.Services.GetRequiredService<LancacheManager.Infrastructure.Services.StateService>()
            .UpdateAccess(access => access.Mode = AccountMode.Password);

        Assert.False(sessions.CanManage(shared.Value.Session));
        Assert.True(sessions.CanManage(new UserSession
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            SessionType = SessionType.User,
            ExpiresAtUtc = DateTime.UtcNow.AddHours(1)
        }));
    }

    [Fact]
    public void DisconnectingASessionAbortsEveryConnectionAndAnyLateRegistration()
    {
        var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
        var sessionId = Guid.NewGuid();
        var aborted = new List<string>();
        connections.RegisterConnection(sessionId, "first", () => aborted.Add("first"));
        connections.RegisterConnection(sessionId, "second", () => aborted.Add("second"));

        Assert.Equal(2, connections.DisconnectSession(sessionId));
        connections.RegisterConnection(sessionId, "late", () => aborted.Add("late"));

        Assert.True(connections.IsDisconnected(sessionId));
        Assert.Equal(new[] { "first", "late", "second" }, aborted.Order());
    }

    [Fact]
    public void ReplacementRemainsTrackedAcrossBothUnregisterOrderings()
    {
        foreach (var unregisterFirst in new[] { true, false })
        {
            var connections = new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance);
            var sessionId = Guid.NewGuid();
            var replacementAborted = false;
            connections.RegisterConnection(sessionId, "old");

            if (unregisterFirst)
            {
                connections.UnregisterConnection("old");
                connections.RegisterConnection(sessionId, "replacement", () => replacementAborted = true);
            }
            else
            {
                connections.RegisterConnection(sessionId, "replacement", () => replacementAborted = true);
                connections.UnregisterConnection("old");
            }

            Assert.Equal(1, connections.DisconnectSession(sessionId));
            Assert.True(replacementAborted);
        }
    }

    /// <summary>
    /// A user joins the same download-hub group as an admin, and a guest still joins the guest group.
    /// </summary>
    [Fact]
    public async Task TheDownloadHubPutsAUserInTheSameGroupAsAnAdminAndLeavesAGuestWhereItWas()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();

        var admin = await SessionCookieAsync(host, scope, SessionType.Admin);
        var user = await SessionCookieAsync(host, scope, SessionType.User);
        var guest = await SessionCookieAsync(host, scope, SessionType.Guest);

        Assert.Equal(
            new[] { DownloadHub.AuthenticatedUsersGroup, DownloadHub.AdminGroup },
            (await ConnectToDownloadHubAsync(host, scope, admin)).Groups.Joined);
        Assert.Equal(
            new[] { DownloadHub.AuthenticatedUsersGroup, DownloadHub.AdminGroup },
            (await ConnectToDownloadHubAsync(host, scope, user)).Groups.Joined);
        Assert.Equal(
            new[] { DownloadHub.AuthenticatedUsersGroup, DownloadHub.GuestGroup },
            (await ConnectToDownloadHubAsync(host, scope, guest)).Groups.Joined);
    }

    /// <summary>
    /// The snapshot a newly-connected client is seeded with is the same for a user as for an admin, and
    /// still trimmed for a guest. This is the second thing the download hub decides from the session type
    /// and it is invisible to group membership.
    /// </summary>
    [Fact]
    public async Task TheDownloadHubSeedsAUserWithTheSameActivitySnapshotAsAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();

        // A non-download entry is what separates the two snapshots; a guest sees only the download domain.
        await host.Application.Services.GetRequiredService<IActivityRegistry>()
            .ReportAsync(ActivityDomains.Schedule, "hub-access", ActivityAspects.Running, isActive: true);

        var admin = await Seeded(await ConnectToDownloadHubAsync(host, scope, await SessionCookieAsync(host, scope, SessionType.Admin)));
        var user = await Seeded(await ConnectToDownloadHubAsync(host, scope, await SessionCookieAsync(host, scope, SessionType.User)));
        var guest = await Seeded(await ConnectToDownloadHubAsync(host, scope, await SessionCookieAsync(host, scope, SessionType.Guest)));

        // Compared by domain rather than by entry count: every session created here reports its own
        // presence into the same registry, so the counts move between one connection and the next.
        Assert.Contains(ActivityDomains.Schedule, Domains(admin));
        Assert.Equal(Domains(admin), Domains(user));
        Assert.DoesNotContain(ActivityDomains.Schedule, Domains(guest));

        static SortedSet<string> Domains(ActivitySnapshot snapshot)
            => new(snapshot.Activities.Select(entry => entry.Domain), StringComparer.Ordinal);

        static Task<ActivitySnapshot> Seeded(DownloadHubConnection connection) =>
            Task.FromResult(Assert.IsType<ActivitySnapshot>(Assert.Single(connection.Clients.Recorded.Sent)));
    }

    /// <summary>
    /// A caller with no session cookie is still refused by the download hub. That is the API-key caller,
    /// which authenticates on a header the hub never reads.
    /// </summary>
    [Fact]
    public async Task TheDownloadHubStillRefusesACallerWithNoSessionCookie()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();
        var connection = await ConnectToDownloadHubAsync(host, scope, new DefaultHttpContext());

        Assert.True(connection.Context.Aborted);
        Assert.Empty(connection.Groups.Joined);
    }

    /// <summary>
    /// A user is admitted to a prefill daemon hub on the same terms as an admin, and every other caller
    /// is admitted or refused exactly as before: a guest needs an unexpired grant, and a caller with no
    /// cookie is refused.
    /// </summary>
    [Fact]
    public async Task ThePrefillDaemonHubAdmitsAUserOnTheSameTermsAsAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();

        var grantedGuest = await SessionCookieAsync(host, scope, SessionType.Guest, GrantSteamPrefill);

        foreach (var (caller, cookie, admitted) in new[]
                 {
                     ("an admin", await SessionCookieAsync(host, scope, SessionType.Admin), true),
                     ("a user", await SessionCookieAsync(host, scope, SessionType.User), true),
                     ("a guest with a grant", grantedGuest, true),
                     ("a guest without a grant", await SessionCookieAsync(host, scope, SessionType.Guest), false),
                     ("a caller with no cookie", new DefaultHttpContext(), false)
                 })
        {
            var connection = await ConnectToSteamDaemonHubAsync(host, scope, cookie);
            Assert.True(admitted != connection.Context.Aborted, $"The Steam daemon hub got {caller} wrong.");
            Assert.Equal(admitted, connection.Context.Items.ContainsKey("SessionId"));
        }
    }

    [Fact]
    public async Task AccountHoldersCanReplayPersistentProgressWithoutGainingTemporaryOrControlAccessAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var scope = host.Application.Services.CreateScope();

        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var notificationRecorder = (RecordingNotificationProxy)notifications;
        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance,
            notifications,
            host.Application.Services.GetRequiredService<IConfiguration>(),
            host.Application.Services.GetRequiredService<IPathResolver>(),
            host.Application.Services.GetRequiredService<IStateService>(),
            scope.ServiceProvider.GetRequiredService<PrefillSessionService>(),
            scope.ServiceProvider.GetRequiredService<PrefillCacheService>(),
            host.Application.Services.GetRequiredService<IOptionsMonitor<PrefillNetworkOptions>>());

        var userRequest = await SessionCookieAsync(host, scope, SessionType.User);
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var rawToken = SessionService.TokenFromCookie(userRequest);
        var userSession = await sessionService.ValidateSessionAsync(rawToken!);
        Assert.NotNull(userSession);

        var progress = new PrefillProgress
        {
            State = "downloading",
            CurrentAppName = "Shared game",
            TotalBytesTransferred = 33_200_000_000
        };
        var persistent = new DaemonSession
        {
            Id = "shared-persistent",
            UserId = Guid.NewGuid(),
            IsPersistent = true,
            IsPrefilling = true,
            LastProgress = progress,
            Status = DaemonSessionStatus.Active,
            AuthState = DaemonAuthState.Authenticated,
            Client = DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        var ownedTemporary = new DaemonSession
        {
            Id = "owned-temporary",
            UserId = userSession!.Id,
            IsTemporary = true,
            Status = DaemonSessionStatus.Active,
            Client = DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        var otherTemporary = new DaemonSession
        {
            Id = "other-temporary",
            UserId = Guid.NewGuid(),
            IsTemporary = true,
            Status = DaemonSessionStatus.Active,
            Client = DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        };
        daemon.InjectSession(persistent);
        daemon.InjectSession(ownedTemporary);
        daemon.InjectSession(otherTemporary);

        var connection = await ConnectToSteamDaemonHubAsync(host, scope, userRequest, daemon);
        await connection.Hub.SubscribeToSessionAsync(persistent.Id);

        Assert.Contains(connection.Context.ConnectionId, persistent.SubscribedConnections);
        Assert.Same(progress, connection.Hub.GetCurrentPrefillProgress(persistent.Id));
        Assert.Contains(
            nameof(ISignalRNotificationService.SendToPrefillClientRawAsync),
            notificationRecorder.InvokedMethods);
        await connection.Hub.SubscribeToSessionAsync(ownedTemporary.Id);
        await Assert.ThrowsAsync<HubException>(
            () => connection.Hub.SubscribeToSessionAsync(otherTemporary.Id));
        Assert.Throws<HubException>(() => { _ = connection.Hub.GetLastPrefillResult(persistent.Id); });
    }

    [Fact]
    public async Task GrantedGuestsCannotSubscribeToPersistentSessionsEvenWhenTheyMatchTheStoredOwnerAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var scope = host.Application.Services.CreateScope();

        var guestRequest = await SessionCookieAsync(host, scope, SessionType.Guest, GrantSteamPrefill);
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var rawToken = SessionService.TokenFromCookie(guestRequest);
        var guestSession = await sessionService.ValidateSessionAsync(rawToken!);
        Assert.NotNull(guestSession);

        var daemon = new TestableSteamDaemonService(
            NullLogger<SteamDaemonService>.Instance,
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            host.Application.Services.GetRequiredService<IConfiguration>(),
            host.Application.Services.GetRequiredService<IPathResolver>(),
            host.Application.Services.GetRequiredService<IStateService>(),
            scope.ServiceProvider.GetRequiredService<PrefillSessionService>(),
            scope.ServiceProvider.GetRequiredService<PrefillCacheService>(),
            host.Application.Services.GetRequiredService<IOptionsMonitor<PrefillNetworkOptions>>());
        daemon.InjectSession(new DaemonSession
        {
            Id = "guest-owned-persistent",
            UserId = guestSession!.Id,
            IsPersistent = true,
            Status = DaemonSessionStatus.Active,
            Client = DispatchProxy.Create<IDaemonClient, NullReturningProxy>()
        });

        var connection = await ConnectToSteamDaemonHubAsync(host, scope, guestRequest, daemon);

        Assert.False(connection.Context.Aborted);
        await Assert.ThrowsAsync<HubException>(
            () => connection.Hub.SubscribeToSessionAsync("guest-owned-persistent"));
    }

    private static void GrantSteamPrefill(UserSession session)
        => session.SteamPrefillExpiresAtUtc = DateTime.UtcNow.AddHours(1);

    private static async Task<DownloadHubConnection> ConnectToDownloadHubAsync(
        EndpointAuthorizationHost host, IServiceScope scope, HttpContext request)
    {
        var connection = new DownloadHubConnection(request);
        var hub = new DownloadHub(
            host.Application.Services.GetRequiredService<ConnectionTrackingService>(),
            scope.ServiceProvider.GetRequiredService<SessionService>(),
            host.Application.Services.GetRequiredService<IActivityRegistry>(),
            host.Application.Services.GetRequiredService<ILogger<DownloadHub>>())
        {
            Context = connection.Context,
            Groups = connection.Groups,
            Clients = DispatchProxy.Create<IHubCallerClients, CallerRecordingClients>()
        };

        connection.Clients = (CallerRecordingClients)hub.Clients;
        await hub.OnConnectedAsync();
        return connection;
    }

    private static async Task<(SteamDaemonHub Hub, RecordingHubContext Context)> ConnectToSteamDaemonHubAsync(
        EndpointAuthorizationHost host,
        IServiceScope scope,
        HttpContext request,
        SteamDaemonService? daemon = null)
    {
        var context = new RecordingHubContext(request);
        var clients = DispatchProxy.Create<IHubCallerClients, CallerRecordingClients>();
        var hub = new SteamDaemonHub(
            daemon ?? host.Application.Services.GetRequiredService<SteamDaemonService>(),
            scope.ServiceProvider.GetRequiredService<SessionService>(),
            host.Application.Services.GetRequiredService<ILogger<SteamDaemonHub>>())
        {
            Context = context,
            Groups = new RecordingGroups(),
            Clients = clients
        };

        await hub.OnConnectedAsync();
        return (hub, context);
    }

    /// <summary>
    /// A request carrying a stored session of <paramref name="sessionType"/>. Account-holder roles use
    /// the account login path, while guests use the guest path, so hub authorization sees production-shaped rows.
    /// </summary>
    private static async Task<HttpContext> SessionCookieAsync(
        EndpointAuthorizationHost host,
        IServiceScope scope,
        SessionType sessionType,
        Action<UserSession>? alsoSet = null)
    {
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var factory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        (string RawToken, UserSession Session) created;
        if (sessionType == SessionType.Guest)
        {
            var guest = await sessionService.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);
            created = guest.Value;
        }
        else
        {
            var account = new UserAccount
            {
                Id = Guid.NewGuid(),
                Username = $"hub-{Guid.NewGuid():N}",
                PasswordHash = "unused",
                Role = sessionType,
                CreatedAtUtc = DateTime.UtcNow
            };
            await using (var database = await factory.CreateDbContextAsync())
            {
                database.UserAccounts.Add(account);
                await database.SaveChangesAsync();
            }
            created = await sessionService.CreateAccountSessionAsync(new DefaultHttpContext(), account);
        }

        await using (var database = await factory.CreateDbContextAsync())
        {
            var stored = await database.UserSessions.FirstAsync(s => s.Id == created.Session.Id);
            alsoSet?.Invoke(stored);
            await database.SaveChangesAsync();
        }

        var request = new DefaultHttpContext();
        sessionService.SetSessionCookie(request, created.RawToken, DateTime.UtcNow.AddDays(1));
        request.Request.Headers.Cookie = request.Response.Headers.SetCookie.ToString().Split(';')[0];
        return request;
    }

    private sealed class DownloadHubConnection
    {
        public DownloadHubConnection(HttpContext request) => Context = new RecordingHubContext(request);

        public RecordingHubContext Context { get; }
        public RecordingGroups Groups { get; } = new();
        public CallerRecordingClients Clients { get; set; } = null!;
    }

    private sealed class RecordingHubContext : HubCallerContext
    {
        private readonly CancellationTokenSource _aborted = new();
        private readonly FeatureCollection _features = new();

        public RecordingHubContext(HttpContext request)
            => _features.Set<IHttpContextFeature>(new HubHttpContext { HttpContext = request });

        public bool Aborted { get; private set; }

        public override string ConnectionId => "account-holder-hub-access";
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features => _features;
        public override CancellationToken ConnectionAborted => _aborted.Token;

        public override void Abort() => Aborted = true;
    }

    /// <summary>What <c>HubCallerContext.GetHttpContext()</c> reads the request off.</summary>
    private sealed class HubHttpContext : IHttpContextFeature
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class RecordingGroups : IGroupManager
    {
        public List<string> Joined { get; } = [];

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Joined.Add(groupName);
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingCaller : ISingleClientProxy
    {
        public List<object?> Sent { get; } = [];

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sent.Add(args.FirstOrDefault());
            return Task.CompletedTask;
        }

        public Task<T> InvokeCoreAsync<T>(string method, object?[] args, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// The hub only ever addresses its caller, so every other member falls through to the shared
    /// null-returning stand-in.
    /// </summary>
    private class CallerRecordingClients : NullReturningProxy
    {
        public RecordingCaller Recorded { get; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_Caller" ? Recorded : base.Invoke(targetMethod, args);
    }

    private class RecordingNotificationProxy : NullReturningProxy
    {
        public List<string> InvokedMethods { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is not null)
            {
                InvokedMethods.Add(targetMethod.Name);
            }

            return base.Invoke(targetMethod, args);
        }
    }
}
