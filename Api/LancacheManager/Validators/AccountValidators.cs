using FluentValidation;
using LancacheManager.Models;

namespace LancacheManager.Validators;

/// <summary>
/// The rules an account's username and password have to pass. Discovered by
/// AddValidatorsFromAssemblyContaining (Program.cs:83) and run by ValidationFilter (Program.cs:66),
/// so binding <see cref="AccountCredentialsRequest"/> is the whole wiring.
/// </summary>
public class AccountCredentialsRequestValidator : AbstractValidator<AccountCredentialsRequest>
{
    public AccountCredentialsRequestValidator()
    {
        // citext has no length variant, so nothing bounds the username at the database and a btree
        // index entry past roughly 2704 bytes fails at insert. 64 is what SteamLoginRequest.Username
        // already caps at.
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required")
            .MaximumLength(64).WithMessage("Username cannot exceed 64 characters");

        // Stop at the first failure so the checks below never run against a null or empty password.
        //
        // SetupController.CheckPassword stays at eight characters so an existing external server
        // still connects. A new embedded role password uses CheckNewRolePassword, which matches
        // these classes.
        //
        // The upper bound is not cosmetic: PBKDF2 runs over the whole password at the iteration count
        // Program.cs configures the hasher with, so an unbounded password is a way to buy a lot of
        // server time with one request. 256 is what SteamLoginRequest.Password already caps at.
        RuleFor(x => x.Password)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Password is required")
            .MinimumLength(PasswordRules.AccountMinimumLength).WithMessage(PasswordRules.MinimumLengthMessage)
            .MaximumLength(256).WithMessage("Password cannot exceed 256 characters")
            .Must(PasswordRules.UsesThreeCharacterClasses)
            .WithMessage(PasswordRules.CharacterClassesMessage)
            .NotEqual(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .WithMessage("Password cannot be the same as the username");
    }
}
