using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace LancacheManager.Controllers.Filters;

public class RequiresGcManagementFilter : IAsyncActionFilter
{
    private readonly IConfiguration _configuration;

    public RequiresGcManagementFilter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var isEnabled = _configuration.GetValue<bool>("Optimizations:EnableGarbageCollectionManagement", false);
        if (!isEnabled)
        {
            context.Result = new NotFoundObjectResult(new ErrorResponse { Error = "Garbage collection management is disabled" });
            return;
        }
        await next();
    }
}

public class RequiresGcManagementAttribute : TypeFilterAttribute
{
    public RequiresGcManagementAttribute() : base(typeof(RequiresGcManagementFilter)) { }
}
