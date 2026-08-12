using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Infrastructure.Services.ScheduledPrefill;

/// <summary>
/// Pure gate logic for the persistent/non-persistent prefill session expiry reaper — extracted for
/// unit testing, mirroring <see cref="ScheduledPrefillRunGates"/>'s convention. Consumed by
/// <c>PrefillDaemonServiceBase.ProcessSessionExpiryAsync</c> (called once a minute, across all 5
/// platforms, by <c>PersistentSessionExpiryService</c>). "Now" is always passed in rather than read
/// from <see cref="DateTime.UtcNow"/> internally, so every branch is independently testable.
/// </summary>
public static class PrefillSessionExpiryGates
{
    /// <summary>
    /// True once <paramref name="nowUtc"/> has passed <paramref name="expiresAt"/>.
    /// </summary>
    public static bool IsExpired(DateTime expiresAt, DateTime nowUtc) => nowUtc > expiresAt;

    /// <summary>
    /// True for an active, persistent session that has passed its expiry and has not already been
    /// flagged. Persistent sessions are never torn down by the reaper (see <see cref="ShouldTerminate"/>);
    /// this is the one-time, idempotent flip that asks the admin to re-login in place. The
    /// <c>!session.NeedsRelogin</c> guard matches the prior <c>CleanupExpiredSessions</c> behavior so an
    /// already-flagged session does not keep re-firing the SignalR push on every tick.
    /// </summary>
    public static bool ShouldFlagNeedsRelogin(DaemonSession session, DateTime nowUtc) =>
        session.IsPersistent &&
        session.Status == DaemonSessionStatus.Active &&
        IsExpired(session.ExpiresAt, nowUtc) &&
        !session.NeedsRelogin;

    /// <summary>
    /// True for an active, non-persistent session that has passed its expiry — it should be
    /// terminated exactly as before. Guest/temporary sessions are never left running past expiry.
    /// </summary>
    public static bool ShouldTerminate(DaemonSession session, DateTime nowUtc) =>
        !session.IsPersistent &&
        session.Status == DaemonSessionStatus.Active &&
        IsExpired(session.ExpiresAt, nowUtc);

    /// <summary>
    /// True for an active session whose tracked login has been left unanswered past its deadline -
    /// the person opened the sign-in, never finished it, and closed the tab. Both existing login
    /// deadlines run in the browser, so nothing else ends that attempt: the session stays
    /// <see cref="DaemonAuthState.LoggingIn"/> and its card stays on the bar.
    ///
    /// The deadline is the daemon's own <see cref="CredentialChallenge.ExpiresAt"/> when it sent one,
    /// falling back to <paramref name="fallbackTimeout"/> measured from
    /// <see cref="DaemonSession.LoginStartedAtUtc"/>. <see cref="CredentialChallenge.ExpiresAt"/> is a
    /// non-nullable <see cref="DateTime"/>, so a payload with no <c>expiresAt</c> field deserializes to
    /// <c>default</c>, which is <c>0001-01-01</c> and therefore always in the past. Treating that as a
    /// real deadline would cancel every live login the first time this runs, so a default value counts
    /// as "no deadline sent" and takes the fallback instead.
    ///
    /// An untracked login (no <see cref="DaemonSession.LoginStartedAtUtc"/>) is left alone: the headless
    /// self-auth path raises no card and ends its own attempt.
    /// </summary>
    public static bool ShouldCancelAbandonedLogin(DaemonSession session, DateTime nowUtc, TimeSpan fallbackTimeout)
    {
        if (session.Status != DaemonSessionStatus.Active ||
            session.AuthState != DaemonAuthState.LoggingIn ||
            session.LoginStartedAtUtc is not { } startedAtUtc)
        {
            return false;
        }

        var challengeExpiresAt = session.PendingLoginChallenge?.ExpiresAt;
        var deadline = challengeExpiresAt is { } expiresAt && expiresAt != default
            ? expiresAt
            : startedAtUtc + fallbackTimeout;

        return IsExpired(deadline, nowUtc);
    }
}
