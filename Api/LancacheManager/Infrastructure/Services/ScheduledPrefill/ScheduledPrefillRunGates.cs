using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Pure gate logic for scheduled prefill runs — extracted for unit testing. Scheduled prefill
/// reuses the long-lived persistent admin container (it never spawns a guest container), so these
/// gates decide (a) whether that container is logged in enough to reuse and (b) whether it is busy.
/// </summary>
public static class ScheduledPrefillRunGates
{
    /// <summary>
    /// Decides whether a running persistent admin container exists for the scheduler to reuse. This
    /// gate ONLY checks existence: the "is it logged in?" decision is made separately by polling the
    /// daemon's LIVE status (see <c>ScheduledPrefillService.RunServiceAsync</c>), because the
    /// in-memory <see cref="DaemonSession.AuthState"/> / <see cref="DaemonSession.NeedsRelogin"/>
    /// flags are unreliable for a persistent container that was re-adopted on a manager restart
    /// (it stays <see cref="DaemonAuthState.NotAuthenticated"/> until an interactive login or a
    /// pushed status update, neither of which fires on passive reconnect). On success returns the
    /// session id to prefill on; otherwise yields a needs-login reason for the needs-login progress path.
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

        sessionId = persistentSession.Id;
        needsLoginReason = string.Empty;
        return true;
    }

    /// <summary>
    /// Reason attached to the needs-login skip when the persistent container IS running but its
    /// account is logged out (e.g. a cancelled interactive login left the session active while the
    /// daemon logged everything out). Kept distinct from the no-container reason produced by
    /// <see cref="TryGetRunnablePersistentSession"/> so UI and logs can tell the two apart.
    /// </summary>
    public const string LoggedOutNeedsLoginReason =
        "The persistent container is running but not logged in. Log in to the persistent container before scheduling.";

    /// <summary>
    /// Top-level needs-login progress message. The two prerequisite failures are deliberately
    /// distinct so plain logs and the schedule card can tell "no running container" apart from
    /// "container running but logged out" without needing the SignalR payload's needsLoginReason.
    /// </summary>
    public static string BuildNeedsLoginMessage(PrefillPlatform serviceId, bool containerRunning) =>
        containerRunning
            ? $"Persistent container for {serviceId} is running but not logged in"
            : $"No running persistent container for {serviceId}";

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
    /// True when at least one per-service config is enabled. Used as an early-exit gate so the
    /// 1-minute poll tick can skip building the due-set (and the daemon/session/tracker work that
    /// would follow) entirely while every service is disabled — the schedule stays idle until
    /// something is (re)enabled, saving the per-minute state lookups in the meantime.
    /// </summary>
    public static bool HasAnyEnabledService(IReadOnlyList<ScheduledPrefillServiceConfigDto> services)
        => services.Any(s => s.Enabled);

    /// <summary>
    /// Pure per-service due decision for the fixed-cadence poll loop. Follows the shared interval
    /// convention: <c>-1</c> = run on startup only (due once per process, before it has run this
    /// process); <c>0</c> (or any non-<c>-1</c> value <c>&lt;= 0</c>) = paused, never due; a positive
    /// value = recurring, due when never run or once <paramref name="nowUtc"/> has reached
    /// <c>lastRun + intervalHours</c>. The caller pre-filters on the master <c>Enabled</c> flag.
    /// </summary>
    public static bool IsServiceDue(double intervalHours, DateTime? lastRunUtc, DateTime nowUtc, bool hasRunThisProcess)
    {
        if (intervalHours == -1d)
        {
            return !hasRunThisProcess;
        }

        if (intervalHours <= 0d)
        {
            return false;
        }

        if (lastRunUtc is null)
        {
            return true;
        }

        return nowUtc >= lastRunUtc.Value.AddHours(intervalHours);
    }

    /// <summary>
    /// Computes the next scheduled run time for the per-service schedule view: <c>lastRun + interval</c>
    /// for a recurring service that has run at least once; <c>null</c> when the service is paused
    /// (<c>&lt;= 0</c>, which also covers startup-only <c>-1</c>) or has never run.
    /// </summary>
    public static DateTime? ComputeNextRunUtc(double intervalHours, DateTime? lastRunUtc)
    {
        if (intervalHours <= 0d)
        {
            return null;
        }

        if (lastRunUtc is null)
        {
            return null;
        }

        return lastRunUtc.Value.AddHours(intervalHours);
    }

    /// <summary>
    /// Decides whether saving a config should anchor a service's first scheduled run to save-time
    /// (by stamping its last-run = now), so it fires one full interval later instead of on the very
    /// next poll — where <see cref="IsServiceDue"/> treats a never-run positive-interval service as
    /// due immediately. Anchors on the first-ever save AND on a disabled-&gt;enabled transition; never
    /// re-anchors a service that was already enabled, so a genuine past run is preserved and an
    /// interval change recomputes from the real last-run. Paused (<c>0</c>) and startup-only
    /// (<c>-1</c>) services are never anchored. The manual "Run Now" path stays the only instant run.
    /// </summary>
    public static bool ShouldAnchorFirstRunOnSave(bool enabled, double intervalHours, bool hasExistingLastRun, bool wasEnabledBefore)
        => enabled && intervalHours > 0d && (!hasExistingLastRun || !wasEnabledBefore);

    /// <summary>
    /// Initial-seed rule for the non-save paths that also reach <see cref="IsServiceDue"/> with a null
    /// last-run — the default config, a v1-&gt;v2 migration, a post-reset clear, and load. Anchors an
    /// enabled positive-interval service to now ONLY when it has no existing last-run key, so those paths
    /// wait one full interval instead of instant-running on the next poll. Because it requires
    /// <c>!hasExistingLastRun</c>, a normal restart (whose last-run map is persisted and reloaded, so every
    /// enabled service already has a key) is never re-anchored and its schedule never shifts. Unlike
    /// <see cref="ShouldAnchorFirstRunOnSave"/> there is deliberately NO disabled-&gt;enabled re-anchor here
    /// (that transition only exists for an explicit save): with a key already present this must return
    /// false. Paused (<c>0</c>) and startup-only (<c>-1</c>) services are never anchored.
    /// </summary>
    public static bool ShouldAnchorFirstRunOnLoad(bool enabled, double intervalHours, bool hasExistingLastRun)
        => enabled && intervalHours > 0d && !hasExistingLastRun;

    /// <summary>
    /// Computes the overall outcome of a completed scheduled-prefill pass from per-service result
    /// counts. Only a genuine failure (a service threw, failed to start, stalled, or exceeded its
    /// runtime) fails the run: a service that was merely skipped because it needs login or was busy
    /// is a prerequisite gap, not a failure, and must not make a run where another service prefilled
    /// successfully report "One or more services failed" in the <c>ScheduledPrefillCompleted</c>
    /// notification. A run where nothing ran at all still reports unsuccessful so the schedule card
    /// surfaces why the tick did no work.
    /// </summary>
    public static ScheduledPrefillRunOutcome EvaluateRunOutcome(
        int servicesRan,
        int servicesNeedingLogin,
        int servicesSkipped,
        int servicesFailed)
    {
        if (servicesFailed > 0)
        {
            return new ScheduledPrefillRunOutcome(false, "One or more services failed during the run");
        }

        if (servicesRan == 0)
        {
            return servicesNeedingLogin > 0 && servicesSkipped == 0
                ? new ScheduledPrefillRunOutcome(false, "All due services need login")
                : new ScheduledPrefillRunOutcome(false, "All enabled services were skipped");
        }

        return new ScheduledPrefillRunOutcome(true, null);
    }

    /// <summary>
    /// Maps a run's position to the percent shown on the universal notification: each due service
    /// owns an equal slice of the bar and <paramref name="currentServiceFraction"/> (games completed
    /// / games in the run, from <see cref="ComputeServiceFraction"/>) fills the active slice. Clamped
    /// to [1, 99] so the card starts at 1% instead of an instant mid-bar jump and reserves 100% for
    /// the run's terminal <c>ScheduledPrefillCompleted</c> event.
    /// </summary>
    public static double ComputeRunPercent(int servicesDone, int serviceCount, double currentServiceFraction)
    {
        if (serviceCount <= 0)
        {
            return 1d;
        }

        var fraction = Math.Clamp(currentServiceFraction, 0d, 1d);
        var percent = (servicesDone + fraction) / serviceCount * 100d;
        return Math.Clamp(percent, 1d, 99d);
    }

    /// <summary>
    /// Per-service completion fraction for <see cref="ComputeRunPercent"/>: games finished so far
    /// over the games in this run (e.g. 1 of 4 selected games = 0.25). Unknown totals yield 0 so the
    /// bar simply waits at the slice start instead of guessing.
    /// </summary>
    public static double ComputeServiceFraction(int appsCompleted, int totalApps)
    {
        if (totalApps <= 0)
        {
            return 0d;
        }

        return Math.Clamp((double)appsCompleted / totalApps, 0d, 1d);
    }
}

/// <summary>
/// How a single service's scheduled run ended, distinguishing prerequisite skips from genuine
/// failures so <see cref="ScheduledPrefillRunGates.EvaluateRunOutcome"/> can report the run
/// honestly: <see cref="Ran"/> = engaged the persistent container and finished its prefill;
/// <see cref="NeedsLogin"/> = skipped because the container is missing or logged out;
/// <see cref="Skipped"/> = skipped for a non-login reason (no daemon, busy, prefill already
/// running); <see cref="Failed"/> = engaged (or tried to) and genuinely failed.
/// </summary>
public enum ScheduledPrefillServiceRunResult
{
    Ran,
    NeedsLogin,
    Skipped,
    Failed
}

/// <summary>
/// Immutable result of evaluating a completed scheduled-prefill run: whether it succeeded overall,
/// plus an optional human-readable reason when it did not.
/// </summary>
public readonly record struct ScheduledPrefillRunOutcome(bool Success, string? Error);
