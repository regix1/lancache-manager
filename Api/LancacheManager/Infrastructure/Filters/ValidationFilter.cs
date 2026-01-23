using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Action filter that intercepts invalid model state from FluentValidation
/// and returns a consistent ValidationErrorResponse
/// </summary>
public class ValidationFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ModelState.IsValid)
        {
            var errors = context.ModelState
                .Where(e => e.Value?.Errors.Count > 0)
                .SelectMany(e => e.Value!.Errors.Select(err => new ValidationFieldError
                {
                    Field = e.Key,
                    Message = err.ErrorMessage
                }))
                .ToList();

            var response = new ValidationErrorResponse
            {
                Error = "Validation failed",
                Errors = errors
            };

            context.Result = new BadRequestObjectResult(response);
        }
    }

    public void OnActionExecuted(ActionExecutedContext context)
    {
        // No action needed after execution
    }
}
