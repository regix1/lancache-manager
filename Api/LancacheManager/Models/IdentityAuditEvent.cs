namespace LancacheManager.Models;

/// <summary>
/// The identity events that are recorded. Scoped to the events that change who can sign in and what
/// they may do, which is the set that answers "who could have done anything else".
/// </summary>
public enum IdentityAuditEvent
{
    AccountCreated,
    AccountDeleted,
    AccountDisabled,
    AccountEnabled,
    RoleChanged,
    PasswordChanged,
    LoginSucceeded,
    LoginFailed,
    ApiKeyRotated,
    MainAdminPasswordRecovered
}
