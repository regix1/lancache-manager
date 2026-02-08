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

}
