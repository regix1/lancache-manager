using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

/// <summary>
/// Clears every session on a start that finds no account. An installation upgraded from a version
/// with no accounts still has live sessions in UserSessions, and their cookies keep working; the
/// people holding them would carry on as administrators while the installation is waiting for its
/// first account to be created. Nothing is deleted besides the sessions, because an installation
/// with no accounts has nothing else to remove.
///
/// The condition is the account count alone, so it keeps holding rather than firing once: while no
/// account exists the installation is unclaimed, and no session should outlive a restart into that
/// state. The first account makes it a no-op from then on.
///
/// An installation using unauthenticated access keeps its sessions. It can remain account-less,
/// so the count might never rise and this would clear its
/// sessions on every start. The display preferences hang off the session row and cascade with it, so
/// that would reset the theme, the clock and the refresh rate every time the container restarts. When a
/// finished setup runs with sign-in off, which keeps no accounts, the start deletes every account
/// instead, as the in-app switch to that mode does.
/// </summary>
public class FirstAdminSessionResetService : IHostedService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<FirstAdminSessionResetService> _logger;

    public FirstAdminSessionResetService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IServiceScopeFactory scopeFactory,
        ILogger<FirstAdminSessionResetService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // SessionService is scoped, and this runs before any request has created a scope of its own.
        // Program.cs:1109 resolves it the same way for the same reason.
        using var scope = _scopeFactory.CreateScope();
        var sessionService = scope.ServiceProvider.GetRequiredService<SessionService>();

        if (!sessionService.IsAuthenticationEnabled())
        {
            // A finished setup with sign-in off keeps no accounts. Security__SignInMode turns sign-in off with
            // a restart and no save, and a temporary signed-in mode from that variable can leave an account
            // behind a saved unauthenticated mode, so the accounts the in-app switch deletes are deleted here.
            // A setup still in progress keeps its accounts.
            var access = scope.ServiceProvider.GetRequiredService<AccessService>();
            if (access.AllowsKeyRecovery())
            {
                // A failure here must not stop the app from starting. The accounts it leaves are
                // deleted by the next start or by the switch to unauthenticated access in the app.
                try
                {
                    if ((await access.AccountIdsAsync()).Count > 0)
                    {
                        var deleted = await access.DeleteEveryAccountAsync(null, null, scope.ServiceProvider);
                        _logger.LogWarning("Deleted {Count} accounts because sign-in is off", deleted);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not delete the accounts left while sign-in is off");
                }
            }
            return;
        }

        // A configuration change can turn sign-in on with no save in the app, so the session every browser
        // shared while it was off would otherwise stay an administrator until access setup finishes.
        await sessionService.RetireSharedAdminSessionAsync();

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.UserAccounts.AnyAsync(cancellationToken))
        {
            return;
        }

        var cleared = await sessionService.ClearAllSessionsAsync(scope.ServiceProvider);

        if (cleared > 0)
        {
            _logger.LogWarning(
                "Cleared {Count} sessions because this installation has no accounts yet. Create the first administrator account to sign in again.",
                cleared);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
