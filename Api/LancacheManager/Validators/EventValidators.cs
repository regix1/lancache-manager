using FluentValidation;
using LancacheManager.Models;

namespace LancacheManager.Validators;

/// <summary>
/// Validator for CreateEventRequest
/// </summary>
public class CreateEventRequestValidator : AbstractValidator<CreateEventRequest>
{
    public CreateEventRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Event name is required")
            .MaximumLength(200).WithMessage("Event name must be 200 characters or less");

        RuleFor(x => x.StartTime)
            .GreaterThan(0).WithMessage("Start time must be a valid Unix timestamp");

        RuleFor(x => x.EndTime)
            .GreaterThan(0).WithMessage("End time must be a valid Unix timestamp")
            .GreaterThan(x => x.StartTime).WithMessage("End time must be after start time");

        RuleFor(x => x.ColorIndex)
            .InclusiveBetween(1, 8).WithMessage("Color index must be between 1 and 8")
            .When(x => x.ColorIndex.HasValue);

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or less")
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}

/// <summary>
/// Validator for UpdateEventRequest
/// </summary>
public class UpdateEventRequestValidator : AbstractValidator<UpdateEventRequest>
{
    public UpdateEventRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Event name is required")
            .MaximumLength(200).WithMessage("Event name must be 200 characters or less");

        RuleFor(x => x.StartTime)
            .GreaterThan(0).WithMessage("Start time must be a valid Unix timestamp");

        RuleFor(x => x.EndTime)
            .GreaterThan(0).WithMessage("End time must be a valid Unix timestamp")
            .GreaterThan(x => x.StartTime).WithMessage("End time must be after start time");

        RuleFor(x => x.ColorIndex)
            .InclusiveBetween(1, 8).WithMessage("Color index must be between 1 and 8")
            .When(x => x.ColorIndex.HasValue);

        RuleFor(x => x.Description)
            .MaximumLength(1000).WithMessage("Description must be 1000 characters or less")
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}
