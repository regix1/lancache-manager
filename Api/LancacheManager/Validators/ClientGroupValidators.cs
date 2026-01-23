using FluentValidation;
using LancacheManager.Models;

namespace LancacheManager.Validators;

/// <summary>
/// Validator for CreateClientGroupRequest
/// </summary>
public class CreateClientGroupRequestValidator : AbstractValidator<CreateClientGroupRequest>
{
    public CreateClientGroupRequestValidator()
    {
        RuleFor(x => x.Nickname)
            .NotEmpty().WithMessage("Nickname is required")
            .MaximumLength(100).WithMessage("Nickname must be 100 characters or less")
            .Matches(@"^[a-zA-Z0-9\s\-_]+$").WithMessage("Nickname contains invalid characters");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must be 500 characters or less")
            .When(x => !string.IsNullOrEmpty(x.Description));

        RuleForEach(x => x.InitialIps)
            .ValidIpAddress()
            .When(x => x.InitialIps != null && x.InitialIps.Count > 0);
    }
}

/// <summary>
/// Validator for UpdateClientGroupRequest
/// </summary>
public class UpdateClientGroupRequestValidator : AbstractValidator<UpdateClientGroupRequest>
{
    public UpdateClientGroupRequestValidator()
    {
        RuleFor(x => x.Nickname)
            .NotEmpty().WithMessage("Nickname is required")
            .MaximumLength(100).WithMessage("Nickname must be 100 characters or less")
            .Matches(@"^[a-zA-Z0-9\s\-_]+$").WithMessage("Nickname contains invalid characters");

        RuleFor(x => x.Description)
            .MaximumLength(500).WithMessage("Description must be 500 characters or less")
            .When(x => !string.IsNullOrEmpty(x.Description));
    }
}

/// <summary>
/// Validator for AddMemberRequest
/// </summary>
public class AddMemberRequestValidator : AbstractValidator<AddMemberRequest>
{
    public AddMemberRequestValidator()
    {
        RuleFor(x => x.ClientIp)
            .RequiredValidIpAddress();
    }
}
