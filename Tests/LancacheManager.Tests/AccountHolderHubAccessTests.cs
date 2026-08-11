using System.Reflection;
using System.Security.Claims;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

/// <summary>
/// The hubs decide group membership and admission themselves, outside the route authorization the
/// endpoint contract test covers. Getting a user into the wrong group is silent: the page renders and
/// then never updates, because the group is the broadcast fan-out.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountHolderHubAccessTests
{
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
            var context = await ConnectToSteamDaemonHubAsync(host, scope, cookie);
            Assert.True(admitted != context.Aborted, $"The Steam daemon hub got {caller} wrong.");
            Assert.Equal(admitted, context.Items.ContainsKey("SessionId"));
        }
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

    private static async Task<RecordingHubContext> ConnectToSteamDaemonHubAsync(
        EndpointAuthorizationHost host, IServiceScope scope, HttpContext request)
    {
        var context = new RecordingHubContext(request);
        var hub = new SteamDaemonHub(
            host.Application.Services.GetRequiredService<SteamDaemonService>(),
            scope.ServiceProvider.GetRequiredService<SessionService>(),
            host.Application.Services.GetRequiredService<ILogger<SteamDaemonHub>>())
        {
            Context = context,
            Groups = new RecordingGroups()
        };

        await hub.OnConnectedAsync();
        return context;
    }

    /// <summary>
    /// A request carrying the cookie of a session stored with <paramref name="sessionType"/>. The session
    /// is minted by the real login path and its stored type is then set, because nothing creates a user
    /// session yet and the hubs read the stored row rather than any claim.
    /// </summary>
    private static async Task<HttpContext> SessionCookieAsync(
        EndpointAuthorizationHost host,
        IServiceScope scope,
        SessionType sessionType,
        Action<UserSession>? alsoSet = null)
    {
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();

        var created = await sessionService.CreateAdminSessionAsync(apiKey, new DefaultHttpContext());
        Assert.NotNull(created);

        var factory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var database = await factory.CreateDbContextAsync())
        {
            var stored = await database.UserSessions.FirstAsync(s => s.Id == created!.Value.Session.Id);
            stored.SessionType = sessionType;
            alsoSet?.Invoke(stored);
            await database.SaveChangesAsync();
        }

        var request = new DefaultHttpContext();
        sessionService.SetSessionCookie(request, created!.Value.RawToken, DateTime.UtcNow.AddDays(1));
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
}
