using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Core.Services;

/// <summary>
/// Result of one <see cref="PrefillDaemonServiceBase.ProcessSessionExpiryAsync"/> pass, for
/// structured logging/diagnostics at the caller (<c>PersistentSessionExpiryService</c>).
/// </summary>
public readonly record struct PrefillSessionExpiryResult(int FlaggedNeedsRelogin, int Terminated, int StalledFailed, int AbandonedLoginsCancelled);

/// <summary>
/// Result of <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync"/>. <see cref="LoggedOut"/>
/// is true when the daemon acknowledged the logout; false covers a genuine failure (a failed response
/// or the round-trip throwing), which the caller must fall back on with a stop+restart rather than
/// assuming the account was forgotten. Note this is NOT a reliable "was the account actually deleted"
/// signal: an un-updated steam/epic daemon image also reports success while only tearing down the
/// live session, not deleting the stored account file - that case is in-band indistinguishable and
/// self-resolves once the image is rebuilt.
/// </summary>
public readonly record struct PersistentLogoutResult(bool LoggedOut);

/// <summary>
/// Result of <see cref="PrefillDaemonServiceBase.ClearPersistentAuthVolumeAsync"/>.
/// </summary>
public enum PersistentVolumeClearResult
{
    /// <summary>The volume was found and removed.</summary>
    Removed,

    /// <summary>The volume did not exist - already gone, nothing to clear.</summary>
    NotFound,

    /// <summary>The volume still exists but is attached to a container and could not be removed.</summary>
    InUse,

    /// <summary>Docker is not available.</summary>
    DockerUnavailable
}

/// <summary>
/// Outcome of <see cref="PrefillDaemonServiceBase.ForgetRunningPersistentLoginAsync"/>: how a RUNNING
/// persistent container's stored login was cleared during the admin "clear stored logins" sweep.
/// </summary>
public enum PersistentRunningLoginClearOutcome
{
    /// <summary>
    /// The daemon's in-place logout was verified against its live status (no longer "logged-in"); the
    /// container is still running and merely logged out. Least disruptive - only reached on a daemon
    /// image whose logout genuinely forgets the account.
    /// </summary>
    LoggedOut,

    /// <summary>
    /// The soft logout did not verifiably forget the login (an old image reported success while its
    /// named-volume token survived, the daemon reported failure, or the live status could not be read),
    /// so the container was terminated and its named auth volume deleted - a hard, image-version-
    /// independent remove. The container ends up stopped with no stored login.
    /// </summary>
    HardRemoved,

    /// <summary>
    /// Escalation ran (container terminated) but the named auth volume could not be deleted for a real
    /// reason (still attached, or Docker unavailable), so the login may survive. The honest failure the
    /// caller must surface rather than reporting a false success.
    /// </summary>
    HardRemoveFailed
}

/// <summary>
/// Explicit outcome of one daemon login attempt (<c>PrefillDaemonServiceBase.StartLoginCoreAsync</c>).
/// The public REST surface still collapses this to "challenge or null" exactly as before, but the
/// headless self-auth coordinator must distinguish a daemon that authenticated from one that answered
/// with a challenge, failed fast, or never responded - the latter three previously shared a null
/// return, and treating a no-response as success left sessions visibly stuck LoggingIn.
/// </summary>
internal enum LoginAttemptOutcome
{
    /// <summary>The daemon reports logged-in (self-authenticated from its stored login, or already authenticated).</summary>
    Authenticated,
    /// <summary>The daemon answered with a credential challenge (carried in <see cref="LoginAttemptResult.Challenge"/>).</summary>
    ChallengeIssued,
    /// <summary>The daemon reported a login failure (fail-fast path; auth state already reset and notified).</summary>
    Failed,
    /// <summary>The daemon neither authenticated, nor challenged, nor failed within the bounded waits.</summary>
    NoResponse
}

/// <summary>
/// Result of one daemon login attempt. <see cref="Challenge"/> is non-null iff
/// <see cref="Outcome"/> is <see cref="LoginAttemptOutcome.ChallengeIssued"/>.
/// </summary>
internal sealed record LoginAttemptResult(LoginAttemptOutcome Outcome, CredentialChallenge? Challenge = null)
{
    internal static readonly LoginAttemptResult Authenticated = new(LoginAttemptOutcome.Authenticated);
    internal static readonly LoginAttemptResult LoginFailed = new(LoginAttemptOutcome.Failed);
    internal static readonly LoginAttemptResult NoResponse = new(LoginAttemptOutcome.NoResponse);

    internal static LoginAttemptResult ForChallenge(CredentialChallenge challenge)
        => new(LoginAttemptOutcome.ChallengeIssued, challenge);
}


/// <summary>
/// Secret-free description of whether a platform integration can be reused by a prefill daemon.
/// </summary>
public sealed record IntegrationLoginAvailability(
    bool Available,
    string? Account,
    string? Reason);
