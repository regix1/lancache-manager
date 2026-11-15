using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Refuses a user-role account every change on a route the <c>AccountHolder</c> policy guards. Those
/// routes change the installation or other people, and a user-role account changes only itself. The
/// routes that let it change its own account, sessions or preferences carry
/// <see cref="HandlerChecksCallerAttribute"/>, because their handlers already decide what this caller
/// may touch.
///
/// Registered once for every controller, as <see cref="AntiforgeryFilter"/> is, so a route added later
/// is refused by default instead of open until somebody remembers a check. It runs after that filter,
/// so a forged request without the token is still answered as one.
/// </summary>
public sealed class UserRoleRefusalFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (context.HttpContext.GetUserSession()?.SessionType != SessionType.User)
        {
            return;
        }

        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method)
            || HttpMethods.IsHead(method)
            || HttpMethods.IsOptions(method)
            || HttpMethods.IsTrace(method))
        {
            return;
        }

        var attributes = context.ActionDescriptor.EndpointMetadata;
        if (!attributes.OfType<IAuthorizeData>().Any(authorize => authorize.Policy == "AccountHolder")
            || attributes.OfType<HandlerChecksCallerAttribute>().Any())
        {
            return;
        }

        context.Result = new ForbidResult();
    }
}
