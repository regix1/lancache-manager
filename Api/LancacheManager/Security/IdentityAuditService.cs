using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

/// <summary>
/// Writes the identity audit trail. Adding a row is the only thing this does; nothing here updates
/// or deletes one. [29e]
/// </summary>
public class IdentityAuditService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly ILogger<IdentityAuditService> _logger;

    public IdentityAuditService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        ILogger<IdentityAuditService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
    }

    /// <summary>
    /// Records one identity event, and never throws. The caller is in the middle of the operation
    /// being recorded, so a failure here is logged and swallowed: a login or a key rotation that
    /// fails because the audit write failed turns a logging fault into an outage. [29d]
    ///
    /// The row goes on its own context rather than the caller's, so a failure leaves nothing tracked
    /// on the caller's context and commits nothing the caller had pending.
    ///
    /// Both actor arguments are nullable because a caller need not have either: a request carrying
    /// only an API key is authenticated with no account and no session row behind it. [29c]
    /// </summary>
    /// <param name="auditEvent">What happened.</param>
    /// <param name="performedByAccountId">The account that did it, or null.</param>
    /// <param name="performedBySessionId">The session it was done from, or null.</param>
    /// <param name="targetAccountId">The account it was done to, or null when the event names none.</param>
    public async Task RecordAsync(
        IdentityAuditEvent auditEvent,
        Guid? performedByAccountId,
        Guid? performedBySessionId,
        Guid? targetAccountId)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync();

            context.IdentityAuditEntries.Add(new IdentityAuditEntry
            {
                Event = auditEvent,
                PerformedByAccountId = performedByAccountId,
                PerformedBySessionId = performedBySessionId,
                TargetAccountId = targetAccountId,
                PerformedAtUtc = DateTime.UtcNow
            });

            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to record identity event {Event} performed by account {PerformedByAccountId} on account {TargetAccountId}",
                auditEvent,
                performedByAccountId,
                targetAccountId);
        }
    }
}
