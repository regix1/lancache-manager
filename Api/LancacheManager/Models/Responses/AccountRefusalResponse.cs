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
    /// The account that owns the installation cannot be deleted, disabled, demoted or edited, by
    /// anybody. Closing delete alone leaves demotion, and closing all three leaves resetting its
    /// password and signing in as it.
    /// </summary>
    public const string MainAdminProtected = "errors.accounts.mainAdminProtected";

    /// <summary>
    /// Only the account that owns the installation hands out the admin role, whether by creating an
    /// account with it or by moving an existing account onto it.
    /// </summary>
    public const string AdminRoleRequiresMainAdmin = "errors.accounts.adminRoleRequiresMainAdmin";

    /// <summary>
    /// The caller's own account cannot be deleted, disabled or moved to another role. All three end
    /// the caller's sessions, and none of them can be undone by the person who did it: only the
    /// account that owns the installation creates accounts or grants the admin role. Renaming your
    /// own account and changing your own password stay open.
    /// </summary>
    public const string SelfProtected = "errors.accounts.selfProtected";

    /// <summary>Guest is a session a visitor is handed, not a role an account can hold.</summary>
    public const string RoleNotAssignable = "errors.accounts.roleNotAssignable";

    /// <summary>Another account already holds that username.</summary>
    public const string UsernameTaken = "errors.accounts.usernameTaken";

    /// <summary>
    /// The username or the password does not meet the rules an account's credentials have to pass.
    /// One key for both, because <see cref="Error"/> carries which rule it was.
    /// </summary>
    public const string CredentialsRejected = "errors.accounts.credentialsRejected";

    public string StageKey { get; set; } = string.Empty;

    public string Error { get; set; } = string.Empty;
}
