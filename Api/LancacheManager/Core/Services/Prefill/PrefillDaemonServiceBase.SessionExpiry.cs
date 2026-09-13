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
    public async Task<PrefillSessionExpiryResult> ProcessSessionExpiryAsync(DateTime nowUtc)
    {
        var flaggedNeedsRelogin = 0;
        var terminated = 0;
        var stalledFailed = 0;
        var abandonedLoginsCancelled = 0;

        foreach (var session in _sessions.Values.Where(session => !session.Runs.IsEmpty
            && session.Status == DaemonSessionStatus.Active))
        {
            if (session.NextRecoveryAtUtc <= nowUtc)
                await RefreshRunsAsync(session.Id);
            foreach (var run in session.Runs.Values.Where(run => run.TerminalCompletedFlag == 0 && !run.CancelRequested))
            {
                var ticks = Volatile.Read(ref run.LastProgressTicksUtc);
                if (ticks == 0 || nowUtc - new DateTime(ticks, DateTimeKind.Utc) <= TimeSpan.FromSeconds(GetStallTimeoutSeconds()))
                    continue;
                try
                {
                    if (await FailStalledSessionAsync(session, run.PrefillRunId, nowUtc,
                        TimeSpan.FromSeconds(GetStallTimeoutSeconds()), "Prefill stalled: no new bytes transferred.", run.PrefillScheduleId))
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
                        await NotifyHubAsync(EventSessionUpdated, DaemonSessionDto.FromSession(session));
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
                    await TerminateSessionAsync(session.Id, "Session expired");
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

        // Stall watchdog: fail any actively-prefilling session that has transferred no new bytes for
        // longer than the configured threshold. Re-reads _sessions fresh (not the snapshot above) to
        // match prior behavior exactly, since termination above can remove entries mid-tick.
        var stallThreshold = TimeSpan.FromSeconds(GetStallTimeoutSeconds());
        var stalledSessions = _sessions.Values
            .Where(s => s.Status == DaemonSessionStatus.Active &&
                        s.Runs.IsEmpty &&
                        (!s.IsPersistent || !s.PrefillScheduleId.HasValue) &&
                        (s.PrefillState == PrefillState.Started || s.PrefillState == PrefillState.Downloading) &&
                        IsPrefillStalled(s, nowUtc, stallThreshold))
            .Select(s => (Session: s, RunId: s.PrefillRunId))
            .ToList();

        async Task ProcessStalledSessionAsync((DaemonSession Session, Guid? RunId) candidate)
        {
            var session = candidate.Session;
            try
            {
                _logger.LogWarning(
                    "Prefill stall detected for session {SessionId}: no new bytes for >{ThresholdSeconds}s. Failing the run.",
                    session.Id, stallThreshold.TotalSeconds);
                if ((!session.IsPersistent || !session.PrefillScheduleId.HasValue)
                    && await FailStalledSessionAsync(session, candidate.RunId, nowUtc, stallThreshold,
                        $"Prefill stalled: no bytes transferred for {(int)stallThreshold.TotalSeconds} seconds."))
                {
                    Interlocked.Increment(ref stalledFailed);
                }
            }
            catch (Exception ex)
            {
                // Same per-session isolation guarantee as the expiry phase above - one stalled
                // session's teardown failure must not stop the rest from being failed this tick.
                _logger.LogError(ex, "Error failing stalled session {SessionId}", session.Id);
            }
        }

        await Task.WhenAll(stalledSessions.Select(ProcessStalledSessionAsync));

        // Abandoned-login watchdog: both of a login's existing deadlines run in the browser, so a person
        // who opens the sign-in and closes the tab leaves the session sitting in LoggingIn with a card on
        // the bar that nothing ever clears. Re-reads _sessions fresh for the same reason the stall phase
        // above does: the phases before this one can remove entries mid-tick.
        var abandonedLoginTimeout = TimeSpan.FromSeconds(GetAbandonedLoginTimeoutSeconds());
        var abandonedLogins = _sessions.Values
            .Where(s => PrefillSessionExpiryGates.ShouldCancelAbandonedLogin(s, nowUtc, abandonedLoginTimeout))
            .ToList();

        async Task ProcessAbandonedLoginAsync(DaemonSession session)
        {
            try
            {
                _logger.LogInformation(
                    "Login for session {SessionId} went unanswered past its deadline. Cancelling it.",
                    session.Id);
                await CancelLoginAsync(session.Id);
                Interlocked.Increment(ref abandonedLoginsCancelled);
            }
            catch (Exception ex)
            {
                // Same per-session isolation as the two phases above. A cancel whose daemon round-trip
                // failed deliberately leaves the login resumable (see CancelLoginAsync), so the next tick
                // sees it again and tries once more.
                _logger.LogWarning(ex, "Error cancelling abandoned login for session {SessionId}", session.Id);
            }
        }

        await Task.WhenAll(abandonedLogins.Select(ProcessAbandonedLoginAsync));

        return new PrefillSessionExpiryResult(flaggedNeedsRelogin, terminated, stalledFailed, abandonedLoginsCancelled);
    }

    /// <summary>
    /// Fails a stalled prefill: best-effort tells the daemon to stop downloading (so it does not keep
    /// transferring bytes after we have given up on the run), then routes through the single
    /// idempotent terminal funnel with <see cref="PrefillState.Failed"/>. The daemon cancel is sent
    /// directly to the client rather than via <see cref="CancelPrefillAsync"/> so the terminal state
    /// stays <c>Failed</c> (a stall is a failure, not a user cancellation).
    /// </summary>
    internal async Task<bool> FailStalledSessionAsync(
        DaemonSession session, Guid? runId, DateTime nowUtc, TimeSpan stallThreshold, string reason,
        Guid? scheduleId = null)
    {
        if (runId.HasValue && session.Runs.TryGetValue(runId.Value, out var run))
        {
            await run.PrefillWork.WaitAsync();
            try
            {
                if (!IsSessionLive(session) || run.TerminalCompletedFlag != 0
                    || run.PrefillScheduleId != scheduleId || run.CancelRequested)
                    return false;
                var ticks = Volatile.Read(ref run.LastProgressTicksUtc);
                if (ticks == 0 || nowUtc - new DateTime(ticks, DateTimeKind.Utc) <= stallThreshold)
                    return false;
                await _sessionService.SetRunCancellationAsync(run.PrefillRunId, CancellationToken.None, "stalled");
                run.CancelReason = "stalled";
                run.CancelRequested = true;
            }
            finally { run.PrefillWork.Release(); }
            await CancelPrefillRunAsync(session.Id, runId.Value, reason: "stalled");
            return true;
        }
        if (session.IsPersistent)
        {
            if (!PersistentEditSessionGate.TryEnterMutation(out var mutation))
                return false;
            await using (mutation!)
            {
                return await TransitionToTerminalAsync(session, PrefillState.Failed, runId, reason,
                    "signalr.scheduledPrefill.failedStalled",
                    canClaim: () => session.Status == DaemonSessionStatus.Active
                        && session.PrefillScheduleId == scheduleId
                        && IsPrefillStalled(session, nowUtc, stallThreshold),
                    cancelDaemon: true);
            }
        }
        return await TransitionToTerminalAsync(session, PrefillState.Failed, runId, reason,
            "signalr.scheduledPrefill.failedStalled",
            canClaim: () => session.Status == DaemonSessionStatus.Active
                && session.PrefillScheduleId == scheduleId
                && IsPrefillStalled(session, nowUtc, stallThreshold),
            cancelDaemon: true);
    }

    /// <summary>
    /// Returns true when a session is actively prefilling but has transferred no new bytes
    /// for longer than <paramref name="stallThreshold"/>. Pure function — no side effects.
    /// </summary>
    internal static bool IsPrefillStalled(DaemonSession session, DateTime nowUtc, TimeSpan stallThreshold)
    {
        if (!session.IsPrefilling)
        {
            return false;
        }

        // Volatile read pairs with the Volatile.Write on the progress/terminal threads so this
        // timer thread never observes a torn or stale tick value. 0 = no prefill clock set.
        var lastProgressTicks = Volatile.Read(ref session.LastProgressTicksUtc);
        if (lastProgressTicks == 0L)
        {
            return false;
        }

        return nowUtc - new DateTime(lastProgressTicks, DateTimeKind.Utc) > stallThreshold;
    }
}
