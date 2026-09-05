using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// What Security:EnableAuthentication=false means: the installation has no access control. Every route
/// answers a caller with no cookie and no claims, and those callers share one session rather than being
/// handed one each - a cookie-less client would otherwise add a row to UserSessions per request.
/// </summary>
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class DisabledAuthenticationAccessTests
{
    /// <summary>
    /// The shared session is what makes the frontend work with the flag off: it is told it is an admin
    /// and starts writing preferences straight away, and a preferences row needs a session that exists.
    /// A test that only asserted a session came back would pass against a version that minted one per
    /// request, which is the shape that fills the table.
    /// </summary>
    [Fact]
    public async Task CookielessRequestsShareOneSession()
    {
        ResetSharedAuthDisabledSession();
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        await host.AssertIsolationAsync(client);

        var first = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        var second = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");

        Assert.Equal(
            first.GetProperty("sessionId").GetGuid(),
            second.GetProperty("sessionId").GetGuid());
        Assert.NotEqual(Guid.Empty, first.GetProperty("sessionId").GetGuid());
    }

    [Fact]
    public async Task AGuestCookieUsesTheSharedManagementSession()
    {
        ResetSharedAuthDisabledSession();
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        await host.AssertIsolationAsync(client);

        using var scope = host.Application.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var guest = await sessions.CreateGuestSessionAsync(new DefaultHttpContext());
        Assert.NotNull(guest);
        var cookie = new DefaultHttpContext();
        sessions.SetSessionCookie(cookie, guest.Value.RawToken, guest.Value.Session.ExpiresAtUtc);
        client.DefaultRequestHeaders.Add("Cookie", cookie.Response.Headers.SetCookie.ToString().Split(';')[0]);

        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");

        Assert.NotEqual(guest.Value.Session.Id, status.GetProperty("sessionId").GetGuid());
        Assert.Equal("admin", status.GetProperty("sessionType").GetString());
        using var response = await client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// A route behind a named policy, reached with no cookie at all. Named policies are not covered by
    /// the default or fallback policy, so opening only those two would leave this one 403.
    /// </summary>
    [Fact]
    public async Task PolicyGatedRouteAnswersACallerWithNoCookie()
    {
        ResetSharedAuthDisabledSession();
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { HandleCookies = false });
        await host.AssertIsolationAsync(client);

        using var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// The check that catches a policy left out of the list the flag opens. One route returning 200
    /// proves the policy that route carries; this walks every route authorization actually evaluates, so
    /// adding a policy and forgetting to open it fails here rather than in whichever screen used it.
    /// </summary>
    [Fact]
    public async Task EveryGatedRouteAdmitsACallerWithNoClaims()
    {
        ResetSharedAuthDisabledSession();
        using var host = new EndpointAuthorizationHost(authenticationEnabled: false);
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        var policyProvider = host.Application.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var refused = new List<string>();

        using var callerScope = host.Application.Services.CreateScope();
        var request = new DefaultHttpContext { RequestServices = callerScope.ServiceProvider };
        var authenticated = await request.AuthenticateAsync(SessionAuthenticationHandler.SchemeName);
        var anonymous = Assert.IsAssignableFrom<ClaimsPrincipal>(authenticated.Principal);

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

            using var scope = host.Application.Services.CreateScope();
            var authorization = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            var result = await authorization.AuthorizeAsync(anonymous, resource: null, policy.Requirements);
            if (!result.Succeeded)
            {
                refused.Add(endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed>");
            }
        }

        Assert.True(
            refused.Count == 0,
            $"{refused.Count} route(s) refuse an anonymous caller while authentication is disabled:"
            + Environment.NewLine + string.Join(Environment.NewLine, refused));
    }

    /// <summary>
    /// The shared session is cached in a static, because SessionService is scoped and the cache has to
    /// survive a request. Each test therefore starts from the state a freshly started process has rather
    /// than inheriting the session another test's host created.
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
