using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Security;

/// <summary>
/// Authentication attribute that checks configuration to determine if authentication is required
/// </summary>
public class ConditionalAuthAttribute : ActionFilterAttribute
{
    private readonly string _configKey;
    private readonly bool _defaultRequireAuth;

    /// <summary>
    /// Creates a conditional authentication attribute
    /// </summary>
    /// <param name="configKey">Configuration key to check (e.g., "Security:RequireAuthForMetrics")</param>
    /// <param name="defaultRequireAuth">Default value if config key is not found</param>
    public ConditionalAuthAttribute(string configKey, bool defaultRequireAuth = false)
    {
        _configKey = configKey;
        _defaultRequireAuth = defaultRequireAuth;
    }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var configuration = context.HttpContext.RequestServices.GetService<IConfiguration>();
        var apiKeyService = context.HttpContext.RequestServices.GetService<ApiKeyService>();

        if (configuration == null || apiKeyService == null)
        {
            context.Result = new StatusCodeResult(500);
            return;
        }

        // Check if authentication is required based on configuration
        var requireAuth = configuration.GetValue<bool>(_configKey, _defaultRequireAuth);

        if (requireAuth)
        {
            var apiKey = context.HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();

            if (string.IsNullOrEmpty(apiKey) || !apiKeyService.ValidateApiKey(apiKey))
            {
                context.Result = new UnauthorizedObjectResult(new
                {
                    error = "API key required",
                    configKey = _configKey
                });
                return;
            }
        }

        base.OnActionExecuting(context);
    }
}