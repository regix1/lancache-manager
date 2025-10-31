using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

/// <summary>
/// Authorization attribute that requires the primary API key
/// Ensures only users with the primary API key can access sensitive admin operations
/// </summary>
public class RequirePrimaryAdminAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;
        var configuration = httpContext.RequestServices.GetRequiredService<IConfiguration>();

        // Check if authentication is globally disabled
        var authEnabled = configuration.GetValue<bool>("Security:EnableAuthentication", true);
        if (!authEnabled)
        {
            // Skip authentication if disabled - allow access
            base.OnActionExecuting(context);
            return;
        }

        var apiKeyService = httpContext.RequestServices.GetRequiredService<ApiKeyService>();

        // Check for API key in header
        var apiKey = httpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                error = "Authentication required",
                message = "Please provide X-Api-Key header"
            });
            return;
        }

        // Check if this is the ADMIN API key (not user key)
        if (!apiKeyService.IsPrimaryApiKey(apiKey))
        {
            context.Result = new ObjectResult(new
            {
                error = "Admin API key required",
                message = "This operation requires the ADMIN API key. You are using the USER API key which has limited access."
            })
            {
                StatusCode = 403 // Forbidden
            };
            return;
        }

        // All checks passed - allow access
        base.OnActionExecuting(context);
    }
}
