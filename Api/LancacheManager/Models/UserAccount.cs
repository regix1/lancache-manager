using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// A person who signs in with a username and password. Not every session has one: a guest,
/// an API-key caller and the disabled-authentication session all run without an account row.
/// </summary>
public class UserAccount
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Stored as PostgreSQL citext, so the unique index treats "Admin" and "admin" as the
    /// same name while keeping the casing the account was created with. citext has no
    /// length variant, so nothing caps this column at the database.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>
    /// Admin or User. <see cref="SessionType.Admin"/> is the zero value of the enum, so a
    /// create that leaves this unset produces an admin.
    /// </summary>
    public SessionType Role { get; set; }

    /// <summary>
    /// True for at most one row, enforced by IX_UserAccounts_IsMainAdmin.
    /// </summary>
    public bool IsMainAdmin { get; set; }

    public bool IsDisabled { get; set; }

    // Timestamps - all in UTC
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastLoginAtUtc { get; set; }
}
