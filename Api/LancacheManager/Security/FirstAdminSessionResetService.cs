using LancacheManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Security;

/// <summary>
/// Clears every session on a start that finds no account. An installation upgraded from a version
/// with no accounts still has live sessions in UserSessions, and their cookies keep working; the
/// people holding them would carry on as administrators while the installation is waiting for its
/// first account to be created. Nothing is deleted besides the sessions, because an installation
/// with no accounts has nothing else to remove. [37]
///
/// The condition is the account count alone, so it keeps holding rather than firing once: while no
/// account exists the installation is unclaimed, and no session should outlive a restart into that
/// state. The first account makes it a no-op from then on.
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
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await context.UserAccounts.AnyAsync(cancellationToken))
        {
            return;
        }

        // SessionService is scoped, and this runs before any request has created a scope of its own.
        // Program.cs:1109 resolves it the same way for the same reason.
        using var scope = _scopeFactory.CreateScope();
        var cleared = await scope.ServiceProvider.GetRequiredService<SessionService>().ClearAllSessionsAsync();

        if (cleared > 0)
        {
            _logger.LogWarning(
                "Cleared {Count} sessions because this installation has no accounts yet. Create the first administrator account to sign in again.",
                cleared);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
