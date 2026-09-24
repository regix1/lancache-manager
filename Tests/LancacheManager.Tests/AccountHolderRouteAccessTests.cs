using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// A user session and an admin session must be answered identically. The routes are compared by the
/// authorization decision the running application actually makes, against the principals the real
/// authentication handler actually mints, because the alternative - firing every verb at every route -
/// would run the cache-clear and database-reset handlers for real.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class AccountHolderRouteAccessTests
{
    private static readonly string[] PrefillEnabledFields =
    [
        "steamPrefillEnabled",
        "epicPrefillEnabled",
        "battlenetPrefillEnabled",
        "riotPrefillEnabled",
        "xboxPrefillEnabled"
    ];

    private static readonly string[] PrefillExpiresFields =
    [
        "steamPrefillExpiresAt",
        "epicPrefillExpiresAt",
        "battlenetPrefillExpiresAt",
        "riotPrefillExpiresAt",
        "xboxPrefillExpiresAt"
    ];

    /// <summary>
    /// Every route the application authorizes reaches the same verdict for a user as for an admin. No
    /// route is skipped, so narrowing a policy back to admins alone fails here rather than passing by
    /// being left out of the comparison. A guest is refused on the same routes as before, which
    /// <see cref="EndpointAuthorizationContractTests"/> asserts endpoint by endpoint.
    /// </summary>
    [Fact]
    public async Task EveryRouteAnswersAUserExactlyAsItAnswersAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var (adminPrincipal, userPrincipal) = await AdminAndUserPrincipalsAsync(host);

        var differences = new List<string>();

        await foreach (var (route, policy) in GatedRoutesAsync(host))
        {
            var admin = await AllowsAsync(host, adminPrincipal, policy);
            var user = await AllowsAsync(host, userPrincipal, policy);
            if (admin != user)
            {
                differences.Add($"{route}: admin={admin}, user={user}");
            }
        }

        Assert.True(
            differences.Count == 0,
            $"{differences.Count} route(s) answer a user differently from an admin:{Environment.NewLine}"
            + string.Join(Environment.NewLine, differences));
    }

    /// <summary>
    /// The prefill routes are the ones the claim minting decides. A user carries the five
    /// <c>*PrefillActive</c> claims for the same reason an admin does, so it reaches the daemon routes an
    /// admin reaches.
    /// </summary>
    [Fact]
    public async Task AUserCarriesTheSamePrefillClaimsAsAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        var (adminPrincipal, userPrincipal) = await AdminAndUserPrincipalsAsync(host);

        foreach (var claimType in new[]
                 {
                     "SteamPrefillActive",
                     "EpicPrefillActive",
                     "BattleNetPrefillActive",
                     "RiotPrefillActive",
                     "XboxPrefillActive"
                 })
        {
            Assert.True(adminPrincipal.HasClaim(claimType, "true"), $"An admin is missing {claimType}.");
            Assert.True(userPrincipal.HasClaim(claimType, "true"), $"A user is missing {claimType}.");
        }
    }

    /// <summary>
    /// The two hand-rolled session-type checks that answer with a status code rather than a body: writing
    /// an admin-only preference key, and changing a refresh rate the caller does not own.
    /// </summary>
    [Fact]
    public async Task TheHandRolledSessionTypeChecksAnswerAUserExactlyAsTheyAnswerAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        using var userClient = await CreateUserClientAsync(host);

        var somebodyElse = Guid.NewGuid();

        foreach (var (name, request) in new (string, Func<HttpClient, Task<HttpResponseMessage>>)[]
                 {
                     ("PATCH /api/user-preferences/refreshRateLocked",
                         client => client.PatchAsJsonAsync("/api/user-preferences/refreshRateLocked", true)),
                     ("PATCH /api/sessions/{id}/refresh-rate for a session the caller does not own",
                         client => client.PatchAsJsonAsync(
                             $"/api/sessions/{somebodyElse}/refresh-rate",
                             new RefreshRateRequest { RefreshRate = "30s" }))
                 })
        {
            using var adminResponse = await request(adminClient);
            using var userResponse = await request(userClient);
            Assert.NotEqual(HttpStatusCode.Forbidden, userResponse.StatusCode);
            Assert.True(
                adminResponse.StatusCode == userResponse.StatusCode,
                $"{name} answered an admin {(int)adminResponse.StatusCode} and a user {(int)userResponse.StatusCode}.");
        }
    }

    /// <summary>
    /// The status body reports the same prefill access to a user as to an admin: the five enabled flags
    /// and the five expiry values, which are what every screen reads.
    /// </summary>
    [Fact]
    public async Task TheAuthStatusBodyReportsTheSamePrefillAccessToAUserAsToAnAdmin()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        using var userClient = await CreateUserClientAsync(host);

        var admin = await adminClient.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var user = await userClient.GetFromJsonAsync<JsonElement>("/api/auth/status");

        Assert.Equal("admin", admin.GetProperty("sessionType").GetString());
        Assert.Equal("user", user.GetProperty("sessionType").GetString());
        AssertSamePrefillAccess(admin, user, "/api/auth/status");
    }

    /// <summary>
    /// The sessions list reports the same prefill access on a user's row as on an admin's, and counts the
    /// three session types rather than two. Each caller reads its own row from its own request.
    /// </summary>
    [Fact]
    public async Task TheSessionsListReportsAUserRowLikeAnAdminRowAndCountsThreeTypes()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        using var userClient = await CreateUserClientAsync(host);
        var adminStatus = await adminClient.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var userStatus = await userClient.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var adminAccountId = adminStatus.GetProperty("accountId").GetGuid();
        var userAccountId = userStatus.GetProperty("accountId").GetGuid();
        var adminSessionId = adminStatus.GetProperty("sessionId").GetGuid();
        var userSessionId = await SessionIdAsync(userClient);

        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await contexts.CreateDbContextAsync();
        var usernames = await context.UserAccounts
            .Where(account => account.Id == adminAccountId || account.Id == userAccountId)
            .ToDictionaryAsync(account => account.Id, account => account.Username);

        var adminBody = await adminClient.GetFromJsonAsync<JsonElement>("/api/sessions?pageSize=100");
        var body = await userClient.GetFromJsonAsync<JsonElement>("/api/sessions?pageSize=100");
        var userRow = body.GetProperty("sessions").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == userSessionId);
        var adminRow = adminBody.GetProperty("sessions").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == adminSessionId);

        AssertSamePrefillAccess(adminRow, userRow, "/api/sessions");
        Assert.Equal(usernames[adminAccountId], adminRow.GetProperty("username").GetString());
        Assert.Equal(usernames[userAccountId], userRow.GetProperty("username").GetString());
        Assert.NotEqual(adminRow.GetProperty("username").GetString(), userRow.GetProperty("username").GetString());
        Assert.False(adminRow.TryGetProperty("accountId", out _));
        Assert.False(userRow.TryGetProperty("accountId", out _));
        Assert.False(adminRow.GetProperty("accountDeleted").GetBoolean());
        Assert.False(userRow.GetProperty("accountDeleted").GetBoolean());
        Assert.Equal(adminRow.GetProperty("ipAddress").GetString(), userRow.GetProperty("ipAddress").GetString());
        Assert.Equal(adminRow.GetProperty("userAgent").GetString(), userRow.GetProperty("userAgent").GetString());

        // Two counts and a total that ignores the third type is how the list said a user was nobody.
        Assert.True(body.GetProperty("userCount").GetInt32() >= 1, "the list counts no user sessions");
        Assert.Equal(
            body.GetProperty("count").GetInt32(),
            body.GetProperty("adminCount").GetInt32()
            + body.GetProperty("userCount").GetInt32()
            + body.GetProperty("guestCount").GetInt32());
    }

    [Fact]
    public async Task AuthenticationDisabledKeepsSharedAccountAndSessionRoutesAccountless()
    {
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var isolationClient = host.Application.CreateClient();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(isolationClient);

        using var statusResponse = await client.GetAsync("/api/auth/status");
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = status.GetProperty("sessionId").GetGuid();
        Assert.Equal("admin", status.GetProperty("sessionType").GetString());
        Assert.False(status.TryGetProperty("accountId", out _));

        client.DefaultRequestHeaders.Add(
            AntiforgeryToken.HeaderName,
            EndpointAuthorizationHost.AntiforgeryTokenFrom(statusResponse));

        using var accountsResponse = await client.GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.OK, accountsResponse.StatusCode);

        var sessions = await client.GetFromJsonAsync<JsonElement>("/api/sessions?pageSize=100");
        var shared = sessions.GetProperty("sessions").EnumerateArray()
            .Single(session => session.GetProperty("id").GetGuid() == sessionId);
        Assert.False(shared.TryGetProperty("accountId", out _));
        Assert.False(shared.TryGetProperty("username", out _));
        Assert.False(shared.GetProperty("accountDeleted").GetBoolean());

        using var createdResponse = await client.PostAsJsonAsync(
            "/api/accounts",
            new CreateAccountRequest
            {
                Username = $"shared-{Guid.NewGuid():N}",
                Password = "Endpoint-Contract-9"
            });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await createdResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("user", created.GetProperty("role").GetString());
        Assert.False(created.GetProperty("isMainAdmin").GetBoolean());

        using var roleResponse = await client.PutAsJsonAsync(
            $"/api/accounts/{created.GetProperty("id").GetGuid()}/role",
            new { role = "admin" });
        Assert.Equal(HttpStatusCode.NotFound, roleResponse.StatusCode);
    }

    /// <summary>
    /// The client-list, cache-health and schedule reads are refused to a guest and answered to a user,
    /// and the LAN event reads stay open to a guest. One list drives both callers, so closing a read to a
    /// guest without leaving it reachable by a user fails here rather than in the browser.
    /// </summary>
    [Fact]
    public async Task TheClosedReadsRefuseAGuestAndAnswerAUserWhileTheEventReadsStayOpen()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var userClient = await CreateUserClientAsync(host);
        using var guestClient = await CreateGuestClientAsync(host);

        // The routes that carry an id are driven against rows that exist, so a 404 cannot be read as the
        // 200 a user is owed. Both rows are created through the API by the user whose access is under test.
        using var groupResponse = await userClient.PostAsJsonAsync(
            "/api/client-groups",
            new CreateClientGroupRequest { Nickname = $"route-access-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode);
        var groupId = (await groupResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        using var eventResponse = await userClient.PostAsJsonAsync(
            "/api/events",
            new CreateEventRequest { Name = $"route-access-{Guid.NewGuid():N}", StartTime = now, EndTime = now + 3600 });
        Assert.Equal(HttpStatusCode.Created, eventResponse.StatusCode);
        var eventId = (await eventResponse.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetInt64();

        var scheduleKey = (await userClient.GetFromJsonAsync<JsonElement>("/api/system/schedules"))
            .EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString())
            .First(key => !string.IsNullOrEmpty(key));

        // Five client-list reads, four cache-health reads, four schedule reads and the run list.
        string[] closedToAGuest =
        [
            "/api/client-groups",
            $"/api/client-groups/{groupId}",
            "/api/client-groups/mapping",
            "/api/stats/clients",
            "/api/stats/exclusions",
            "/api/cache",
            "/api/cache/size/scan/status",
            "/api/stats/eviction",
            "/api/stats/eviction/scan/status",
            "/api/system/schedules",
            $"/api/system/schedules/{scheduleKey}",
            $"/api/system/schedules/{scheduleKey}/run-status",
            "/api/system/schedules/notification-display-mode",
            "/api/operations/runs"
        ];

        // The LAN event calendar is the read a guest keeps, alongside the client name map. That one
        // answers a guest as though the lookup were off until an admin allows guests to see names,
        // so it is open in the sense that it answers, not in the sense that it tells a guest anything.
        string[] openToAGuest =
        [
            "/api/clients/hostnames",
            "/api/events",
            "/api/events/active",
            "/api/events/calendar",
            $"/api/events/{eventId}",
            $"/api/events/{eventId}/downloads"
        ];

        Assert.Equal(14, closedToAGuest.Length);
        Assert.Equal(6, openToAGuest.Length);

        foreach (var route in closedToAGuest)
        {
            using var guestResponse = await guestClient.GetAsync(route);
            using var userResponse = await userClient.GetAsync(route);

            Assert.True(
                guestResponse.StatusCode == HttpStatusCode.Forbidden,
                $"GET {route} answered a guest {(int)guestResponse.StatusCode}, not 403.");
            Assert.True(
                userResponse.StatusCode == HttpStatusCode.OK,
                $"GET {route} answered a user {(int)userResponse.StatusCode}, not 200.");
        }

        foreach (var route in openToAGuest)
        {
            using var guestResponse = await guestClient.GetAsync(route);

            Assert.True(
                guestResponse.StatusCode == HttpStatusCode.OK,
                $"GET {route} answered a guest {(int)guestResponse.StatusCode}, not 200.");
        }
    }

    /// <summary>
    /// The guest-config reads that used to answer anyone who could reach the port now need a session, and
    /// a guest's session is enough for them.
    /// </summary>
    [Fact]
    public async Task TheGuestConfigReadsNeedASessionAndAnswerAGuestThatHasOne()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var anonymousClient = host.Application.CreateClient();
        using var guestClient = await CreateGuestClientAsync(host);

        string[] guestConfigReads =
        [
            "/api/auth/guest/status",
            "/api/auth/guest/config",
            "/api/auth/guest/prefill/config",
            "/api/auth/guest/epic-prefill/config",
            "/api/auth/guest/battlenet-prefill/config",
            "/api/auth/guest/riot-prefill/config",
            "/api/auth/guest/xbox-prefill/config"
        ];

        Assert.Equal(7, guestConfigReads.Length);

        foreach (var route in guestConfigReads)
        {
            using var anonymousResponse = await anonymousClient.GetAsync(route);
            using var guestResponse = await guestClient.GetAsync(route);

            Assert.True(
                anonymousResponse.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {route} answered a caller with no session {(int)anonymousResponse.StatusCode}, not 401.");
            Assert.True(
                guestResponse.StatusCode == HttpStatusCode.OK,
                $"GET {route} answered a guest {(int)guestResponse.StatusCode}, not 200.");
        }
    }

    private static void AssertSamePrefillAccess(JsonElement admin, JsonElement user, string source)
    {
        foreach (var field in PrefillEnabledFields)
        {
            Assert.True(admin.GetProperty(field).GetBoolean(), $"{source} reports {field} false for an admin.");
            Assert.True(user.GetProperty(field).GetBoolean(), $"{source} reports {field} false for a user.");
        }

        // A null expiry is omitted from the payload entirely (Program.cs:72), so presence is part of the
        // value: an account holder's access does not expire and carries no date either way.
        foreach (var field in PrefillExpiresFields)
        {
            var adminHasExpiry = admin.TryGetProperty(field, out var adminExpiry)
                && adminExpiry.ValueKind != JsonValueKind.Null;
            var userHasExpiry = user.TryGetProperty(field, out var userExpiry)
                && userExpiry.ValueKind != JsonValueKind.Null;

            Assert.False(adminHasExpiry, $"{source} dates {field} for an admin: {adminExpiry}");
            Assert.False(userHasExpiry, $"{source} dates {field} for a user: {userExpiry}");
        }
    }

    /// <summary>
    /// Every routed endpoint that authorization actually evaluates, paired with the policy it evaluates.
    /// Anonymous endpoints are skipped because the framework short-circuits them for every caller alike.
    /// </summary>
    private static async IAsyncEnumerable<(string Route, AuthorizationPolicy Policy)> GatedRoutesAsync(
        EndpointAuthorizationHost host)
    {
        var policyProvider = host.Application.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        foreach (var endpoint in host.Application.Services.GetRequiredService<EndpointDataSource>().Endpoints)
        {
            if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() != null)
            {
                continue;
            }

            var policy = await AuthorizationPolicy.CombineAsync(
                policyProvider,
                endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            policy ??= await policyProvider.GetFallbackPolicyAsync();
            if (policy == null)
            {
                continue;
            }

            yield return (endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed>", policy);
        }
    }

    private static async Task<bool> AllowsAsync(
        EndpointAuthorizationHost host, ClaimsPrincipal principal, AuthorizationPolicy policy)
    {
        using var scope = host.Application.Services.CreateScope();
        var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(principal, resource: null, policy.Requirements);
        return result.Succeeded;
    }

    /// <summary>
    /// Two sessions backed by distinct stored accounts, resolved into principals by the real
    /// authentication handler so the claims under test are the ones a live request would carry.
    /// </summary>
    private static async Task<(ClaimsPrincipal Admin, ClaimsPrincipal User)> AdminAndUserPrincipalsAsync(
        EndpointAuthorizationHost host)
    {
        using var scope = host.Application.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var contexts = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        UserAccount adminAccount;
        var userAccount = NewAccount();
        await using (var context = await contexts.CreateDbContextAsync())
        {
            adminAccount = await context.UserAccounts.SingleOrDefaultAsync(account => account.IsMainAdmin)
                ?? NewAccount(mainAdmin: true);
            if (context.Entry(adminAccount).State == EntityState.Detached)
            {
                context.UserAccounts.Add(adminAccount);
            }

            context.UserAccounts.Add(userAccount);
            await context.SaveChangesAsync();
        }

        Assert.True(adminAccount.IsMainAdmin);
        Assert.Equal(SessionType.Admin, adminAccount.Role);
        Assert.False(userAccount.IsMainAdmin);
        Assert.Equal(SessionType.User, userAccount.Role);

        var admin = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), adminAccount);
        var user = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), userAccount);

        return (await PrincipalForAsync(host, admin.RawToken), await PrincipalForAsync(host, user.RawToken));
    }

    private static UserAccount NewAccount(bool mainAdmin = false) => new()
    {
        Id = Guid.NewGuid(),
        Username = $"route-{Guid.NewGuid():N}",
        PasswordHash = "unused",
        Role = mainAdmin ? SessionType.Admin : SessionType.User,
        IsMainAdmin = mainAdmin,
        CreatedAtUtc = DateTime.UtcNow
    };

    private static async Task<ClaimsPrincipal> PrincipalForAsync(EndpointAuthorizationHost host, string rawToken)
    {
        using var scope = host.Application.Services.CreateScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        // Round-tripping the cookie through the writer keeps the cookie name out of this file.
        scope.ServiceProvider.GetRequiredService<SessionService>()
            .SetSessionCookie(context, rawToken, DateTime.UtcNow.AddDays(1));
        context.Request.Headers.Cookie = context.Response.Headers.SetCookie.ToString().Split(';')[0];

        var result = await context.AuthenticateAsync(SessionAuthenticationHandler.SchemeName);
        Assert.True(result.Succeeded, $"The session cookie did not authenticate: {result.Failure?.Message}");
        return result.Principal!;
    }

    private static async Task<HttpClient> CreateUserClientAsync(EndpointAuthorizationHost host)
    {
        var client = host.Application.CreateClient();

        try
        {
            using var scope = host.Application.Services.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            var account = NewAccount();
            await using (var context = await factory.CreateDbContextAsync())
            {
                context.UserAccounts.Add(account);
                await context.SaveChangesAsync();
            }

            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var session = await sessions.CreateAccountSessionAsync(new DefaultHttpContext(), account);
            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, session.RawToken, session.Session.ExpiresAtUtc);
            client.DefaultRequestHeaders.Add(
                "Cookie",
                cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0]);
            await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A client carrying a real guest session's cookie. The session is minted by the service the guest
    /// sign-in endpoint calls, and the cookie is round-tripped through the writer so the cookie name stays
    /// out of this file.
    /// </summary>
    private static async Task<HttpClient> CreateGuestClientAsync(EndpointAuthorizationHost host)
    {
        var client = host.Application.CreateClient();

        try
        {
            using var scope = host.Application.Services.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
            var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
            Assert.NotNull(guest);

            var cookieWriter = new DefaultHttpContext();
            sessions.SetSessionCookie(cookieWriter, guest!.Value.RawToken, DateTime.UtcNow.AddDays(1));
            client.DefaultRequestHeaders.Add(
                "Cookie",
                cookieWriter.Response.Headers.SetCookie.ToString().Split(';')[0]);

            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<Guid> SessionIdAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        return status.GetProperty("sessionId").GetGuid();
    }

}
