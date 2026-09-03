using LancacheManager.Controllers;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Infrastructure.Services.ScheduledPrefill;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Tests;

public class ScheduledPrefillRunGatesTests
{
    private static readonly Guid SystemUserId = ScheduledPrefillConstants.DeriveSystemUserId();
    private static readonly Guid RealUserId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenOnlyPersistentSystemSessionAuthenticated()
    {
        var session = MakeSession(SystemUserId, isPersistent: true);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(session, out var message, out var stageKey);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
        Assert.Equal(string.Empty, stageKey);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenRealUserSessionActive()
    {
        // INVERTED on purpose. This used to skip on any session owned by somebody other than the
        // scheduler, and that is the behavior being removed: a manual or guest container is a
        // separate entity with its own container, so it must never hold up a scheduled run. The gate
        // is also no longer handed a session list, so a manual session can only reach it by being
        // passed as the persistent session, which is what this asserts is ignored.
        var session = MakeSession(RealUserId, isPersistent: false);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(session, out var message, out var stageKey);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
        Assert.Equal(string.Empty, stageKey);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenANonPersistentSessionIsPrefilling()
    {
        // The user's rule, stated as a test: a temporary or guest container actively downloading for
        // somebody else says nothing about whether this platform's scheduled run can proceed.
        var session = MakeSession(RealUserId, isPersistent: false, isPrefilling: true);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(session, out var message, out var stageKey);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
        Assert.Equal(string.Empty, stageKey);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsTrue_WhenThePersistentSessionIsPrefilling()
    {
        var session = MakeSession(SystemUserId, isPersistent: true, isPrefilling: true);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(session, out var message, out var stageKey);

        Assert.True(shouldSkip);
        Assert.Equal("A prefill is already in progress", message);
        Assert.Equal("signalr.scheduledPrefill.skippedAlreadyRunning", stageKey);
    }

    [Fact]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenThereIsNoPersistentSession()
    {
        // The gate now takes the ONE session the run already resolved, so "no session list" and
        // "every session was terminated" both arrive here as a null.
        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(null, out var message, out var stageKey);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
        Assert.Equal(string.Empty, stageKey);
    }

    [Theory]
    [InlineData(DaemonSessionStatus.Terminated)]
    [InlineData(DaemonSessionStatus.Error)]
    public void ShouldSkipForBusySessions_ReturnsFalse_WhenThePersistentSessionIsNotActive(DaemonSessionStatus status)
    {
        // Replaces the old terminated-sessions and error-sessions cases: with a single session in
        // hand rather than a list, "not active" is the whole of what those two used to say. A
        // terminated container's stale prefilling flag must not defer the run forever.
        var session = MakeSession(SystemUserId, isPersistent: true, isPrefilling: true, status: status);

        var shouldSkip = ScheduledPrefillRunGates.ShouldSkipForBusySessions(session, out var message, out var stageKey);

        Assert.False(shouldSkip);
        Assert.Equal(string.Empty, message);
        Assert.Equal(string.Empty, stageKey);
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsTrue_AndSessionId_WhenPersistentSessionExists()
    {
        // Reuse contract: a running persistent container is prefilled on its OWN session id. The
        // scheduler never creates a new (guest) session for this.
        var session = MakeSession(SystemUserId, isPersistent: true, authState: DaemonAuthState.Authenticated);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.True(runnable);
        Assert.Equal(session.Id, sessionId);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsFalse_WithReason_WhenNoPersistentSession()
    {
        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(null, out var sessionId, out var reason);

        Assert.False(runnable);
        Assert.Equal(string.Empty, sessionId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    // The two removed tests (_WhenSessionNotAuthenticated / _WhenSessionNeedsRelogin) asserted the
    // OLD contract where the gate rejected on the in-memory AuthState / NeedsRelogin flags. Those
    // flags are unreliable for a persistent container re-adopted on a manager restart (it stays
    // NotAuthenticated until interactive login), which is exactly the bug being fixed. The gate no
    // longer inspects them — the "is logged in" decision now comes from the daemon's LIVE status in
    // ScheduledPrefillService.RunServiceAsync (status?.Status == "logged-in"). That live poll requires
    // a daemon client seam and is not exercisable through this pure gate. The two tests below prove
    // the gate now ignores the stale in-memory auth flags and returns the session id regardless.

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsTrue_WhenInMemoryAuthStateNotAuthenticated()
    {
        // The core of the fix: a persistent container re-adopted on restart carries a stale
        // NotAuthenticated in-memory AuthState. The gate must NOT skip on that anymore; the live
        // daemon status (checked downstream) is the source of truth.
        var session = MakeSession(SystemUserId, isPersistent: true, authState: DaemonAuthState.NotAuthenticated);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.True(runnable);
        Assert.Equal(session.Id, sessionId);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void TryGetRunnablePersistentSession_ReturnsTrue_WhenInMemoryNeedsRelogin()
    {
        var session = MakeSession(
            SystemUserId,
            isPersistent: true,
            authState: DaemonAuthState.Authenticated,
            needsRelogin: true);

        var runnable = ScheduledPrefillRunGates.TryGetRunnablePersistentSession(session, out var sessionId, out var reason);

        Assert.True(runnable);
        Assert.Equal(session.Id, sessionId);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void BuildNeedsLoginMessage_DistinguishesStoppedContainerFromLoggedOutContainer()
    {
        // Both prerequisite failures reach the same needs-login stage, but the outward message
        // must say WHICH prerequisite failed: a stopped container versus a running container whose
        // account was logged out (e.g. after a cancelled interactive login). The old shared
        // "No logged-in persistent container" wording made the two indistinguishable in plain logs.
        var stopped = ScheduledPrefillRunGates.BuildNeedsLoginMessage(PrefillPlatform.Steam, containerRunning: false);
        var loggedOut = ScheduledPrefillRunGates.BuildNeedsLoginMessage(PrefillPlatform.Steam, containerRunning: true);

        Assert.NotEqual(stopped, loggedOut);
        Assert.Contains("Steam", stopped);
        Assert.Contains("Steam", loggedOut);
        Assert.Contains("No running persistent container", stopped);
        Assert.Contains("not logged in", loggedOut);
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam, true)]
    [InlineData(PrefillPlatform.Epic, true)]
    [InlineData(PrefillPlatform.Xbox, true)]
    [InlineData(PrefillPlatform.BattleNet, false)]
    [InlineData(PrefillPlatform.Riot, false)]
    public void RequiresLogin_IsFalseOnlyForTheAnonymousPlatforms(PrefillPlatform platform, bool expected)
    {
        // Battle.net and Riot pull public CDN content with no account, which is why their daemons
        // override InitialAuthState to Authenticated. Everything that gates on login state reads this.
        Assert.Equal(expected, platform.RequiresLogin());
    }

    [Fact]
    public void BuildNotReadyMessage_DropsTheLoginLanguageForAnonymousPlatforms()
    {
        // A running-but-unready container is the same situation for every platform, but Battle.net
        // and Riot have nothing to sign in to - telling them to log in sends the user looking for a
        // screen that does not exist.
        var anonymous = ScheduledPrefillRunGates.BuildNotReadyMessage(PrefillPlatform.BattleNet);

        Assert.Contains("BattleNet", anonymous);
        Assert.DoesNotContain("logged in", anonymous);
        Assert.DoesNotContain("log in", anonymous);
        Assert.DoesNotContain("login", anonymous, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("login", ScheduledPrefillRunGates.ContainerNotReadyReason.Replace(
            "needs no login", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log in", ScheduledPrefillRunGates.NoContainerReason);
    }

    [Fact]
    public void EvaluateRunOutcome_AnonymousSkipDoesNotReportNeedingLogin()
    {
        // An anonymous platform routes its prerequisite failure to Skipped, not NeedsLogin, so a run
        // where only Battle.net was due cannot summarise itself as "All due services need login".
        var outcome = ScheduledPrefillRunGates.EvaluateRunOutcome(
            servicesRan: 0, servicesNeedingLogin: 0, servicesSkipped: 1, servicesFailed: 0);

        Assert.False(outcome.Success);
        Assert.Equal("All enabled services were skipped", outcome.Error);
    }

    // ---- Run classification (TallyRunResults) ----
    // The reported bug: services now run side by side, and one stopped service reported the WHOLE run
    // as stopped, burying the siblings that prefilled to completion.

    [Fact]
    public void TallyRunResults_ARanRanCancelledRun_DoesNotReportTheRunStopped()
    {
        var tally = ScheduledPrefillRunGates.TallyRunResults([
            ScheduledPrefillServiceRunResult.Ran,
            ScheduledPrefillServiceRunResult.Ran,
            ScheduledPrefillServiceRunResult.Cancelled
        ]);

        Assert.Equal(2, tally.Ran);
        Assert.Equal(1, tally.Cancelled);
        Assert.False(tally.ReportsCancelled);

        // A stopped service must not count as skipped or failed either, or the run summary would
        // claim a failure the user caused on purpose.
        Assert.Equal(0, tally.Skipped);
        Assert.Equal(0, tally.Failed);
        Assert.True(ScheduledPrefillRunGates.EvaluateRunOutcome(
            tally.Ran, tally.NeedingLogin, tally.Skipped, tally.Failed).Success);
    }

    [Fact]
    public void TallyRunResults_ReportsTheRunStopped_OnlyWhenNothingRan()
    {
        var nothingRan = ScheduledPrefillRunGates.TallyRunResults([
            ScheduledPrefillServiceRunResult.Skipped,
            ScheduledPrefillServiceRunResult.Cancelled
        ]);

        Assert.True(nothingRan.ReportsCancelled);
    }

    [Fact]
    public void TallyRunResults_CountsEveryOtherResultIntoItsOwnBucket()
    {
        var tally = ScheduledPrefillRunGates.TallyRunResults([
            ScheduledPrefillServiceRunResult.Ran,
            ScheduledPrefillServiceRunResult.NeedsLogin,
            ScheduledPrefillServiceRunResult.Skipped,
            ScheduledPrefillServiceRunResult.Failed,
            ScheduledPrefillServiceRunResult.Cancelled
        ]);

        Assert.Equal(new ScheduledPrefillRunTally(1, 1, 1, 1, 1), tally);
    }

    [Fact]
    public void TallyRunResults_AnEmptyRunReportsNothing()
    {
        var tally = ScheduledPrefillRunGates.TallyRunResults([]);

        Assert.Equal(default, tally);
        Assert.False(tally.ReportsCancelled);
    }

    [Fact]
    public void LoggedOutNeedsLoginReason_DiffersFromNoContainerReason()
    {
        // The detailed needsLoginReason carried on the progress event must also stay distinct
        // between the no-container gate and the live logged-out check.
        ScheduledPrefillRunGates.TryGetRunnablePersistentSession(null, out _, out var noContainerReason);

        Assert.NotEqual(ScheduledPrefillRunGates.LoggedOutNeedsLoginReason, noContainerReason);
        Assert.False(string.IsNullOrWhiteSpace(ScheduledPrefillRunGates.LoggedOutNeedsLoginReason));
    }

    [Fact]
    public void GuestPrefillValidation_AcceptsDurationHoursOneThroughThree()
    {
        foreach (var hours in new[] { 1, 2, 3 })
        {
            var isValid = GuestPrefillValidation.TryValidateDurationHours(hours, out var error);

            Assert.True(isValid);
            Assert.Equal(string.Empty, error);
        }
    }

    [Fact]
    public void GuestPrefillValidation_RejectsDurationHoursOutsideOneThroughThree()
    {
        foreach (var hours in new[] { 0, 4, -1 })
        {
            var isValid = GuestPrefillValidation.TryValidateDurationHours(hours, out var error);

            Assert.False(isValid);
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    // ---- Idle-schedule gate (HasAnyEnabledService) ----

    [Fact]
    public void HasAnyEnabledService_ReturnsFalse_WhenAllServicesDisabled()
    {
        var services = new[]
        {
            MakeServiceConfig(PrefillPlatform.Steam, enabled: false),
            MakeServiceConfig(PrefillPlatform.Epic, enabled: false),
            MakeServiceConfig(PrefillPlatform.BattleNet, enabled: false)
        };

        Assert.False(ScheduledPrefillRunGates.HasAnyEnabledService(services));
    }

    [Fact]
    public void HasAnyEnabledService_ReturnsTrue_WhenAnyServiceEnabled()
    {
        var services = new[]
        {
            MakeServiceConfig(PrefillPlatform.Steam, enabled: false),
            MakeServiceConfig(PrefillPlatform.Epic, enabled: false),
            MakeServiceConfig(PrefillPlatform.BattleNet, enabled: true)
        };

        Assert.True(ScheduledPrefillRunGates.HasAnyEnabledService(services));
    }

    private static ScheduledPrefillServiceConfigDto MakeServiceConfig(PrefillPlatform serviceId, bool enabled)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = serviceId,
            Enabled = enabled,
            Preset = ScheduledPrefillPreset.All,
            OperatingSystems = new List<ScheduledPrefillOperatingSystem>(),
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto
            {
                Mode = ScheduledPrefillMaxConcurrencyMode.Auto
            }
        };
    }

    // ---- Per-service due-check (IsServiceDue) ----

    [Fact]
    public void IsServiceDue_Paused_IsNeverDue()
    {
        // 0 = paused: never due, regardless of last-run or elapsed time.
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(0d, lastRunUtc: null, nowUtc: DateTime.UtcNow, hasRunThisProcess: false));
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(0d, lastRunUtc: DateTime.UtcNow.AddDays(-30), nowUtc: DateTime.UtcNow, hasRunThisProcess: false));
    }

    [Fact]
    public void IsServiceDue_StartupOnly_DueOncePerProcess()
    {
        // -1 = run on startup only: due before it has run this process, never after.
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(-1d, lastRunUtc: null, nowUtc: DateTime.UtcNow, hasRunThisProcess: false));
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(-1d, lastRunUtc: null, nowUtc: DateTime.UtcNow, hasRunThisProcess: true));
        // A persisted last-run from a PRIOR process must NOT suppress the startup run this process.
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(-1d, lastRunUtc: DateTime.UtcNow.AddHours(-1), nowUtc: DateTime.UtcNow, hasRunThisProcess: false));
    }

    [Fact]
    public void IsServiceDue_NeverRunRecurring_IsDue()
    {
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(24d, lastRunUtc: null, nowUtc: DateTime.UtcNow, hasRunThisProcess: false));
    }

    [Fact]
    public void IsServiceDue_Overdue_IsDue()
    {
        var now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var lastRun = now.AddHours(-25); // interval 24h elapsed
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(24d, lastRun, now, hasRunThisProcess: true));
    }

    [Fact]
    public void IsServiceDue_NotYetElapsed_IsNotDue()
    {
        var now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var lastRun = now.AddHours(-1); // only 1h of a 24h interval elapsed
        Assert.False(ScheduledPrefillRunGates.IsServiceDue(24d, lastRun, now, hasRunThisProcess: true));
    }

    [Fact]
    public void IsServiceDue_ExactlyAtBoundary_IsDue()
    {
        var now = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var lastRun = now.AddHours(-24); // now == lastRun + interval
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(24d, lastRun, now, hasRunThisProcess: true));
    }

    // ---- Next-run computation (ComputeNextRunUtc) ----

    [Fact]
    public void ComputeNextRunUtc_RecurringWithLastRun_IsLastRunPlusInterval()
    {
        var lastRun = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(lastRun.AddHours(24), ScheduledPrefillRunGates.ComputeNextRunUtc(24d, lastRun));
    }

    [Fact]
    public void ComputeNextRunUtc_NeverRunOrPausedOrStartupOnly_IsNull()
    {
        Assert.Null(ScheduledPrefillRunGates.ComputeNextRunUtc(24d, lastRunUtc: null)); // never run
        Assert.Null(ScheduledPrefillRunGates.ComputeNextRunUtc(0d, lastRunUtc: DateTime.UtcNow)); // paused
        Assert.Null(ScheduledPrefillRunGates.ComputeNextRunUtc(-1d, lastRunUtc: DateTime.UtcNow)); // startup-only
    }

    // ---- First-run anchoring on save (ShouldAnchorFirstRunOnSave) ----
    // The reported bug: saving a config never anchored a per-service last-run, so IsServiceDue saw a
    // null last-run for a positive interval and ran the service on the very next 1-minute poll. The
    // save path now anchors last-run = save-time for the services this predicate selects.

    [Theory]
    [InlineData(true, 48d, false, false, true)]   // first-ever save of an enabled service
    [InlineData(true, 48d, false, true, true)]    // enabled but never anchored yet (defensive)
    [InlineData(true, 48d, true, false, true)]    // re-enable after a disable -> re-anchor from now
    [InlineData(true, 48d, true, true, false)]    // already enabled + has a real run -> don't clobber
    [InlineData(true, 0d, false, false, false)]   // paused (0) is never anchored
    [InlineData(true, -1d, false, false, false)]  // startup-only (-1) is never anchored
    [InlineData(false, 48d, false, false, false)] // disabled service is never anchored
    public void ShouldAnchorFirstRunOnSave_MatchesTruthTable(
        bool enabled,
        double intervalHours,
        bool hasExistingLastRun,
        bool wasEnabledBefore,
        bool expected)
    {
        var shouldAnchor = ScheduledPrefillRunGates.ShouldAnchorFirstRunOnSave(
            enabled, intervalHours, hasExistingLastRun, wasEnabledBefore);

        Assert.Equal(expected, shouldAnchor);
    }

    [Fact]
    public void IsServiceDue_AnchoredOnSave_IsNotDueUntilOneIntervalLater()
    {
        // After the save-time anchor (last-run = save time) a freshly-enabled 48h service must NOT be
        // due on the next poll; it becomes due exactly one interval later. Contrast with
        // IsServiceDue_NeverRunRecurring_IsDue above, which is the un-anchored (buggy) instant-run path.
        var savedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(ScheduledPrefillRunGates.IsServiceDue(48d, savedAt, savedAt.AddMinutes(1), hasRunThisProcess: false));
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(48d, savedAt, savedAt.AddHours(48), hasRunThisProcess: false));
    }

    // ---- First-run anchoring on the non-save paths (ShouldAnchorFirstRunOnLoad) ----
    // Default config / v1->v2 migration / post-reset / load all reach IsServiceDue with a null last-run
    // too, so they need anchoring as well — but ONLY for services with no existing last-run key. The
    // (true, 24, true) => false row is the restart-no-shift invariant: a persisted+reloaded key must never
    // be re-anchored, or every restart would push the schedule out one interval.

    [Theory]
    [InlineData(true, 24d, false, true)]    // enabled, recurring, no key -> seed (default / migrated / reset / fresh)
    [InlineData(true, 24d, true, false)]    // enabled, recurring, key ALREADY present -> NOT re-seeded (restart-no-shift)
    [InlineData(true, 0d, false, false)]    // paused (0) is never anchored
    [InlineData(true, -1d, false, false)]   // startup-only (-1) is never anchored
    [InlineData(false, 24d, false, false)]  // disabled service is never anchored
    public void ShouldAnchorFirstRunOnLoad_MatchesInitialSeedTruthTable(
        bool enabled,
        double intervalHours,
        bool hasExistingLastRun,
        bool expected)
    {
        var shouldAnchor = ScheduledPrefillRunGates.ShouldAnchorFirstRunOnLoad(
            enabled, intervalHours, hasExistingLastRun);

        Assert.Equal(expected, shouldAnchor);
    }

    // ---- Custom schedules on both gates ----
    // A custom schedule wins over IntervalHours in both gates, and both answer it BEFORE their
    // interval handling. IsServiceDue's null-lastRun early return would otherwise fire a freshly
    // saved schedule on the next 1-minute poll, and ComputeNextRunUtc's <= 0 guard (which swallows
    // the -1 startup-only sentinel as well as 0) would leave it with no next run to show at all.

    [Fact]
    public void IsServiceDue_CustomScheduleNeverRun_IsNotDue()
    {
        // 09:00 against a daily-02:00 schedule with no anchor yet. The interval path answers true
        // here (a never-run recurring service is due immediately), which would run it on the next poll.
        var now = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc);

        Assert.False(ScheduledPrefillRunGates.IsServiceDue(
            24d, lastRunUtc: null, nowUtc: now, hasRunThisProcess: false, schedule: MakeSchedule("0 2 * * *")));
    }

    [Fact]
    public void IsServiceDue_CustomScheduleOccurrenceGoneBy_IsDue()
    {
        var lastRun = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);
        var schedule = MakeSchedule("0 2 * * *");

        Assert.False(ScheduledPrefillRunGates.IsServiceDue(
            24d, lastRun, new DateTime(2026, 1, 2, 1, 59, 0, DateTimeKind.Utc), hasRunThisProcess: false, schedule));
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(
            24d, lastRun, new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc), hasRunThisProcess: false, schedule));
    }

    [Fact]
    public void IsServiceDue_CustomScheduleOnAPausedOrStartupOnlyInterval_StillFollowsTheSchedule()
    {
        // A service keeps whichever interval it had when the custom schedule was set, so neither the
        // 0 (paused) nor the -1 (startup-only) sentinel may decide the answer.
        var lastRun = new DateTime(2026, 1, 1, 2, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc);
        var schedule = MakeSchedule("0 2 * * *");

        Assert.True(ScheduledPrefillRunGates.IsServiceDue(0d, lastRun, now, hasRunThisProcess: false, schedule));
        Assert.True(ScheduledPrefillRunGates.IsServiceDue(-1d, lastRun, now, hasRunThisProcess: true, schedule));
    }

    [Fact]
    public void ComputeNextRunUtc_CustomSchedule_BeatsTheInterval()
    {
        // The 1h interval from a 09:00 last run says 10:00; the daily-02:00 schedule says tomorrow 02:00.
        var lastRun = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        var nextRun = ScheduledPrefillRunGates.ComputeNextRunUtc(1d, lastRun, MakeSchedule("0 2 * * *"));

        Assert.Equal(new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc), nextRun);
    }

    [Fact]
    public void ComputeNextRunUtc_CustomScheduleOnAPausedOrStartupOnlyInterval_IsNotNull()
    {
        var lastRun = new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);
        var schedule = MakeSchedule("0 2 * * *");

        Assert.NotNull(ScheduledPrefillRunGates.ComputeNextRunUtc(0d, lastRun, schedule));      // paused
        Assert.NotNull(ScheduledPrefillRunGates.ComputeNextRunUtc(-1d, lastRun, schedule));     // startup-only
    }

    [Fact]
    public void ShouldAnchorFirstRunOnSave_CustomScheduleOnANonPositiveInterval_Anchors()
    {
        // Without the anchor the schedule has no last-run to measure from, IsServiceDue answers false
        // for a null last-run, and a custom schedule left on a paused interval would never run at all.
        Assert.True(ScheduledPrefillRunGates.ShouldAnchorFirstRunOnSave(
            enabled: true,
            intervalHours: 0d,
            hasExistingLastRun: false,
            wasEnabledBefore: false,
            schedule: MakeSchedule("0 2 * * *")));
    }

    [Fact]
    public void ShouldAnchorFirstRunOnLoad_CustomScheduleOnANonPositiveInterval_Seeds()
    {
        Assert.True(ScheduledPrefillRunGates.ShouldAnchorFirstRunOnLoad(
            enabled: true,
            intervalHours: -1d,
            hasExistingLastRun: false,
            schedule: MakeSchedule("0 2 * * *")));
        // The restart-no-shift invariant holds for a custom schedule too: a key already present is
        // never re-seeded.
        Assert.False(ScheduledPrefillRunGates.ShouldAnchorFirstRunOnLoad(
            enabled: true,
            intervalHours: -1d,
            hasExistingLastRun: true,
            schedule: MakeSchedule("0 2 * * *")));
    }

    private static CustomSchedule MakeSchedule(
        string expression,
        string timeZoneId = "UTC",
        TimeOnly? windowStart = null,
        TimeOnly? windowEnd = null)
    {
        return new CustomSchedule
        {
            Expression = expression,
            TimeZoneId = timeZoneId,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
    }

    private static DaemonSession MakeSession(
        Guid userId,
        bool isPersistent,
        DaemonSessionStatus status = DaemonSessionStatus.Active,
        bool isPrefilling = false,
        DaemonAuthState authState = DaemonAuthState.Authenticated,
        bool needsRelogin = false)
    {
        return new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = userId,
            Status = status,
            IsPersistent = isPersistent,
            IsPrefilling = isPrefilling,
            AuthState = authState,
            NeedsRelogin = needsRelogin,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
    }
}
