using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// One identity event, written once and never changed.
///
/// The actor and target columns hold plain ids rather than foreign keys on purpose: a row has to
/// outlive the account it names, so deleting an account must not take the record of the deletion
/// with it.
/// </summary>
public class IdentityAuditEntry
{
    [Key]
    public long Id { get; set; }

    public IdentityAuditEvent Event { get; set; }

    /// <summary>
    /// The account that performed the event, or null when the caller has no account row. A request
    /// carrying only an API key is authenticated without one. [29c]
    /// </summary>
    public Guid? PerformedByAccountId { get; set; }

    /// <summary>
    /// The session the event was performed from, or null when the caller has no session row. [29c]
    /// </summary>
    public Guid? PerformedBySessionId { get; set; }

    /// <summary>
    /// The account the event was about, or null for an event that names no account.
    /// </summary>
    public Guid? TargetAccountId { get; set; }

    public DateTime PerformedAtUtc { get; set; }
}
