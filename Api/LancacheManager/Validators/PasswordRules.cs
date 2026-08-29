namespace LancacheManager.Validators;

/// <summary>
/// The account-password policy shared by the FluentValidation account validator and the setup
/// endpoints' embedded-role password check (SetupController). One home for the length floor,
/// the character-class rule, and their user-facing sentences, so the two paths cannot drift.
/// </summary>
public static class PasswordRules
{
    /// <summary>Floor for account and embedded-role passwords. The 8-character floor for an
    /// already-running EXTERNAL server's password stays separate in SetupController.CheckPassword.</summary>
    public const int AccountMinimumLength = 12;

    public const string MinimumLengthMessage = "Password must be at least 12 characters";

    public const string CharacterClassesMessage =
        "Password must use at least three of: lowercase letters, uppercase letters, digits, and other characters";

    public static bool UsesThreeCharacterClasses(string password) =>
        (password.Any(char.IsLower) ? 1 : 0)
        + (password.Any(char.IsUpper) ? 1 : 0)
        + (password.Any(char.IsDigit) ? 1 : 0)
        + (password.Any(character => !char.IsLetterOrDigit(character)) ? 1 : 0) >= 3;
}
