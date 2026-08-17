using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

/// <summary>
/// The account that owns the installation is hidden from every caller that is not that account.
/// Users still do not see any administrator; other administrators see every account except this one.
/// </summary>
internal static class MainAdminVisibility
{
    /// <summary>
    /// The owner's account id when the caller must not see that account, or null when nothing is
    /// hidden: the owner themselves, and a caller with no account row (API key only, or
    /// authentication off).
    /// </summary>
    public static async Task<Guid?> HiddenAccountIdAsync(
        AppDbContext context,
        UserSession? caller,
        CancellationToken cancellationToken = default)
    {
        if (caller?.AccountId is not { } callerAccountId)
        {
            return null;
        }

        var callerOwnsInstallation = await context.UserAccounts
            .AsNoTracking()
            .Where(a => a.Id == callerAccountId)
            .Select(a => a.IsMainAdmin)
            .FirstOrDefaultAsync(cancellationToken);

        if (callerOwnsInstallation)
        {
            return null;
        }

        return await context.UserAccounts
            .AsNoTracking()
            .Where(a => a.IsMainAdmin)
            .Select(a => (Guid?)a.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The accounts a caller may see and name. A user is answered without the administrators; an
    /// administrator who is not the owner is answered without the owner; the owner and a caller
    /// with no account row are answered with everybody.
    /// </summary>
    public static IQueryable<UserAccount> AccountsVisibleTo(AppDbContext context, UserAccount? caller)
    {
        if (caller is { Role: SessionType.User })
        {
            return context.UserAccounts.Where(a => a.Role != SessionType.Admin);
        }

        if (caller is { IsMainAdmin: false })
        {
            return context.UserAccounts.Where(a => !a.IsMainAdmin);
        }

        return context.UserAccounts;
    }

    public static IQueryable<UserSession> SessionsVisibleTo(
        IQueryable<UserSession> sessions,
        Guid? hiddenAccountId)
    {
        // AccountId is null on guest and API-key sessions. SQL treats NULL != id as unknown, so
        // a bare inequality would hide those rows along with the owner's.
        return hiddenAccountId is { } id
            ? sessions.Where(s => s.AccountId == null || s.AccountId != id)
            : sessions;
    }
}
