using FluentValidation;
using System.Net;

namespace LancacheManager.Validators;

/// <summary>
/// Common validation extension methods for reuse across validators
/// </summary>
public static class CommonValidators
{
    /// <summary>
    /// Validates that a string is a valid IP address format
    /// </summary>
    public static IRuleBuilderOptions<T, string?> ValidIpAddress<T>(this IRuleBuilder<T, string?> ruleBuilder)
    {
        return ruleBuilder
            .Must(ip => string.IsNullOrEmpty(ip) || IPAddress.TryParse(ip, out _))
            .WithMessage("Invalid IP address format");
    }

    /// <summary>
    /// Validates that a string is a valid IP address (required)
    /// </summary>
    public static IRuleBuilderOptions<T, string> RequiredValidIpAddress<T>(this IRuleBuilder<T, string> ruleBuilder)
    {
        return ruleBuilder
            .NotEmpty().WithMessage("IP address is required")
            .Must(ip => IPAddress.TryParse(ip, out _))
            .WithMessage("Invalid IP address format");
    }

    /// <summary>
    /// Validates that a string doesn't contain potentially dangerous characters
    /// </summary>
    public static IRuleBuilderOptions<T, string?> SafeString<T>(this IRuleBuilder<T, string?> ruleBuilder, int maxLength = 500)
    {
        return ruleBuilder
            .MaximumLength(maxLength).WithMessage($"Must be {maxLength} characters or less")
            .Must(s => s == null || (!s.Contains('<') && !s.Contains('>')))
            .WithMessage("String contains invalid characters");
    }

    /// <summary>
    /// Validates a nickname field (alphanumeric with spaces, hyphens, underscores)
    /// </summary>
    public static IRuleBuilderOptions<T, string> ValidNickname<T>(this IRuleBuilder<T, string> ruleBuilder, int maxLength = 100)
    {
        return ruleBuilder
            .NotEmpty().WithMessage("Nickname is required")
            .MaximumLength(maxLength).WithMessage($"Nickname must be {maxLength} characters or less")
            .Matches(@"^[a-zA-Z0-9\s\-_]+$").WithMessage("Nickname contains invalid characters (only letters, numbers, spaces, hyphens, and underscores allowed)");
    }

    /// <summary>
    /// Validates an optional description field
    /// </summary>
    public static IRuleBuilderOptions<T, string?> ValidDescription<T>(this IRuleBuilder<T, string?> ruleBuilder, int maxLength = 500)
    {
        return ruleBuilder
            .MaximumLength(maxLength).WithMessage($"Description must be {maxLength} characters or less")
            .When(x => !string.IsNullOrEmpty((x as dynamic)?.Description as string), ApplyConditionTo.CurrentValidator);
    }

    /// <summary>
    /// Validates a Unix timestamp is valid (positive number)
    /// </summary>
    public static IRuleBuilderOptions<T, long> ValidUnixTimestamp<T>(this IRuleBuilder<T, long> ruleBuilder, string fieldName)
    {
        return ruleBuilder
            .GreaterThan(0).WithMessage($"{fieldName} must be a valid Unix timestamp");
    }
}
