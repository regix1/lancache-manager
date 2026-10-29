using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
    /// <summary>
    /// Single per-tick pass over this daemon's in-memory sessions, called externally once a minute by
    /// <see cref="LancacheManager.Infrastructure.Services.PersistentSessionExpiryService"/> across all
    /// platforms (replaces the old per-instance <see cref="System.Threading.Timer"/>-driven
    /// <c>CleanupExpiredSessions</c>). Does four things, the first three preserving exact prior behavior:
    /// (1) flags expired persistent sessions <see cref="DaemonSession.NeedsRelogin"/> and pushes a
    /// SignalR update - the container is NEVER torn down; (2) terminates expired non-persistent
    /// sessions; (3) fails stalled non-persistent prefills via the existing stall watchdog; (4) cancels
    /// a login nobody came back to answer, which no other clock covers because both of a login's
    /// existing deadlines run in the browser. Termination
    /// and stall-failure are properly awaited here (no longer fire-and-forget) since this is no longer
    /// constrained by a synchronous <see cref="System.Threading.TimerCallback"/> signature. Per-session
    /// work within each phase runs concurrently via <see cref="Task.WhenAll(IEnumerable{Task})"/>, and
    /// every unit of work is individually try/catch-isolated so one session's teardown exception can
    /// never abort processing of the rest of that tick's sessions - this restores the isolation the old
    /// fire-and-forget pattern gave for free, now that these calls are properly awaited instead.
    /// </summary>
    public async Task<PrefillSessionExpiryResult> ProcessSessionExpiryAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var flaggedNeedsRelogin = 0;
        var terminated = 0;
        var stalledFailed = 0;
        var abandonedLoginsCancelled = 0;

        // A session with no runs still needs a pass while it is recovering or a pass skipped its read. The reads
        // run together because one whose daemon cannot be reached waits out the client's connect bound, and the
        // host's stop ends them so the services that stop guest containers on shutdown are not held behind them.
        var recovering = _sessions.Values.Where(session => (!session.Runs.IsEmpty || session.Recovering || session.RecoveryRequested)
            && session.Status == DaemonSessionStatus.Active).ToList();
        await Task.WhenAll(recovering.Where(session => session.NextRecoveryAtUtc <= nowUtc)
            .Select(session => RefreshRunsAsync(session.Id, cancellationToken)));
        foreach (var session in recovering)
        {
            foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0 && !run.CancelRequested))
            {
                // A scheduled run stalls on the schedule's own timeout, the one its watch uses; a run restored with
                // no watch is still ended, only not sooner than the admin allowed.
                var stallTimeout = run.PrefillScheduleId.HasValue
                    ? _stateService.GetScheduledPrefillConfig().StallTimeout
                    : TimeSpan.FromSeconds(GetStallTimeoutSeconds());
                var ticks = Volatile.Read(ref run.LastProgressTicksUtc);
                if (ticks == 0 || nowUtc - new DateTime(ticks, DateTimeKind.Utc) <= stallTimeout)
                    continue;
                try
                {
                    if (await FailStalledSessionAsync(session, run.PrefillRunId, nowUtc,
                        stallTimeout, run.PrefillScheduleId))
                        stalledFailed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not request cancellation of stalled prefill {RunId}", run.PrefillRunId);
                }
            }
        }

        var expiredSessions = _sessions.Values
            .Where(s => s.Status == DaemonSessionStatus.Active && PrefillSessionExpiryGates.IsExpired(s.ExpiresAt, nowUtc))
            .ToList();

        async Task ProcessExpiredSessionAsync(DaemonSession session)
        {
            try
            {
                if (PrefillSessionExpiryGates.ShouldFlagNeedsRelogin(session, nowUtc))
                {
                    _logger.LogInformation(
                        "Persistent session past expiry: {SessionId}. Flagging for re-login (container left running).",
                        session.Id);
                    session.NeedsRelogin = true;
                    Interlocked.Increment(ref flaggedNeedsRelogin);

                    // Close the SignalR gap: the prior Timer-based reaper flipped this flag silently (a sync
                    // TimerCallback can't cleanly await a broadcast). Mirrors NotifyAuthStateChangeAsync's
                    // "mutate, then broadcast, wrapped in try/catch" shape so a broadcast failure never masks
                    // the state mutation that already happened above.
                    try
                    {
                        await NotifyHubAsync(session, EventSessionUpdated, DaemonSessionDto.FromSession(session));
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to broadcast session update after expiry flag for session {SessionId}",
                            session.Id);
                    }
                }
                else if (PrefillSessionExpiryGates.ShouldTerminate(session, nowUtc))
                {
                    _logger.LogInformation("Session expired: {SessionId}", session.Id);
                    await TerminateSessionAsync(session.Id, "Session expired", reasonKey: SessionExpiredReasonKey);
                    Interlocked.Increment(ref terminated);
                }
            }
            catch (Exception ex)
            {
                // Per-session isolation: TerminateSessionAsync is now properly awaited (no longer
                // fire-and-forget), so without this try/catch one session's teardown exception would
                // propagate out of the Task.WhenAll below and abort processing of every other session
                // in this tick - including the stall watchdog phase that runs after this one.
                _logger.LogError(ex, "Error processing session expiry for session {SessionId}", session.Id);
            }
        }

        await Task.WhenAll(expiredSessions.Select(ProcessExpiredSessionAsync));

        // Abandoned-login watchdog: both of a login's existing deadlines run in the browser, so a person
        // who opens the sign-in and closes the tab leaves the session sitting in LoggingIn with a card on
        // the bar that nothing ever clears. Re-reads _sessions fresh for the same reason the stall phase
        // above does: the phases before this one can remove entries mid-tick.
        // The attempt is read before the gate. A new start moves the deadline before it moves the attempt, so an attempt
        // read first that passes the gate is the overdue one, and the cancel below never ends a sign-in started after it.
        var abandonedLogins = new List<(DaemonSession Session, long Attempt)>();
        foreach (var candidate in _sessions.Values)
        {
            long attempt;
            lock (candidate.PrefillLock) attempt = candidate.LoginAttempt;
            if (PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(candidate, nowUtc))
                abandonedLogins.Add((candidate, attempt));
        }

        async Task ProcessAbandonedLoginAsync((DaemonSession Session, long Attempt) abandoned)
        {
            try
            {
                _logger.LogInformation(
                    "Login for session {SessionId} went unanswered past its deadline. Cancelling it.",
                    abandoned.Session.Id);
                if (await CancelLoginAsync(abandoned.Session.Id, loginAttempt: abandoned.Attempt,
                        stopReason: "common.notifications.warnings.signInExpired"))
                {
                    Interlocked.Increment(ref abandonedLoginsCancelled);
                }
            }
            catch (Exception ex)
            {
                // Same per-session isolation as the two phases above. A cancel whose daemon round-trip
                // failed deliberately leaves the login resumable (see CancelLoginAsync), so the next tick
                // sees it again and tries once more.
                _logger.LogWarning(ex, "Error cancelling abandoned login for session {SessionId}", abandoned.Session.Id);
            }
        }

        await Task.WhenAll(abandonedLogins.Select(ProcessAbandonedLoginAsync));

        return new PrefillSessionExpiryResult(flaggedNeedsRelogin, terminated, stalledFailed, abandonedLoginsCancelled);
    }

    /// <summary>
    /// Fails a stalled run: marks it stalled, then cancels it so its terminal state stays <c>Failed</c>
    /// (a stall is a failure, not a user cancellation). Returns false when the run is gone, already
    /// ending, owned by another schedule, recovering, has moved bytes within the threshold, or the daemon
    /// did not answer the cancel; a recorded cancel the daemon did not answer is sent by the recovery pass.
    /// </summary>
    internal async Task<bool> FailStalledSessionAsync(
        DaemonSession session, Guid runId, DateTime nowUtc, TimeSpan stallThreshold, Guid? scheduleId = null)
    {
        if (!session.Runs.TryGetValue(runId, out var run))
            return false;
        await run.PrefillWork.WaitAsync();
        try
        {
            // A run whose daemon connection is down gets no progress through no fault of its own.
            if (!IsSessionLive(session) || run.TerminalCompletedFlag != 0
                || run.PrefillScheduleId != scheduleId || run.CancelRequested
                || run.Recovering || session.Recovering)
                return false;
            var ticks = Volatile.Read(ref run.LastProgressTicksUtc);
            if (ticks == 0 || nowUtc - new DateTime(ticks, DateTimeKind.Utc) <= stallThreshold)
                return false;
            await _sessionService.SetRunCancellationAsync(run.PrefillRunId, CancellationToken.None, "stalled");
            run.CancelReason = "stalled";
            run.CancelRequested = true;
        }
        finally { run.PrefillWork.Release(); }
        return await CancelPrefillRunAsync(session.Id, runId, reason: "stalled");
    }
}
