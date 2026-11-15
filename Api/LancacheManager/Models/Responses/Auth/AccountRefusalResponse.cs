namespace LancacheManager.Models;

/// <summary>
/// Refusal body for the account-management endpoints. <see cref="StageKey"/> is the i18n key the
/// browser renders; <see cref="Error"/> is the English sentence a client that does not localize falls
/// back to, so both travel on every refusal.
/// </summary>
public class AccountRefusalResponse
{
    /// <summary>
    /// No account with that id is addressable by this caller. One key for "there is no such account"
    /// and for "there is one and you may not see it", because a user who could tell those apart would
    /// learn which admin accounts exist by asking for ids until one answered differently.
    /// </summary>
    public const string AccountNotFound = "errors.accounts.notFound";

    /// <summary>
    /// The account that owns the installation cannot be deleted, disabled or edited by anybody.
    /// Closing delete alone still leaves resetting its password and signing in as it.
    /// </summary>
    public const string MainAdminProtected = "errors.accounts.mainAdminProtected";

    /// <summary>
    /// Deleting every account is the owning account's alone.
    /// </summary>
    public const string WipeRequiresMainAdmin = "errors.accounts.wipeRequiresMainAdmin";

    /// <summary>
    /// The caller's own account cannot be deleted or disabled. Both end the caller's sessions, and
    /// neither can be undone by the person who did it. Renaming their own account and changing their
    /// own password stay open.
    /// </summary>
    public const string SelfProtected = "errors.accounts.selfProtected";

    /// <summary>
    /// A user-role account changes only its own account: editing, disabling or deleting another
    /// account is left to an administrator.
    /// </summary>
    public const string OwnAccountOnly = "errors.accounts.ownAccountOnly";

    /// <summary>
    /// A <c>Security__*</c> environment variable allows this account (its owner subject or subject
    /// allow list), so deleting it would only let its next sign-in create it again. It is removed
    /// from the variable first.
    /// </summary>
    public const string EnvironmentManaged = "errors.accounts.environmentManaged";

    /// <summary>Another account already holds that username.</summary>
    public const string UsernameTaken = "errors.accounts.usernameTaken";

    /// <summary>
    /// The username or the password does not meet the rules an account's credentials have to pass.
    /// One key for both, because <see cref="Error"/> carries which rule it was.
    /// </summary>
    public const string CredentialsRejected = "errors.accounts.credentialsRejected";

    /// <summary>
    /// Sign-in is turned off, and that mode keeps no accounts, so none is created. An account made
    /// then could never be signed in to, and it would stop the shared session changing access.
    /// </summary>
    public const string SignInOff = "errors.accounts.signInOff";

    /// <summary>
    /// The switch to unauthenticated access deletes every account and waits for the person to confirm
    /// it. <see cref="Context"/> carries the account count under <c>count</c>.
    /// </summary>
    public const string WipeConfirmRequired = "accessSetup.wipeConfirm.required";

    public string StageKey { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;

    /// <summary>Values the stage key's text is filled with; null for a refusal whose text has none.</summary>
    public Dictionary<string, object?>? Context { get; set; }
}
