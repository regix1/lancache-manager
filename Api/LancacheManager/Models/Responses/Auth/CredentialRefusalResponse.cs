namespace LancacheManager.Models;

/// <summary>
/// Refusal body for signing in and for changing your own password. <see cref="StageKey"/> is the i18n
/// key the browser renders; <see cref="Error"/> is the English sentence a client that does not localize
/// falls back to, so both travel on every refusal.
/// </summary>
public class CredentialRefusalResponse
{
    /// <summary>
    /// Every way a sign-in can fail: an unknown username, a wrong password, a wrong or missing API key,
    /// a disabled account, and an account with too many recent failures. One key rather than five,
    /// because a caller who can tell those apart can learn which usernames exist, which passwords are
    /// half-right, and whether their guessing is working.
    /// </summary>
    public const string InvalidCredentials = "errors.login.invalidCredentials";

    /// <summary>The current password submitted to the change-password endpoint did not match.</summary>
    public const string PasswordIncorrect = "errors.password.incorrect";

    /// <summary>
    /// Too many recent failed password attempts for this account. Named here, unlike on the sign-in
    /// path, because the caller already holds a session for the account and so learns nothing about
    /// whether it exists.
    /// </summary>
    public const string AccountLocked = "errors.password.locked";

    /// <summary>The proposed new password does not meet the rules an account's password has to pass.</summary>
    public const string PasswordRejected = "errors.password.rejected";

    /// <summary>
    /// The caller has a session but no account behind it, so there is no password to change. An
    /// <c>X-Api-Key</c> caller and the session used while authentication is disabled both arrive this
    /// way.
    /// </summary>
    public const string AccountRequired = "errors.password.accountRequired";

    public string StageKey { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
