using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Pure gate logic for scheduled prefill runs — extracted for unit testing. Scheduled prefill
/// reuses the long-lived persistent admin container (it never spawns a guest container), so these
/// gates decide (a) whether that container is logged in enough to reuse and (b) whether it is busy.
/// </summary>
public static class ScheduledPrefillRunGates
{
    /// <summary>
    /// Decides whether the running persistent admin container is healthy enough for the scheduler
    /// to reuse: it must exist, be authenticated, and not be flagged for re-login (the container
    /// authenticates itself from its named auth volume — the manager never injects a token). On
    /// success returns the session id to prefill on; otherwise yields a needs-login reason for the
    /// existing needs-login progress path.
    /// </summary>
    public static bool TryGetRunnablePersistentSession(
        DaemonSession? persistentSession,
        out string sessionId,
        out string needsLoginReason)
    {
        if (persistentSession is null)
        {
            sessionId = string.Empty;
            needsLoginReason = "No running persistent container. Start and log in the persistent container before scheduling.";
            return false;
        }

        if (persistentSession.AuthState != DaemonAuthState.Authenticated || persistentSession.NeedsRelogin)
        {
            sessionId = string.Empty;
            needsLoginReason = "The persistent container is not logged in. Log in to the persistent container before scheduling.";
            return false;
        }

        sessionId = persistentSession.Id;
        needsLoginReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns true when a scheduled run for this daemon should defer. The persistent reuse target
    /// and every other scheduler-owned session share <paramref name="systemUserId"/>, so an idle
    /// authenticated persistent container does NOT block (Cause 2 / 19): only a genuinely-conflicting
    /// non-system interactive (guest/manual) session, or a persistent container already mid-prefill
    /// (a prior run still going), causes a skip.
    /// </summary>
    public static bool ShouldSkipForBusySessions(
        IEnumerable<DaemonSession> sessions,
        Guid systemUserId,
        out string skipMessage)
    {
        var activeSessions = sessions
            .Where(s => s.Status == DaemonSessionStatus.Active)
            .ToList();

        if (activeSessions.Any(s => s.UserId != systemUserId))
        {
            skipMessage = "A manual prefill session is active";
            return true;
        }

        if (activeSessions.Any(s => s.IsPrefilling))
        {
            skipMessage = "A prefill is already in progress";
            return true;
        }

        skipMessage = string.Empty;
        return false;
    }

    /// <summary>
    /// Computes the overall outcome of a completed scheduled-prefill pass. The run is only reported
    /// successful when at least one service actually engaged its persistent container AND no service
    /// threw, skipped, or failed to engage. This stops a partial failure (one service erroring or
    /// skipping) from masquerading as a fully successful run in the <c>ScheduledPrefillCompleted</c>
    /// notification.
    /// </summary>
    public static ScheduledPrefillRunOutcome EvaluateRunOutcome(int servicesAttempted, bool anyServiceFailed)
    {
        if (servicesAttempted == 0)
        {
            return new ScheduledPrefillRunOutcome(false, "All enabled services were skipped");
        }

        if (anyServiceFailed)
        {
            return new ScheduledPrefillRunOutcome(false, "One or more services failed during the run");
        }

        return new ScheduledPrefillRunOutcome(true, null);
    }
}

/// <summary>
/// Immutable result of evaluating a completed scheduled-prefill run: whether it succeeded overall,
/// plus an optional human-readable reason when it did not.
/// </summary>
public readonly record struct ScheduledPrefillRunOutcome(bool Success, string? Error);
