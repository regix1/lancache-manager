namespace LancacheManager.Tests;

/// <summary>
/// Every answer carries <c>Cross-Origin-Resource-Policy: same-site</c>, beside the other security
/// headers the app sets on every response, so a page on another site cannot embed or read the app's
/// answers through a cross-origin load. Same-site rather than same-origin keeps the split-hostname
/// reverse-proxy setup working, where the page and the API sit on two hostnames under one domain and
/// the page loads game banners from the API (<c>docs-site/content/reverse-proxy.en.md</c>).
/// <c>GET /api/auth/status</c> is the first call every page makes.
/// </summary>
// The host resolves the process-wide shared sessions, so this runs in the collection that already
// serializes every class touching them.
[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class ApiAnswersCarryResourcePolicyTests
{
    [Fact]
    public async Task TheAuthStatusAnswer_CarriesASameSiteResourcePolicyAsync()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();
        await host.AssertIsolationAsync(client);

        using var answer = await client.GetAsync("/api/auth/status");

        var policy = answer.Headers.TryGetValues("Cross-Origin-Resource-Policy", out var values) ? string.Join(", ", values) : "absent";
        Assert.True(
            policy == "same-site",
            $"GET /api/auth/status answered {(int)answer.StatusCode} with Cross-Origin-Resource-Policy {policy}");
    }
}
