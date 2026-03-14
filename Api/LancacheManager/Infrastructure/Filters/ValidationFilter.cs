using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Filters;

/// <summary>
/// Action filter that runs FluentValidation validators on action arguments
/// and returns a consistent ValidationErrorResponse.
/// Replaces the deprecated FluentValidation.AspNetCore auto-validation pipeline.
/// </summary>
public class ValidationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // Run FluentValidation validators against each action argument
        foreach (var (_, value) in context.ActionArguments)
        {
            if (value is null) continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(value.GetType());
            if (context.HttpContext.RequestServices.GetService(validatorType) is IValidator validator)
            {
                var validationContext = new ValidationContext<object>(value);
                var result = await validator.ValidateAsync(validationContext, context.HttpContext.RequestAborted);

                if (!result.IsValid)
                {
                    foreach (var error in result.Errors)
                    {
                        context.ModelState.AddModelError(error.PropertyName, error.ErrorMessage);
                    }
                }
            }
        }

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
            return;
        }

        await next();
    }
}
