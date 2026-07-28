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

        RuleFor(x => x.SeparateMemberRows)
            .NotNull().WithMessage("Separate member rows is required");
    }
}

/// <summary>
/// Validator for SetMembersRequest
/// </summary>
/// <remarks>
/// This layer fails the whole request on a malformed address. The controller normalizes the list a
/// second time so it can name the offending entries, and so that an address another group owns is
/// reported per item rather than failing the save.
/// </remarks>
public class SetMembersRequestValidator : AbstractValidator<SetMembersRequest>
{
    public SetMembersRequestValidator()
    {
        RuleFor(x => x.ClientIps)
            .NotNull().WithMessage("Client IPs are required");

        RuleForEach(x => x.ClientIps)
            .ValidIpAddress()
            .When(x => x.ClientIps != null && x.ClientIps.Count > 0);
    }
}
