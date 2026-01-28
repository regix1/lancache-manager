using FluentValidation;
using LancacheManager.Models;

namespace LancacheManager.Validators;

/// <summary>
/// Validator for TestApiKeyRequest
/// </summary>
public class TestApiKeyRequestValidator : AbstractValidator<TestApiKeyRequest>
{
    public TestApiKeyRequestValidator()
    {
        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("API key is required");
    }
}

/// <summary>
/// Validator for SaveApiKeyRequest
/// </summary>
public class SaveApiKeyRequestValidator : AbstractValidator<SaveApiKeyRequest>
{
    public SaveApiKeyRequestValidator()
    {
        RuleFor(x => x.ApiKey)
            .NotEmpty().WithMessage("API key is required");
    }
}
