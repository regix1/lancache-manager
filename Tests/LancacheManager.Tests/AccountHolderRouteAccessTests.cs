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
    /// <see cref="EndpointAuthorizationContractTests"/> asserts endpoint by endpoint. [12]
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
    /// admin reaches. [12]
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
    /// an admin-only preference key, and changing a refresh rate the caller does not own. [12]
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
    /// and the five expiry values, which are what every screen reads. [12b]
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
    /// three session types rather than two. Each caller reads its own row from its own request. [12b] [16]
    /// </summary>
    [Fact]
    public async Task TheSessionsListReportsAUserRowLikeAnAdminRowAndCountsThreeTypes()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var adminClient = await host.CreateAdminClientAsync();
        using var userClient = await CreateUserClientAsync(host);
        var userSessionId = await SessionIdAsync(userClient);

        var adminBody = await adminClient.GetFromJsonAsync<JsonElement>("/api/sessions?pageSize=100");
        var body = await userClient.GetFromJsonAsync<JsonElement>("/api/sessions?pageSize=100");
        var userRow = body.GetProperty("sessions").EnumerateArray()
            .Single(row => row.GetProperty("id").GetGuid() == userSessionId);
        var adminRow = adminBody.GetProperty("sessions").EnumerateArray()
            .First(row => row.GetProperty("sessionType").GetString() == "admin");

        AssertSamePrefillAccess(adminRow, userRow, "/api/sessions");

        // Two counts and a total that ignores the third type is how the list said a user was nobody.
        Assert.True(body.GetProperty("userCount").GetInt32() >= 1, "the list counts no user sessions");
        Assert.Equal(
            body.GetProperty("count").GetInt32(),
            body.GetProperty("adminCount").GetInt32()
            + body.GetProperty("userCount").GetInt32()
            + body.GetProperty("guestCount").GetInt32());
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
    /// Two sessions that differ only in their stored session type, resolved into principals by the real
    /// authentication handler so the claims under test are the ones a live request would carry.
    /// </summary>
    private static async Task<(ClaimsPrincipal Admin, ClaimsPrincipal User)> AdminAndUserPrincipalsAsync(
        EndpointAuthorizationHost host)
    {
        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();

        using var scope = host.Application.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();

        var admin = await sessions.CreateAdminSessionAsync(apiKey, new DefaultHttpContext());
        var user = await sessions.CreateAdminSessionAsync(apiKey, new DefaultHttpContext());
        Assert.NotNull(admin);
        Assert.NotNull(user);
        await PromoteToUserAsync(host, user!.Value.Session.Id);

        return (await PrincipalForAsync(host, admin!.Value.RawToken), await PrincipalForAsync(host, user.Value.RawToken));
    }

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
        var client = await host.CreateAdminClientAsync();

        try
        {
            await PromoteToUserAsync(host, await SessionIdAsync(client));
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

    private static async Task PromoteToUserAsync(EndpointAuthorizationHost host, Guid sessionId)
    {
        var factory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var context = await factory.CreateDbContextAsync();
        var session = await context.UserSessions.FirstAsync(s => s.Id == sessionId);
        session.SessionType = SessionType.User;
        await context.SaveChangesAsync();
    }
}
