namespace LancacheManager.Models;

/// <summary>
/// One account as the account-management screen reads it. The password hash is not on it: nothing
/// outside <see cref="UserAccount"/> ever needs to see one.
/// </summary>
public class AccountResponse
{
    public Guid Id { get; set; }

    public string Username { get; set; } = string.Empty;

    public SessionType Role { get; set; }

    public bool IsMainAdmin { get; set; }

    public bool IsDisabled { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Null until the account signs in for the first time.</summary>
    public DateTime? LastLoginAtUtc { get; set; }
}

/// <summary>Body of <c>POST /api/accounts</c>.</summary>
public class CreateAccountRequest
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Required rather than defaulted, because <see cref="SessionType.Admin"/> is the zero value of
    /// the enum: an omitted field would ask for an admin while the caller meant a user.
    /// </summary>
    public required SessionType Role { get; set; }
}

/// <summary>Body of <c>PUT /api/accounts/{id}</c>.</summary>
public class EditAccountRequest
{
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Null leaves the stored password alone, which is what renaming an account does. A value
    /// replaces it, which is the only way back in for an account that is not the main administrator:
    /// the recovery endpoint resets that one account and no other.
    /// </summary>
    public string? Password { get; set; }
}

/// <summary>Body of <c>PUT /api/accounts/{id}/role</c>.</summary>
public class SetAccountRoleRequest
{
    /// <summary>Required for the same reason <see cref="CreateAccountRequest.Role"/> is.</summary>
    public required SessionType Role { get; set; }
}

/// <summary>Body of <c>PUT /api/accounts/{id}/disabled</c>.</summary>
public class SetAccountDisabledRequest
{
    /// <summary>
    /// Required rather than defaulted: false is the zero value of the type, so an omitted field
    /// would read as "enable this account" whichever the caller meant.
    /// </summary>
    public required bool Disabled { get; set; }
}
