using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// The download hub carries every live update the app shows. It authenticates from the session cookie
/// alone, and a connection it refuses fails silently: the page renders and then simply never updates, so
/// nothing else in the suite notices. These run the real negotiate, transport and handshake against the
/// application in process, then assert a server broadcast actually lands on each connection.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class DownloadHubCookieConnectionTests
{
    [Fact]
    public async Task UnauthenticatedModeConnectsWithoutAnIncomingSessionCookie()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        using var connection = new HubOverLongPolling(host.Application.Server.CreateClient(), string.Empty);

        Assert.Equal(HttpStatusCode.OK, await connection.NegotiateAsync());
        await connection.StartAsync();
        Assert.True(await connection.ReceivedAsync(SignalREvents.ActivityUpdated));
    }

    [Fact]
    public async Task EnablingPasswordAccessClosesEverySharedSocketButKeepsAccountSockets()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        var (ownerName, ownerPassword) = await host.NewAccountAsync();
        var factory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var database = await factory.CreateDbContextAsync())
        {
            var owner = await database.UserAccounts.SingleAsync(account => account.Username == ownerName);
            var existingOwner = await database.UserAccounts.SingleOrDefaultAsync(account => account.IsMainAdmin);
            if (existingOwner is null)
            {
                owner.IsMainAdmin = true;
            }
            else
            {
                existingOwner.PasswordHash = owner.PasswordHash;
                ownerName = existingOwner.Username;
            }
            await database.SaveChangesAsync();
        }
        using var ownerClient = host.Application.CreateClient();
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(ownerClient);
        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
        using (var login = await ownerClient.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { ApiKey = apiKey, Username = ownerName, Password = ownerPassword }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(ownerClient);
        using var scope = host.Application.Services.CreateScope();
        var sharedCookie = await SharedCookieAsync(host);
        var accountCookie = (await SessionAsync(host, scope, SessionType.Admin)).Cookie;
        var sharedConnections = new[]
        {
            new HubOverLongPolling(host.Application.Server.CreateClient(), sharedCookie),
            new HubOverLongPolling(host.Application.Server.CreateClient(), sharedCookie)
        };
        using var daemonConnection = new HubOverLongPolling(
            host.Application.Server.CreateClient(),
            sharedCookie,
            "/hubs/steam-daemon");
        using var accountConnection = new HubOverLongPolling(
            host.Application.Server.CreateClient(),
            accountCookie);

        try
        {
            foreach (var connection in sharedConnections.Append(accountConnection))
            {
                Assert.Equal(HttpStatusCode.OK, await connection.NegotiateAsync());
                await connection.StartAsync();
                Assert.True(await connection.ReceivedAsync(SignalREvents.ActivityUpdated));
            }
            Assert.Equal(HttpStatusCode.OK, await daemonConnection.NegotiateAsync());
            await daemonConnection.StartAsync();

            using var changed = await ownerClient.PostAsJsonAsync(
                "/api/auth/setup",
                new AccessSetupRequest { Mode = AccountMode.Password, ApiKey = apiKey });
            Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

            var closed = await Task.WhenAll(
                sharedConnections.Append(daemonConnection).Select(connection => connection.ClosedAsync()));
            Assert.All(closed, Assert.True);

            var marker = $"secure-mode-{Guid.NewGuid():N}";
            await host.Application.Services.GetRequiredService<IHubContext<DownloadHub>>()
                .Clients.Group(DownloadHub.AdminGroup)
                .SendAsync(SignalREvents.ActivityUpdated, marker);
            Assert.True(await accountConnection.ReceivedAsync(marker));
        }
        finally
        {
            foreach (var connection in sharedConnections)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// An admin, a user and a guest each connect with nothing but the cookie and each receives a
    /// broadcast sent to the group they were put in, and the guest never receives an admin-only one.
    /// </summary>
    [Fact]
    public async Task AnAdminAUserAndAGuestEachReceiveABroadcastOverACookieOnlyConnection()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);
        Assert.IsType<DownloadHubLifetimeManager>(
            host.Application.Services.GetRequiredService<HubLifetimeManager<DownloadHub>>());

        using var scope = host.Application.Services.CreateScope();
        var connections = new List<(SessionType Role, HubOverLongPolling Connection)>();

        try
        {
            foreach (var role in new[] { SessionType.Admin, SessionType.User, SessionType.Guest })
            {
                var connection = new HubOverLongPolling(
                    host.Application.Server.CreateClient(),
                    (await SessionAsync(host, scope, role)).Cookie);
                connections.Add((role, connection));

                Assert.Equal(HttpStatusCode.OK, await connection.NegotiateAsync());
                await connection.StartAsync();

                // OnConnectedAsync joins the groups first and seeds the caller with the activity
                // snapshot last, so the seed arriving is what proves the connection is in
                // AuthenticatedUsersGroup before the broadcast below is sent.
                Assert.True(
                    await connection.ReceivedAsync(SignalREvents.ActivityUpdated),
                    $"A {role} connection never received the snapshot the hub seeds a caller with.");
            }

            var broadcast = $"download-hub-broadcast-{Guid.NewGuid():N}";
            await host.Application.Services.GetRequiredService<IHubContext<DownloadHub>>()
                .Clients.Group(DownloadHub.AuthenticatedUsersGroup)
                .SendAsync(SignalREvents.ActivityUpdated, broadcast);

            foreach (var (role, connection) in connections)
            {
                Assert.True(
                    await connection.ReceivedAsync(broadcast),
                    $"A {role} connection stopped receiving live updates.");
            }

            var hubContext = host.Application.Services.GetRequiredService<IHubContext<DownloadHub>>();
            var adminOnly = $"download-hub-admin-{Guid.NewGuid():N}";
            var authenticated = $"download-hub-authenticated-{Guid.NewGuid():N}";
            await hubContext.Clients.Group(DownloadHub.AdminGroup).SendAsync(SignalREvents.ActivityUpdated, adminOnly);
            await hubContext.Clients.Group(DownloadHub.AuthenticatedUsersGroup).SendAsync(SignalREvents.ActivityUpdated, authenticated);

            var admin = connections.Single(entry => entry.Role == SessionType.Admin).Connection;
            Assert.True(await admin.ReceivedAsync(adminOnly));
            Assert.True(await admin.ReceivedAsync(authenticated));

            // A connection receives its updates in send order, so a guest holding the second marker
            // would already hold the first if it had been sent to it.
            var guest = connections.Single(entry => entry.Role == SessionType.Guest).Connection;
            Assert.True(await guest.ReceivedAsync(authenticated));
            Assert.False(guest.HasReceived(adminOnly), "A guest connection received an admin-only update.");
        }
        finally
        {
            foreach (var (_, connection) in connections)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// A guest sees no management page, so it receives no run progress or schedule state, and it still
    /// receives every completion its dashboard and downloads list refetch on. Admins and users receive both.
    /// </summary>
    [Fact]
    public async Task AGuestReceivesOnlyTheRunEventsItsDashboardRefetchesOn()
    {
        // Read before the host starts: the host moves the working directory to a temporary root.
        var typesSource = File.ReadAllText(Path.Combine(
            EndpointAuthorizationHost.FindRepositoryRoot(), "Web", "src", "contexts", "SignalRContext", "types.ts"));
        var refreshList = Regex.Match(
            typesSource,
            @"SIGNALR_REFRESH_EVENTS = \[(?<names>.*?)\]",
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));
        Assert.True(refreshList.Success, "SIGNALR_REFRESH_EVENTS was not found in types.ts.");
        var refreshEvents = Regex.Matches(refreshList.Groups["names"].Value, "'(?<name>\\w+)'", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Select(match => match.Groups["name"].Value)
            .ToList();
        Assert.NotEmpty(refreshEvents);

        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();
        var connections = new List<(SessionType Role, HubOverLongPolling Connection)>();

        try
        {
            foreach (var role in new[] { SessionType.Admin, SessionType.User, SessionType.Guest })
            {
                var connection = new HubOverLongPolling(
                    host.Application.Server.CreateClient(),
                    (await SessionAsync(host, scope, role)).Cookie);
                connections.Add((role, connection));

                Assert.Equal(HttpStatusCode.OK, await connection.NegotiateAsync());
                await connection.StartAsync();
                Assert.True(await connection.ReceivedAsync(SignalREvents.ActivityUpdated));
            }

            var admin = connections.Single(entry => entry.Role == SessionType.Admin).Connection;
            var guest = connections.Single(entry => entry.Role == SessionType.Guest).Connection;
            var notifications = host.Application.Services.GetRequiredService<ISignalRNotificationService>();

            var managementOnly = new List<string>();
            foreach (var eventName in new[]
            {
                SignalREvents.GameRemovalProgress, SignalREvents.SchedulesUpdated,
                SignalREvents.ScheduledPrefillStarted, SignalREvents.PrefillHistoryUpdated
            })
            {
                managementOnly.Add($"{eventName}-{Guid.NewGuid():N}");
                await notifications.NotifyAllAsync(eventName, managementOnly[^1]);
            }

            managementOnly.Add($"{SignalREvents.StatusCheckProgress}-{Guid.NewGuid():N}");
            notifications.NotifyAllFireAndForget(SignalREvents.StatusCheckProgress, managementOnly[^1]);

            // The fire-and-forget send runs on another thread; once an admin holds it, every send above
            // has been written to every connection it was sent to.
            Assert.True(await admin.ReceivedAsync(managementOnly[^1]));

            var everyone = new List<string>();
            foreach (var eventName in refreshEvents.Concat(new[]
            {
                SignalREvents.DatabaseResetProgress, SignalREvents.EventCreated, SignalREvents.GuestDurationUpdated
            }))
            {
                everyone.Add($"{eventName}-{Guid.NewGuid():N}");
                await notifications.NotifyAllAsync(eventName, everyone[^1]);
            }

            foreach (var (role, connection) in connections)
            {
                foreach (var marker in everyone)
                {
                    Assert.True(await connection.ReceivedAsync(marker), $"A {role} connection missed {marker}.");
                }
            }

            foreach (var (role, connection) in connections.Where(entry => entry.Role != SessionType.Guest))
            {
                foreach (var marker in managementOnly)
                {
                    Assert.True(await connection.ReceivedAsync(marker), $"A {role} connection missed {marker}.");
                }
            }

            // A connection receives its updates in send order, so a guest holding the later markers
            // would already hold these if they had been sent to it.
            foreach (var marker in managementOnly)
            {
                Assert.False(guest.HasReceived(marker), $"A guest connection received {marker}.");
            }
        }
        finally
        {
            foreach (var (_, connection) in connections)
            {
                connection.Dispose();
            }
        }
    }

    /// <summary>
    /// An admin browser that stops polling while updates keep coming is closed once it falls more than
    /// the queue limit behind, with a Close that lets it reconnect, and the same cookie connects again
    /// and receives live updates. An admin that keeps polling receives every update throughout.
    /// </summary>
    [Fact]
    public async Task AnAdminThatFallsTooFarBehindIsClosedWithReconnectAllowed()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();
        var laggingCookie = (await SessionAsync(host, scope, SessionType.Admin)).Cookie;
        using var lagging = new HubOverLongPolling(host.Application.Server.CreateClient(), laggingCookie);
        using var polling = new HubOverLongPolling(
            host.Application.Server.CreateClient(),
            (await SessionAsync(host, scope, SessionType.Admin)).Cookie);
        foreach (var connection in new[] { lagging, polling })
        {
            Assert.Equal(HttpStatusCode.OK, await connection.NegotiateAsync());
            await connection.StartAsync();
            Assert.True(await connection.ReceivedAsync(SignalREvents.ActivityUpdated));
        }

        // Batches of 32 messages of about 1 KB fill half of a connection's 64 KB transport buffer, so
        // the connection polled after each batch never queues a write while the unpolled one does.
        const int batchSize = 32;
        var hubContext = host.Application.Services.GetRequiredService<IHubContext<DownloadHub>>();
        for (var batchStart = 0; batchStart < 2 * DownloadHubLifetimeManager.MaxQueuedWrites; batchStart += batchSize)
        {
            var marker = string.Empty;
            for (var index = batchStart; index < batchStart + batchSize; index++)
            {
                marker = $"queued-update-{index}-{Guid.NewGuid():N}";
                await hubContext.Clients.Group(DownloadHub.AdminGroup)
                    .SendAsync(SignalREvents.ActivityUpdated, marker.PadRight(1000, '.'));
            }

            Assert.True(await polling.ReceivedAsync(marker), $"The polling admin missed {marker}.");
        }

        Assert.True(await lagging.ReceivedAsync("\"type\":7"), "The lagging admin never received a Close.");
        Assert.True(lagging.HasReceived("{\"type\":7,\"allowReconnect\":true}"), "The Close forbade reconnecting.");
        Assert.True(await lagging.ClosedAsync());

        using var reconnected = new HubOverLongPolling(host.Application.Server.CreateClient(), laggingCookie);
        Assert.Equal(HttpStatusCode.OK, await reconnected.NegotiateAsync());
        await reconnected.StartAsync();
        Assert.True(await reconnected.ReceivedAsync(SignalREvents.ActivityUpdated));

        var afterReconnect = $"after-reconnect-{Guid.NewGuid():N}";
        await hubContext.Clients.Group(DownloadHub.AdminGroup).SendAsync(SignalREvents.ActivityUpdated, afterReconnect);
        Assert.True(await reconnected.ReceivedAsync(afterReconnect));
        Assert.True(await polling.ReceivedAsync(afterReconnect));
    }

    /// <summary>
    /// The same token that works in the cookie is refused in the query string, so it no longer has to
    /// travel through proxy logs, access logs and browser history to reach the hub.
    /// </summary>
    [Fact]
    public async Task TheHubTakesTheTokenFromTheCookieAndRefusesItInTheQuery()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var scope = host.Application.Services.CreateScope();
        var session = await SessionAsync(host, scope, SessionType.Admin);

        using var client = host.Application.Server.CreateClient();

        using var withCookie = new HttpRequestMessage(HttpMethod.Post, "/hubs/downloads/negotiate?negotiateVersion=1");
        withCookie.Headers.Add("Cookie", session.Cookie);
        using var accepted = await client.SendAsync(withCookie);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using var refused = await client.PostAsync(
            $"/hubs/downloads/negotiate?negotiateVersion=1&access_token={Uri.EscapeDataString(session.RawToken)}",
            content: null);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// A session stored with <paramref name="role"/>, plus the cookie header a browser holding it sends.
    /// The session is minted by the real login path and its stored type is then set, because the hub reads
    /// the stored row rather than any claim.
    /// </summary>
    private static async Task<(string Cookie, string RawToken)> SessionAsync(
        EndpointAuthorizationHost host, IServiceScope scope, SessionType role)
    {
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var factory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        var account = new UserAccount
        {
            Id = Guid.NewGuid(),
            Username = $"hub-{Guid.NewGuid():N}",
            PasswordHash = "unused",
            Role = role,
            CreatedAtUtc = DateTime.UtcNow
        };
        await using (var database = await factory.CreateDbContextAsync())
        {
            database.UserAccounts.Add(account);
            await database.SaveChangesAsync();
        }

        var created = await sessionService.CreateAccountSessionAsync(new DefaultHttpContext(), account);

        var written = new DefaultHttpContext();
        sessionService.SetSessionCookie(written, created.RawToken, DateTime.UtcNow.AddDays(1));
        return (written.Response.Headers.SetCookie.ToString().Split(';')[0], created.RawToken);
    }

    private static async Task<string> SharedCookieAsync(EndpointAuthorizationHost host)
    {
        using var client = host.Application.CreateClient();
        using var response = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return response.Headers.GetValues("Set-Cookie")
            .Select(value => value.Split(';')[0])
            .Single(value => value.StartsWith("LancacheManager.Session=", StringComparison.Ordinal));
    }

    /// <summary>
    /// A download-hub connection driven over the long-polling transport, which is the one transport a test
    /// server speaks without a socket. Everything below the hub is the application's own: routing,
    /// authentication, authorization, the connection dispatcher and the hub protocol handshake.
    /// </summary>
    private sealed class HubOverLongPolling : IDisposable
    {
        private const string Handshake = "{\"protocol\":\"json\",\"version\":1}";
        private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(30);

        private readonly HttpClient _client;
        private readonly string _hubPath;
        private string _cookie;
        private readonly StringBuilder _received = new();
        private string _connectionToken = string.Empty;

        public HubOverLongPolling(HttpClient client, string cookie, string hubPath = "/hubs/downloads")
        {
            _client = client;
            _cookie = cookie;
            _hubPath = hubPath;
        }

        /// <summary>Runs the negotiate the browser runs first, and keeps the token the rest of it needs.</summary>
        public async Task<HttpStatusCode> NegotiateAsync()
        {
            using var request = Request(HttpMethod.Post, $"{_hubPath}/negotiate?negotiateVersion=1");
            using var response = await _client.SendAsync(request);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                return response.StatusCode;
            }

            if (string.IsNullOrEmpty(_cookie)
                && response.Headers.TryGetValues("Set-Cookie", out var cookies))
            {
                _cookie = cookies
                    .Select(value => value.Split(';')[0])
                    .FirstOrDefault(value => value.StartsWith("LancacheManager.Session=", StringComparison.Ordinal))
                    ?? string.Empty;
            }

            using var negotiated = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _connectionToken = negotiated.RootElement.GetProperty("connectionToken").GetString() ?? string.Empty;
            Assert.NotEqual(string.Empty, _connectionToken);
            return response.StatusCode;
        }

        /// <summary>
        /// Opens the transport and sends the protocol handshake. The first poll is what establishes the
        /// connection and returns without data, exactly as the SignalR client's own transport does it.
        /// </summary>
        public async Task StartAsync()
        {
            using var opening = Request(HttpMethod.Get, TransportUrl);
            using var opened = await _client.SendAsync(opening);
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);

            using var handshake = Request(HttpMethod.Post, TransportUrl);
            handshake.Content = new StringContent(Handshake, Encoding.UTF8);
            using var handshaken = await _client.SendAsync(handshake);
            Assert.Equal(HttpStatusCode.OK, handshaken.StatusCode);
        }

        /// <summary>
        /// Polls until <paramref name="marker"/> shows up in what the server has sent, or until the
        /// receive timeout. False means the connection never delivered it.
        /// </summary>
        public async Task<bool> ReceivedAsync(string marker)
        {
            using var deadline = new CancellationTokenSource(ReceiveTimeout);

            while (!HasReceived(marker))
            {
                try
                {
                    using var request = Request(HttpMethod.Get, TransportUrl);
                    using var response = await _client.SendAsync(request, deadline.Token);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return false;
                    }

                    _received.Append(await response.Content.ReadAsStringAsync(deadline.Token));
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>True when <paramref name="marker"/> is in what the polls so far have returned.</summary>
        public bool HasReceived(string marker) => _received.ToString().Contains(marker, StringComparison.Ordinal);

        public async Task<bool> ClosedAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!deadline.IsCancellationRequested)
            {
                try
                {
                    using var request = Request(HttpMethod.Get, TransportUrl);
                    using var response = await _client.SendAsync(request, deadline.Token);
                    if (response.StatusCode != HttpStatusCode.OK)
                    {
                        return true;
                    }

                    var body = await response.Content.ReadAsStringAsync(deadline.Token);
                    if (body.Contains("\"type\":7", StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch (HttpRequestException)
                {
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }

        public void Dispose() => _client.Dispose();

        private string TransportUrl => $"{_hubPath}?id={Uri.EscapeDataString(_connectionToken)}";

        private HttpRequestMessage Request(HttpMethod method, string url)
        {
            var request = new HttpRequestMessage(method, url);
            if (!string.IsNullOrEmpty(_cookie))
            {
                request.Headers.Add("Cookie", _cookie);
            }
            return request;
        }
    }
}
