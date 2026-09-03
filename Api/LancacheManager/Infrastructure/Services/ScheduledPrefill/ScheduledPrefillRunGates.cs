using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
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
    /// Reason attached to an anonymous platform's skip when its container is running but has not
    /// reported ready. Battle.net and Riot have no sign-in, so the logged-out wording above would be
    /// telling the user to do something that does not exist for them.
    /// </summary>
    public const string ContainerNotReadyReason =
        "The persistent container is running but has not reported ready yet. This platform needs no login - wait for the container to finish starting.";

    /// <summary>
    /// No-container reason for an anonymous platform. Same situation as the reason produced by
    /// <see cref="TryGetRunnablePersistentSession"/>, minus the instruction to log in.
    /// </summary>
    public const string NoContainerReason =
        "No running persistent container. Start the persistent container before scheduling.";

    /// <summary>
    /// Top-level message for an anonymous platform whose container is running but not ready. The
    /// no-container case is worded the same for every platform, so it stays on
    /// <see cref="BuildNeedsLoginMessage"/>; only the running-but-unusable case needs to drop the
    /// login language.
    /// </summary>
    public static string BuildNotReadyMessage(PrefillPlatform serviceId) =>
        $"Persistent container for {serviceId} is not ready yet";

    /// <summary>
    /// Returns true when a scheduled run for this platform should defer. The only session that can
    /// defer it is the persistent container the run has already resolved, asked one question: is it
    /// itself prefilling, i.e. is a prior run still going? A temporary or guest container is a
    /// separate entity with its own container and its own download, so whether it is active or
    /// prefilling says nothing about whether this platform's scheduled run can proceed.
    /// Classification is by <see cref="DaemonSession.IsPersistent"/> and never by
    /// <see cref="DaemonSession.UserId"/>: a persistent container re-adopted on a manager restart
    /// falls back to an empty owner id when its Docker label is missing, which would otherwise read
    /// as somebody else's session and block the scheduler's own container forever. [13][14][15][16]
    /// </summary>
    /// <paramref name="skipStageKey"/> names the same reason as an i18n key, so the notification
    /// card can read in the operator's language; <paramref name="skipMessage"/> stays English for
    /// the server log.
    public static bool ShouldSkipForBusySessions(
        DaemonSession? persistentSession,
        out string skipMessage,
        out string skipStageKey)
    {
        if (persistentSession is { Status: DaemonSessionStatus.Active, IsPersistent: true, IsPrefilling: true })
        {
            skipMessage = "A prefill is already in progress";
            skipStageKey = "signalr.scheduledPrefill.skippedAlreadyRunning";
            return true;
        }

        skipMessage = string.Empty;
        skipStageKey = string.Empty;
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
    /// A non-null <paramref name="schedule"/> replaces that convention entirely: the service is due
    /// once an occurrence of the schedule has gone by since its last run, and the interval value is
    /// ignored (it stays on the record so clearing the schedule restores the old cadence).
    /// </summary>
    public static bool IsServiceDue(double intervalHours, DateTime? lastRunUtc, DateTime nowUtc, bool hasRunThisProcess, CustomSchedule? schedule = null)
    {
        // A custom schedule wins outright over the interval, and it is answered BEFORE the
        // null-lastRun early return below. A freshly saved custom schedule arrives here with no
        // last-run stamp yet, and that early return would treat it as due on the very next
        // one-minute poll instead of at its first real occurrence. With no stamp there is also no
        // point to measure "has an occurrence gone by since?" from, so it waits for the save-time
        // anchor rather than guessing.
        if (schedule is not null)
        {
            if (lastRunUtc is null)
            {
                return false;
            }

            var nextRun = ScheduleTiming.ComputeNextRun(schedule, lastRunUtc.Value);
            return nextRun is not null && nowUtc >= nextRun.Value;
        }

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
    /// (<c>&lt;= 0</c>, which also covers startup-only <c>-1</c>) or has never run. A non-null
    /// <paramref name="schedule"/> is answered first and returns the schedule's own next occurrence,
    /// so a service running on one still shows a next run whatever its interval value happens to be.
    /// </summary>
    public static DateTime? ComputeNextRunUtc(double intervalHours, DateTime? lastRunUtc, CustomSchedule? schedule = null)
    {
        // Answered before the interval guards below, which return null for every value <= 0 - the
        // startup-only -1 included. A service that keeps a paused or startup-only interval while it
        // runs on a custom schedule would otherwise show no next run at all. With no last run to
        // measure from, the first occurrence after now is the honest answer.
        if (schedule is not null)
        {
            return ScheduleTiming.ComputeNextRun(schedule, lastRunUtc ?? DateTime.UtcNow);
        }

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
    /// A service with a <paramref name="schedule"/> anchors whatever its interval value says: the
    /// stamp is what tells <see cref="IsServiceDue"/> that occurrences before the save do not count,
    /// and without it a custom schedule left on a paused or startup-only interval never becomes due.
    /// </summary>
    public static bool ShouldAnchorFirstRunOnSave(bool enabled, double intervalHours, bool hasExistingLastRun, bool wasEnabledBefore, CustomSchedule? schedule = null)
        => enabled && (schedule is not null || intervalHours > 0d) && (!hasExistingLastRun || !wasEnabledBefore);

    /// <summary>
    /// Initial-seed rule for the non-save paths that also reach <see cref="IsServiceDue"/> with a null
    /// last-run — the default config, a v1-&gt;v2 migration, a post-reset clear, and load. Anchors an
    /// enabled positive-interval service to now ONLY when it has no existing last-run key, so those paths
    /// wait one full interval instead of instant-running on the next poll. Because it requires
    /// <c>!hasExistingLastRun</c>, a normal restart (whose last-run map is persisted and reloaded, so every
    /// enabled service already has a key) is never re-anchored and its schedule never shifts. Unlike
    /// <see cref="ShouldAnchorFirstRunOnSave"/> there is deliberately NO disabled-&gt;enabled re-anchor here
    /// (that transition only exists for an explicit save): with a key already present this must return
    /// false. Paused (<c>0</c>) and startup-only (<c>-1</c>) services are never anchored, and a
    /// service with a <paramref name="schedule"/> is seeded on the same terms as a positive interval
    /// for the reason given on <see cref="ShouldAnchorFirstRunOnSave"/>.
    /// </summary>
    public static bool ShouldAnchorFirstRunOnLoad(bool enabled, double intervalHours, bool hasExistingLastRun, CustomSchedule? schedule = null)
        => enabled && (schedule is not null || intervalHours > 0d) && !hasExistingLastRun;

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
            return new ScheduledPrefillRunOutcome(
                false,
                "One or more services failed during the run",
                "signalr.scheduledPrefill.runSomeServicesFailed");
        }

        if (servicesRan == 0)
        {
            return servicesNeedingLogin > 0 && servicesSkipped == 0
                ? new ScheduledPrefillRunOutcome(
                    false,
                    "All due services need login",
                    "signalr.scheduledPrefill.runAllNeedLogin")
                : new ScheduledPrefillRunOutcome(
                    false,
                    "All enabled services were skipped",
                    "signalr.scheduledPrefill.runAllSkipped");
        }

        return new ScheduledPrefillRunOutcome(true, null);
    }

    /// <summary>
    /// Counts a completed run's per-service results into the four counters
    /// <see cref="EvaluateRunOutcome"/> takes, plus the stopped ones it deliberately does not.
    /// Stopping one platform says nothing about the platforms that ran beside it, so a stopped
    /// service is counted on its own and never inflates the skipped or failed totals. Extracted as a
    /// pure function because the run's whole classification hangs on it and the orchestrator it used
    /// to live inside needs a daemon, a tracker and a notification hub before it can be driven. [10]
    /// </summary>
    public static ScheduledPrefillRunTally TallyRunResults(IEnumerable<ScheduledPrefillServiceRunResult> results)
    {
        var ran = 0;
        var needingLogin = 0;
        var skipped = 0;
        var failed = 0;
        var cancelled = 0;

        foreach (var result in results)
        {
            switch (result)
            {
                case ScheduledPrefillServiceRunResult.Ran:
                    ran++;
                    break;
                case ScheduledPrefillServiceRunResult.NeedsLogin:
                    needingLogin++;
                    break;
                case ScheduledPrefillServiceRunResult.Failed:
                    failed++;
                    break;
                case ScheduledPrefillServiceRunResult.Cancelled:
                    cancelled++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        return new ScheduledPrefillRunTally(ran, needingLogin, skipped, failed, cancelled);
    }

    /// <summary>
    /// Maps this service's completion fraction (from <see cref="ComputeServiceFraction"/>) to the
    /// percent shown on its own notification card. The bar tracks the one service the card belongs
    /// to rather than slicing across every due service: needs-login and busy services skip in
    /// milliseconds, so equal run-wide slices would leave a genuine multi-gigabyte download crawling
    /// inside a fifth of the bar. Clamped to [1, 99] so the card starts at 1% instead of an instant
    /// mid-bar jump and reserves 100% for that service's terminal
    /// <c>ScheduledPrefillCompleted</c> event.
    /// </summary>
    public static double ComputeRunPercent(double serviceFraction)
        => Math.Clamp(Math.Clamp(serviceFraction, 0d, 1d) * 100d, 1d, 99d);

    /// <summary>
    /// Per-service completion fraction for <see cref="ComputeRunPercent"/>: games finished so far
    /// plus the byte-level fraction of the game currently downloading, over the games in this run.
    /// E.g. 1 of 4 games done with the second game half-downloaded = 0.375, so 4 selected games
    /// advance the bar by 25% each. Unknown totals yield 0 so the bar simply waits at the start
    /// instead of guessing.
    /// </summary>
    public static double ComputeServiceFraction(int appsCompleted, int totalApps, double currentAppFraction = 0d)
    {
        if (totalApps <= 0)
        {
            return 0d;
        }

        var fraction = (appsCompleted + Math.Clamp(currentAppFraction, 0d, 1d)) / totalApps;
        return Math.Clamp(fraction, 0d, 1d);
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
    Failed,

    /// <summary>
    /// The user stopped this service's prefill while it was running (from the prefill modal, which
    /// cancels the DAEMON session rather than this run's cancellation token). Distinct from
    /// <see cref="Ran"/>: a stopped prefill did not finish, so it must not stamp the genuine
    /// "Last run", and distinct from <see cref="Failed"/>: nothing went wrong.
    /// </summary>
    Cancelled
}

/// <summary>
/// Immutable result of evaluating a completed scheduled-prefill run: whether it succeeded overall,
/// plus an optional human-readable reason when it did not, and the i18n key naming that same reason
/// so the notification card can show it in the reader's language.
/// </summary>
public readonly record struct ScheduledPrefillRunOutcome(bool Success, string? Error, string? StageKey = null);

/// <summary>
/// How many due services ended each way, produced by
/// <see cref="ScheduledPrefillRunGates.TallyRunResults"/>.
/// </summary>
public readonly record struct ScheduledPrefillRunTally(
    int Ran,
    int NeedingLogin,
    int Skipped,
    int Failed,
    int Cancelled)
{
    /// <summary>
    /// True when the run as a whole should report as stopped rather than as a success or a failure.
    /// One platform being stopped must not bury four siblings that prefilled to completion, so the
    /// run only claims to have been stopped when nothing reached
    /// <see cref="ScheduledPrefillServiceRunResult.Ran"/>. [8]
    /// </summary>
    public bool ReportsCancelled => Cancelled > 0 && Ran == 0;
}
