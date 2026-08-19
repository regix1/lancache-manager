using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Periodically republishes every currently valid (non-revoked, non-expired) user session into the
/// unified activity registry as a full <see cref="IActivityRegistry.ReplaceAsync"/> snapshot - the same
/// "poller recomputes the full active set" pattern RustSpeedTrackerService uses for downloads.
/// SessionService's create/revoke/delete calls already report individual transitions the instant they
/// happen; this reconciler is the self-healing pass that covers what those can't reach: a session that
/// already existed in the database before this process started (e.g. one that survived a restart) never
/// gets an individual create-time report, so without this pass the registry would never learn it exists
/// and every "is this session present" consumer would treat it as permanently absent.
///
/// A plain <see cref="BackgroundService"/> on purpose, not ScheduledBackgroundService or
/// ConfigurableScheduledService: ServiceScheduleRegistry auto-registers any IHostedService of either of
/// those types as a user-facing card on the Schedules page, which is wrong for an internal reconciliation
/// detail that has nothing for a user to configure.
/// </summary>
public class UserSessionActivityReconciler : BackgroundService
{
    private static readonly TimeSpan _startupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IActivityRegistry _activityRegistry;
    private readonly ILogger<UserSessionActivityReconciler> _logger;

    public UserSessionActivityReconciler(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IActivityRegistry activityRegistry,
        ILogger<UserSessionActivityReconciler> logger)
    {
        _dbContextFactory = dbContextFactory;
        _activityRegistry = activityRegistry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileOnceAsync(_dbContextFactory, _activityRegistry, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown mid-query - not a real failure, don't log it as one.
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reconcile user session presence");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One reconciliation pass: replaces the entire UserSession/Present active set with exactly the
    /// sessions that are currently valid in the database. Extracted as an internal seam so the query and
    /// the ReplaceAsync call can be unit tested without running the BackgroundService loop.
    /// </summary>
    internal static async Task ReconcileOnceAsync(
        IDbContextFactory<AppDbContext> dbContextFactory,
        IActivityRegistry activityRegistry,
        CancellationToken stoppingToken)
    {
        var now = DateTime.UtcNow;
        using var context = dbContextFactory.CreateDbContext();
        var activeIds = await context.UserSessions
            .AsNoTracking()
            .Where(s => !s.IsRevoked && s.ExpiresAtUtc > now)
            .Select(s => s.Id)
            .ToListAsync(stoppingToken);

        var active = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in activeIds)
        {
            active[id.ToString()] = 1;
        }

        await activityRegistry.ReplaceAsync(ActivityDomains.UserSession, ActivityAspects.Present, active);
    }
}
