using LancacheManager.Models;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Refuses a request that changes something unless it carries the antiforgery token
/// <see cref="Security.AntiforgeryToken"/> handed the browser, so a cookie on its own is never enough
/// to act.
///
/// Registered once for every controller rather than as an attribute on each route, because an
/// attribute per route is a hole the first time somebody forgets one. Being an MVC filter is also what
/// keeps it away from the SignalR hubs: their own negotiate call is a POST, and it never reaches this.
///
/// The framework ships [AutoValidateAntiforgeryToken] for the same job, but it resolves a filter that
/// only AddControllersWithViews registers and this application calls AddControllers, so wiring it up
/// would mean pulling in the whole view stack for one type. The check itself is still the framework's.
/// </summary>
public class AntiforgeryFilter : IAsyncAuthorizationFilter
{
    private readonly IAntiforgery _antiforgery;

    public AntiforgeryFilter(IAntiforgery antiforgery)
    {
        _antiforgery = antiforgery;
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // The routes that prove their caller with the installation's API key opt out with
        // [IgnoreAntiforgeryToken]. Two of them accept a session as well, and ask that caller for the
        // token themselves, because the opt-out is per route and cannot tell the two apart
        // (Controllers/SetupController.cs, RequireApiKeyAsync).
        if (context.Filters.OfType<IgnoreAntiforgeryTokenAttribute>().Any())
        {
            return;
        }

        // A read changes nothing, so it needs nothing, which is what keeps this off the great majority
        // of the traffic.
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
        {
            return;
        }

        try
        {
            await _antiforgery.ValidateRequestAsync(context.HttpContext);
        }
        catch (AntiforgeryValidationException)
        {
            context.Result = new BadRequestObjectResult(
                ApiResponse.Error(Security.AntiforgeryToken.MissingTokenMessage));
        }
    }
}
