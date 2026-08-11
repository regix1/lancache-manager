using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

/// <summary>
/// What the X-Api-Key header still admits a caller to, and what it no longer does. The key used to
/// authenticate as an admin on every route, which made it a password that is printed to the log at
/// every start, cannot be revoked without rotating it for everybody, and leaves no session behind to
/// see or to end. It now stands in for a session on the API reference and its document only.
/// </summary>
// Boots the whole application and resolves the process-wide shared sessions, so it runs in the
// collection that already serializes every class doing that.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ApiKeyHeaderScopeTests
{
    private const string Password = "Correct-Horse-9";

    /// <summary>
    /// The reads a guest lost in the same release, plus the two the API documentation gives as curl
    /// examples and the two a monitoring script is most likely to poll. A caller holding the real key
    /// and nothing else is answered 401 on all of them, and the same client is answered 200 on the
    /// document, so the header did reach the server and the key in it is the right one.
    /// </summary>
    [Fact]
    public async Task OrdinaryRoutesRefuseACallerHoldingOnlyTheKey()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(
            "X-Api-Key",
            host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey());

        string[] ordinaryRoutes =
        [
            "/api/client-groups",
            "/api/client-groups/mapping",
            "/api/clients/hostnames",
            "/api/stats/clients",
            "/api/stats/exclusions",
            "/api/cache",
            "/api/cache/size/scan/status",
            "/api/stats/eviction",
            "/api/stats/eviction/scan/status",
            "/api/system/schedules",
            "/api/system/permissions",
            "/api/dashboard/batch",
            "/api/sessions",

            // The endpoint whose whole job was telling a key holder whether their key works. It reads
            // the header itself, and its route still asks for an account, so it answers 401 as well.
            "/api/api-keys/status"
        ];

        foreach (var route in ordinaryRoutes)
        {
            using var response = await client.GetAsync(route);

            Assert.True(
                response.StatusCode == HttpStatusCode.Unauthorized,
                $"GET {route} answered a caller holding only the API key {(int)response.StatusCode}, not 401.");
        }

        using var document = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
    }

    /// <summary>
    /// The two endpoints an operator reaches for when the database credentials are wrong read the key
    /// out of the header themselves, so they answer a caller that has no session and cannot get one.
    /// What comes back is each endpoint's own refusal of the empty body it was sent, which only a
    /// caller that got all the way to the handler can be given.
    /// </summary>
    [Fact]
    public async Task TheRepairEndpointsStillAnswerTheKeyCaller()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient();
        client.DefaultRequestHeaders.Add(
            "X-Api-Key",
            host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey());

        using var credentials = await client.PostAsJsonAsync(
            "/api/setup/credentials", new SetupCredentialsRequest());
        using var external = await client.PostAsJsonAsync(
            "/api/setup/external", new SetExternalDbCredentialsRequest());

        Assert.Equal(HttpStatusCode.BadRequest, credentials.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, external.StatusCode);
    }

    /// <summary>
    /// The metrics gate is its own middleware with its own setting, and it validates the header itself
    /// rather than reading a principal, so narrowing the handler leaves it exactly as it was: off, the
    /// scrape is public; on, it takes the key in the header and refuses without it.
    /// </summary>
    [Fact]
    public async Task MetricsStillHonoursItsOwnSetting()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        using var client = host.Application.CreateClient();

        using (var publicScrape = await client.GetAsync("/metrics"))
        {
            Assert.Equal(HttpStatusCode.OK, publicScrape.StatusCode);
        }

        host.Application.Services.GetRequiredService<IStateService>().SetRequireAuthForMetrics(true);

        using (var refused = await client.GetAsync("/metrics"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        client.DefaultRequestHeaders.Add(
            "X-Api-Key",
            host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey());

        using var scrape = await client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
    }

    /// <summary>
    /// ⭐The whole first run, on an installation that has no accounts and no sessions, with the header
    /// no longer authorizing anything: claim the installation with the key in the request body, sign in
    /// with the key and those credentials, and reach a route that needs an account. Narrowing the header
    /// before this path existed would have left a scripted install with no way in at all, so the check
    /// is the flow end to end rather than one endpoint's status code.
    /// </summary>
    [Fact]
    public async Task AFreshInstallClaimsItselfAndSignsIn()
    {
        using var host = new EndpointAuthorizationHost();
        using var isolationClient = host.Application.CreateClient();
        await host.AssertIsolationAsync(isolationClient);

        // An installation nobody has claimed yet has to be made rather than assumed: the database
        // outlives a test run, and every host-based test that signs in leaves an account behind. The
        // rows cleared here are only ever the suite's own, because the host names a database of its
        // own rather than the one the developer's installation runs on. Every class sharing it runs
        // in this collection, so none of them is mid-flight while these rows go, and the account this
        // test creates goes the same way at the end.
        var dbContextFactory = host.Application.Services.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using (var context = await dbContextFactory.CreateDbContextAsync())
        {
            await context.UserAccounts.ExecuteDeleteAsync();
            Assert.Equal(0, await context.UserAccounts.CountAsync());
        }

        var apiKey = host.Application.Services.GetRequiredService<ApiKeyService>().GetApiKey();
        var username = $"operator-{Guid.NewGuid():N}";
        using var client = host.Application.CreateClient(
            new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        // The state this whole phase creates: the key opens no ordinary route, so everything below is
        // the only way in that is left.
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        using (var keyOnly = await client.GetAsync("/api/sessions"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, keyOnly.StatusCode);
        }

        client.DefaultRequestHeaders.Remove("X-Api-Key");

        using (var created = await client.PostAsJsonAsync(
            "/api/account-setup/first-admin",
            new AccountCredentialsRequest { Username = username, Password = Password, ApiKey = apiKey }))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        // Signing in changes something, so it carries the token the page holds from the status call it
        // makes on load. Nothing else in this flow needs one: the claim endpoint is key-authenticated.
        await EndpointAuthorizationHost.PrimeAntiforgeryAsync(client);
        using (var login = await client.PostAsJsonAsync(
            "/api/auth/login",
            new LoginRequest { ApiKey = apiKey, Username = username, Password = Password }))
        {
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }

        var status = await client.GetFromJsonAsync<JsonElement>("/api/auth/status");
        Assert.True(status.GetProperty("isAuthenticated").GetBoolean());
        Assert.Equal("admin", status.GetProperty("sessionType").GetString());

        using var accountRoute = await client.GetAsync("/api/sessions");
        Assert.Equal(HttpStatusCode.OK, accountRoute.StatusCode);

        await using var stored = await dbContextFactory.CreateDbContextAsync();
        var account = await stored.UserAccounts.SingleAsync();
        Assert.Equal(username, account.Username);
        Assert.True(account.IsMainAdmin);

        await stored.UserAccounts.ExecuteDeleteAsync();
    }
}
