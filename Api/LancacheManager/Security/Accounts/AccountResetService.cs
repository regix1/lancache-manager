using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Security;

public sealed class AccountResetService(
    AppDbContext context,
    IServiceProvider services,
    ILogger<AccountResetService> logger)
{
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await context.Database.OpenConnectionAsync(cancellationToken);
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            Exception? failure = null;
            try { await ResetAsync(connection, cancellationToken); }
            catch (Exception ex) { failure = ex; }

            try { await ReleaseAsync(connection); }
            catch (Exception ex) when (failure is not null)
            {
                logger.LogError(ex, "Account reset cleanup failed; preserving the original startup failure");
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        });
    }

    private async Task ResetAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT pg_advisory_lock(hashtextextended(current_database() || ':' || current_schema() || ':account-reset', 0))";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var reset = await context.AccountResets.SingleOrDefaultAsync(cancellationToken);
        if (reset is not null)
        {
            Validate(reset);
            if (reset.CompletedAtUtc.HasValue) return;
        }
        else
        {
            await using var intent = await context.Database.BeginTransactionAsync(cancellationToken);
            var accounts = await context.UserAccounts.AsNoTracking()
                .Select(account => new UserAccount { Id = account.Id, IsMainAdmin = account.IsMainAdmin, Role = account.Role })
                .ToListAsync(cancellationToken);
            var primary = accounts.Where(account => account.IsMainAdmin).ToList();
            if (accounts.Count > 0 && (primary.Count != 1 || primary[0].Role != SessionType.Admin || primary[0].Id == Guid.Empty))
                throw new InvalidOperationException("Account reset requires exactly one valid primary administrator. Repair the primary account identity before restarting.");

            reset = new AccountReset
            {
                Id = 1,
                PrimaryAccountId = primary.Count == 1 ? primary[0].Id : null,
                AccountIds = accounts.Where(account => !account.IsMainAdmin).Select(account => account.Id).Order().ToArray(),
                StartedAtUtc = DateTime.UtcNow
            };
            Validate(reset);
            context.AccountResets.Add(reset);
            await context.SaveChangesAsync(cancellationToken);
            await intent.CommitAsync(cancellationToken);
        }

        await ValidateAccountsAsync(reset, cancellationToken);
        var targets = reset.AccountIds.ToHashSet();
        if (targets.Count > 0)
        {
            await services.GetRequiredService<SteamAuthStorageService>().ClearAccountLoginsAsync(targets, cancellationToken);
            await services.GetRequiredService<EpicAuthStorageService>().ClearAccountLoginsAsync(targets, cancellationToken);
            await services.GetRequiredService<XboxAuthStorageService>().ClearAccountLoginsAsync(targets, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            services.GetRequiredService<AccessService>().ForgetAccounts(targets);
        }

        await using var completion = await context.Database.BeginTransactionAsync(cancellationToken);
        await ValidateAccountsAsync(reset, cancellationToken);
        var removed = await context.UserAccounts.AsNoTracking()
            .Where(account => reset.AccountIds.Contains(account.Id)).Select(account => account.Id)
            .ToListAsync(cancellationToken);
        var sessions = await context.UserSessions
            .Where(session => session.AccountId.HasValue && reset.AccountIds.Contains(session.AccountId.Value))
            .ExecuteDeleteAsync(cancellationToken);
        await context.UserAccounts.Where(account => reset.AccountIds.Contains(account.Id)).ExecuteDeleteAsync(cancellationToken);
        var now = DateTime.UtcNow;
        foreach (var accountId in removed)
            context.IdentityAuditEntries.Add(new IdentityAuditEntry
            {
                Event = IdentityAuditEvent.AccountDeleted,
                TargetAccountId = accountId,
                PerformedAtUtc = now
            });
        reset.CompletedAtUtc = now;
        await context.SaveChangesAsync(cancellationToken);
        await completion.CommitAsync(cancellationToken);
        logger.LogInformation("Account reset completed: removed {Accounts} accounts and {Sessions} sessions", removed.Count, sessions);
    }

    private async Task ReleaseAsync(NpgsqlConnection connection)
    {
        Exception? failure = null;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT pg_advisory_unlock(hashtextextended(current_database() || ':' || current_schema() || ':account-reset', 0))";
            await command.ExecuteNonQueryAsync(cleanup.Token);
        }
        catch (Exception ex)
        {
            // A connection whose session lock could not be released must not reenter its pool.
            NpgsqlConnection.ClearPool(connection);
            failure = ex;
        }
        try { await context.Database.CloseConnectionAsync(); }
        catch (Exception ex) when (failure is not null)
        {
            logger.LogError(ex, "Account reset connection could not close after lock release failed");
        }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Validate(AccountReset reset)
    {
        if (reset.Id != 1 || reset.AccountIds is null || reset.StartedAtUtc == default
            || reset.PrimaryAccountId == Guid.Empty || reset.AccountIds.Contains(Guid.Empty)
            || reset.AccountIds.Distinct().Count() != reset.AccountIds.Length
            || !reset.AccountIds.SequenceEqual(reset.AccountIds.Order())
            || (reset.PrimaryAccountId is null && reset.AccountIds.Length != 0)
            || (reset.PrimaryAccountId is { } primary && reset.AccountIds.Contains(primary))
            || reset.CompletedAtUtc < reset.StartedAtUtc)
            throw new InvalidOperationException("The account reset journal is invalid. Repair its frozen account identities before restarting.");
    }

    private async Task ValidateAccountsAsync(AccountReset reset, CancellationToken cancellationToken)
    {
        if (reset.PrimaryAccountId is null) return;
        var accounts = await context.UserAccounts.AsNoTracking()
            .Where(account => account.IsMainAdmin || account.Id == reset.PrimaryAccountId || reset.AccountIds.Contains(account.Id))
            .Select(account => new UserAccount { Id = account.Id, IsMainAdmin = account.IsMainAdmin, Role = account.Role })
            .ToListAsync(cancellationToken);
        var primary = accounts.Where(account => account.IsMainAdmin).ToList();
        if (primary.Count != 1 || primary[0].Id != reset.PrimaryAccountId || primary[0].Role != SessionType.Admin
            || accounts.Any(account => reset.AccountIds.Contains(account.Id) && account.IsMainAdmin))
            throw new InvalidOperationException("The primary account changed during the pending account reset. Restore the frozen primary identity before restarting.");
    }
}
