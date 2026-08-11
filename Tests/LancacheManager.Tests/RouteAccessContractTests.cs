using LancacheManager.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace LancacheManager.Tests;

[Collection(nameof(EndpointAuthorizationCollection))]
public sealed class RouteAccessContractTests
{
    /// <summary>
    /// The complete set of leading words used by the application's GET actions. A GET reads, so a new
    /// GET named for something that writes lands outside this set and fails.
    /// </summary>
    private static readonly string[] ReadPrefixes =
    [
        "Cache",
        "Check",
        "Dashboard",
        "Eviction",
        "Get",
        "Is",
        "List",
        "Search",
        "Validate"
    ];

    [Fact]
    public async Task NoGetRouteChangesState()
    {
        using var host = new EndpointAuthorizationHost();
        using var client = host.Application.CreateClient();

        await host.AssertIsolationAsync(client);

        var writing = Routes(host.Application.Services)
            .Where(route => string.Equals(route.Method, "GET", StringComparison.OrdinalIgnoreCase))
            .Where(route => route.Action is string action
                && !ReadPrefixes.Any(prefix =>
                    action.StartsWith(prefix, StringComparison.Ordinal)
                    && (action.Length == prefix.Length || char.IsUpper(action[prefix.Length]))))
            .Select(route => $"{route.Action} (GET /{route.Route})")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            writing.Length == 0,
            $"A GET must not change state, and these GET actions are named for something that does: {string.Join(", ", writing)}. "
            + "Move the action to POST, PUT, PATCH or DELETE, or name it for the read it performs.");
    }

    [Fact]
    public void TheSessionCookieStaysSameSiteLax()
    {
        using var host = new EndpointAuthorizationHost();
        using var scope = host.Application.Services.CreateScope();

        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();
        var httpContext = new DefaultHttpContext();

        sessionService.SetSessionCookie(httpContext, "route-access-contract", DateTime.UtcNow.AddHours(1));

        var setCookie = httpContext.Response.Headers.SetCookie.ToString();

        Assert.True(
            setCookie.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase),
            $"The session cookie must stay SameSite=Lax; it is what stops a cross-site request carrying it. Got '{setCookie}'.");
    }

    private static RouteAccess[] Routes(IServiceProvider services)
    {
        return services.GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Select(description => new RouteAccess(
                description.HttpMethod ?? "ANY",
                description.RelativePath ?? string.Empty,
                // Null for the routes mapped outside a controller: /health, /api/version, /metrics and
                // the documentation routes. Those are pinned by EndpointAuthorizationContractTests.
                (description.ActionDescriptor as ControllerActionDescriptor)?.ActionName))
            .ToArray();
    }

    private sealed record RouteAccess(string Method, string Route, string? Action);
}
