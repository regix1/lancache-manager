using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Represents a Steam Prefill daemon session
/// </summary>
public class DaemonSession
{
    public System.Collections.Concurrent.ConcurrentDictionary<Guid, DaemonRun> Runs { get; } = new();
    public DaemonStatus? Capabilities { get; set; }
    private bool _recovering;

    /// <summary>
    /// A page can read a change written here before any session update carries it, so a change clears
    /// <see cref="LastRecoveryUpdate"/> and the next recovery pass sends its update. Written under
    /// <see cref="PrefillLock"/>, so a change cannot fall between a session update's check and its record.
    /// </summary>
    public bool Recovering
    {
        get => _recovering;
        set
        {
            lock (PrefillLock)
            {
                if (_recovering == value) return;
                _recovering = value;
                RecoveringChanges++;
                LastRecoveryUpdate = null;
            }
        }
    }

    /// <summary>
    /// How many times <see cref="Recovering"/> changed, under <see cref="PrefillLock"/>. A session update is recorded
    /// as every page's state only when this did not move while it was sent: a page that opened in between may hold
    /// the other value even when the flag changed back.
    /// </summary>
    public long RecoveringChanges { get; private set; }
    public bool AdmissionClosed { get; set; }

    /// <summary>
    /// A stop of this session is in progress. <see cref="Status"/> reads Error meanwhile so every guard refuses
    /// new work; the session's wire status reads Terminated instead.
    /// </summary>
    public bool IsStopping { get; set; }

    /// <summary>
    /// The daemon did not acknowledge the cancel that cleaned up a failed sign-in, so it may still be in that
    /// sign-in; the next interactive login cancels it first.
    /// </summary>
    public bool LoginCancelUnconfirmed { get; set; }
    public bool LoginSettled { get; set; } = true;
    public bool PreserveLoginExpiry { get; set; }
    public SemaphoreSlim RecoveryWork { get; } = new(1, 1);
    public DateTime NextRecoveryAtUtc { get; set; }

    /// <summary>
    /// A recovery pass found <see cref="RecoveryWork"/> taken and skipped a daemon read that may be newer than the
    /// holder's, so another pass with a fresh read follows. Cleared by the pass that reads next.
    /// </summary>
    public bool RecoveryRequested { get; set; }
    public string? LastRecoveryUpdate { get; set; }

    /// <summary>Sends one session update at a time, so each page receives them in the order they are recorded.</summary>
    public SemaphoreSlim SessionUpdateSend { get; } = new(1, 1);
    public string Id { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string ConfiguredImage { get; set; } = string.Empty;
    public string ImageId { get; set; } = string.Empty;
    public string CommandsDir { get; init; } = string.Empty;
    public string ResponsesDir { get; init; } = string.Empty;
    public DaemonSessionStatus Status { get; set; } = DaemonSessionStatus.Active;
    public string? ErrorMessage { get; set; }
    public DaemonAuthState AuthState { get; set; } = DaemonAuthState.NotAuthenticated;

    /// <summary>
    /// The daemon's real "Login failed: &lt;reason&gt;" text from the most recent
    /// <see cref="PrefillDaemonServiceBase.StartLoginAsync(string, TimeSpan?, CancellationToken)"/> attempt,
    /// captured via the fail-fast <c>OnStatusUpdate</c> race. Null when the last attempt had no observed
    /// failure (success, still pending, or never attempted). Reset to null at the start of every attempt
    /// so stale text from a previous failure never leaks into a later one. Transient - not persisted, not
    /// part of <see cref="DaemonSessionDto"/> (only the persistent-login endpoint reads it directly).
    /// </summary>
    public string? LastLoginFailureMessage { get; set; }

    /// <summary>
    /// The reason key of a sign-in failure that was not the daemon refusing the details (a saved login that timed out,
    /// a daemon connection that dropped). Null for a refusal, which reads as one.
    /// </summary>
    public string? LastLoginFailureKey { get; set; }

    /// <summary>
    /// The credential challenge most recently handed back to a caller of
    /// <see cref="PrefillDaemonServiceBase.StartLoginAsync(string, TimeSpan?, CancellationToken)"/> for this
    /// session, retained so a SECOND login request arriving while the daemon is still mid-flow (e.g. the
    /// frontend closed/reopened the login modal) can be answered with the SAME challenge instead of issuing
    /// another daemon <c>login</c> command - the daemon replies "already in progress" without re-emitting a
    /// challenge, and the socket/TCP client destroys its own queued copy the moment a second
    /// <c>StartLoginAsync</c> is invoked, so a resume must never reach the daemon at all. Cleared on
    /// cancel-login, on any transition to <see cref="DaemonAuthState.Authenticated"/> (see
    /// <see cref="PrefillDaemonServiceBase.NotifyAuthStateChangeAsync(DaemonSession)"/>), on a fail-fast login
    /// failure, and on session termination, so a stale challenge is never served once the daemon has moved on.
    /// Transient - not persisted, not part of <see cref="DaemonSessionDto"/>.
    /// </summary>
    public CredentialChallenge? PendingLoginChallenge { get; set; }

    /// <summary>
    /// Id of the tracked operation raised for the login attempt currently in flight on this session,
    /// or null when no login is being tracked. It is the notification card's id, so the bar's X and
    /// <c>POST /api/operations/{id}/cancel</c> can both reach this session's login. Set in
    /// <c>PrefillDaemonServiceBase.RegisterLoginOperation</c> and cleared by
    /// <c>PrefillDaemonServiceBase.CompleteLoginOperation</c>. Its presence is also the re-entry guard
    /// that stops a resumed login (the modal closed and reopened, so <see cref="PendingLoginChallenge"/>
    /// answers the second call without a daemon command) raising a second card for one attempt.
    /// Transient - not persisted, not part of <see cref="DaemonSessionDto"/>.
    /// </summary>
    public Guid? LoginOperationId { get; set; }

    /// <summary>
    /// Absolute UTC deadline for the tracked login attempt, or null when no login is being tracked.
    /// A daemon challenge replaces it with the provider's deadline when one is supplied. Written and
    /// cleared alongside
    /// <see cref="LoginOperationId"/>. Transient - not persisted, not part of
    /// <see cref="DaemonSessionDto"/>.
    /// </summary>
    public DateTime? LoginExpiresAtUtc { get; set; }

    /// <summary>The locale key naming why the app ended the current sign-in; null when a person ended it or it is still running.</summary>
    public string? LoginStopReason { get; set; }

    /// <summary>
    /// How this session's last tracked sign-in ended, by its <see cref="LoginAttempt"/>, for a browser that missed the
    /// auth-state event. Null until one ends. Transient - not persisted.
    /// </summary>
    public PrefillLoginEnding? LastLoginEnding { get; set; }

    /// <summary>
    /// The <see cref="CredentialChallenge.ChallengeId"/> of the login challenge most recently answered via
    /// <see cref="PrefillDaemonServiceBase.ProvideCredentialAsync"/> on this session. The daemon delivers each
    /// credential challenge over TWO channels - the <c>WaitForChallengeAsync</c> return value AND the
    /// <c>OnCredentialChallenge</c> event - so once a credential is provided (and <see cref="PendingLoginChallenge"/>
    /// is cleared), a late duplicate of that SAME challenge arriving over the event channel must NOT re-cache the
    /// now-consumed challenge, or the next poll would replay it (the "challenge:password twice" race).
    /// <see cref="PrefillDaemonServiceBase.OnCredentialChallengeAsync(DaemonSession, CredentialChallenge)"/> drops any
    /// incoming challenge whose id equals this value; only a genuinely new follow-on challenge (different id) is allowed
    /// to repopulate the cache and broadcast. Reset to null at the start of each fresh login attempt so it can never
    /// suppress a legitimate future challenge. Transient - not persisted, not part of <see cref="DaemonSessionDto"/>.
    /// </summary>
    public string? LastConsumedLoginChallengeId { get; set; }

    /// <summary>
    /// Counts the fresh daemon logins started on this session; a resumed challenge keeps the current value.
    /// Stamped onto every challenge handed to the browser as <see cref="CredentialChallenge.LoginAttempt"/>, so a
    /// cancel names the attempt it belongs to and a late cancel never ends a newer attempt. Incremented under
    /// <see cref="PrefillLock"/>. Transient - not persisted.
    /// </summary>
    public long LoginAttempt { get; set; }

    /// <summary>
    /// The retry that signs a persistent session in again after a dropped Steam connection, running or
    /// finished. Set and cleared under <see cref="PrefillLock"/>; any applied sign-in clears it.
    /// </summary>
    internal Task? SignInAgainAfterDrop { get; set; }

    /// <summary>
    /// Stops <see cref="SignInAgainAfterDrop"/> when a person starts their own sign-in. Set with it, under
    /// <see cref="PrefillLock"/>. Never disposed: it holds no registrations, so a cancel after the retry
    /// ended does nothing.
    /// </summary>
    internal CancellationTokenSource? SignInAgainCancel { get; set; }

    /// <summary>
    /// A person canceled a running sign-in, or a Log in of theirs that the server recorded and had not refused or
    /// ended, so the <c>auth-lost</c> code the daemon writes for that cancel starts no automatic sign-in. Set and read
    /// under <see cref="PrefillLock"/>; only the next applied sign-in or the person's next accepted Log in clears it.
    /// A persistent session stores it on its row, so a manager restart or a recreate keeps it.
    /// </summary>
    internal bool LoginCanceledByPerson { get; set; }

    /// <summary>
    /// The daemon reached logged-in while <see cref="LoginCanceledByPerson"/> still held. Set and cleared
    /// under <see cref="PrefillLock"/> with the mark. A persistent session stores it on the same row, so a
    /// manager restart still treats a later connection drop as the one automatic sign-in. The cancel's own
    /// auth-lost finds this clear.
    /// </summary>
    internal bool LoggedInWhilePersonCanceled { get; set; }

    /// <summary>
    /// The daemon reported this signed-in persistent container signed out without the drop code, as Steam, Epic
    /// and Xbox do when the account is logged off, or a person's Log out of it landed, so the automatic sign-in with
    /// the saved login skips it. Set and read under <see cref="PrefillLock"/>; the person's next accepted Log in clears
    /// it. Stored on the session's row, so a manager restart or a recreate keeps it, and copied to the session that
    /// replaces this one after an image update or an error.
    /// </summary>
    internal bool SignedOut { get; set; }

    /// <summary>
    /// Lets one write of the session's login state (<see cref="LoginCanceledByPerson"/>,
    /// <see cref="LoggedInWhilePersonCanceled"/>, <see cref="SignedOut"/>, <see cref="LastLoginAt"/> and
    /// <see cref="ExpiresAt"/>) to its row run at a time.
    /// </summary>
    internal SemaphoreSlim LoginStateWrites { get; } = new(1, 1);

    /// <summary>
    /// The automatic sign-in with the saved login has sent its login and not ended yet, so a cancel it meets is not a
    /// person's. Set under <see cref="PrefillLock"/> with the suppress flag; cleared when that try ends, however it ends.
    /// A person's "Use my saved login" also suppresses challenges but is still theirs to cancel.
    /// </summary>
    internal bool SigningInAutomatically { get; set; }

    /// <summary>
    /// How many times this session changed into signed in, under <see cref="PrefillLock"/>. A retry
    /// that sees it move while it waits returns, because something else signed the session in.
    /// </summary>
    internal int SignInCount { get; set; }

    /// <summary>
    /// How many times this session changed out of signed in, under <see cref="PrefillLock"/>. A status read
    /// takes it before the read, and a signed-in answer is not applied when it moved meanwhile.
    /// </summary>
    internal int SignOutCount { get; set; }

    /// <summary>Blocks a new daemon login until the current cancel command has settled.</summary>
    public bool LoginCanceling { get; set; }
    public Task<bool>? LoginCancelTask { get; set; }
    public TaskCompletionSource<bool>? LoginDispatch { get; set; }
    public Dictionary<Guid, LoginRequest> LoginRequests { get; } = new();

    /// <summary>
    /// True while a headless (manager-initiated) login attempt owns this session's login flow.
    /// While set, an incoming daemon credential challenge is NOT published: the event handler in
    /// <c>PrefillDaemonServiceBase.OnCredentialChallengeAsync</c> neither rewrites <see cref="AuthState"/>,
    /// nor caches <see cref="PendingLoginChallenge"/>, nor broadcasts the challenge - and the login flow
    /// skips its own resume-cache writes. The headless attempt consumes the challenge from the command
    /// return channel and silently cancels it, so publishing would hand the UI (or an unlocked REST
    /// challenge poll) a challenge that is about to be revoked. Only ever set and cleared by the headless
    /// coordinator while it holds <see cref="LoginLock"/>; a field (not a property) so it can be volatile,
    /// because the transport event handlers read it from socket threads. Interactive logins never set it,
    /// so their challenge delivery is unchanged.
    /// </summary>
    public volatile bool SuppressLoginChallengePublication;

    /// <summary>
    /// Serializes login attempts on this session (<c>PrefillDaemonServiceBase.StartLoginAsync</c>).
    /// Overlapping calls would race the daemon's single challenge/status stream, clobber each other's
    /// <see cref="LastLoginFailureMessage"/>/<see cref="AuthState"/> writes, and stack duplicate
    /// <c>OnStatusUpdate</c> subscriptions. A second concurrent attempt is rejected outright (try-acquire,
    /// never queued) rather than silently waiting behind one the caller doesn't know about.
    /// </summary>
    public SemaphoreSlim LoginLock { get; } = new(1, 1);

    public object PrefillLock { get; } = new();

    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the login was last signed in; <see cref="ExpiresAt"/> of a persistent session is this time plus the
    /// admin-configured validity days. Read only by the writers of that date.
    /// </summary>
    public DateTime LastLoginAt { get; set; }

    /// <summary>
    /// True when this session is a guest/temporary container subject to the manager-enforced,
    /// per-service guest permission duration cap (<c>PrefillDaemonServiceBase.GetGuestPermissionDurationHours</c>).
    /// When true, <see cref="ExpiresAt"/> was stamped to createdAt + the configured cap and the
    /// session is reaped by <c>CleanupExpiredSessions</c> at that expiry. Admin/persistent sessions
    /// leave this false and keep the standard timeout.
    /// </summary>
    public bool IsTemporary { get; init; }

    /// <summary>
    /// True when this session is a persistent admin login that must survive the
    /// <c>CleanupExpiredSessions</c> reaper. Persistent sessions are never torn down by the reaper;
    /// instead, when past <see cref="ExpiresAt"/>, <see cref="NeedsRelogin"/> is flagged and the
    /// container is left running so the admin can re-authenticate in place.
    /// </summary>
    public bool IsPersistent { get; init; }

    /// <summary>
    /// True when a persistent session has passed its <see cref="ExpiresAt"/> and now requires the
    /// admin to re-login. Set by the reaper (instead of terminating the session) and cleared once
    /// the session is re-authenticated.
    /// </summary>
    public bool NeedsRelogin { get; set; }

    /// <summary>
    /// The prefill account username (set from a username credential or the daemon's authenticated account name).
    /// Used for ban display and admin visibility.
    /// </summary>
    public string? AccountUsername { get; set; }

    /// <summary>
    /// Platform identifier (e.g., "Steam", "Epic")
    /// </summary>
    public string Platform { get; set; } = "Steam";

    /// <summary>
    /// Platform-agnostic display name. For Steam, populated from credential username.
    /// For Epic, populated from OAuth token display name.
    /// </summary>
    public string? Username { get; set; }

    /// <summary>
    /// Client connection info for admin visibility
    /// </summary>
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? OperatingSystem { get; init; }
    public string? Browser { get; init; }
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Network diagnostics results (internet connectivity and DNS resolution tests)
    /// </summary>
    public NetworkDiagnostics? NetworkDiagnostics { get; set; }

    public IDaemonClient Client { get; set; } = null!;

    /// <summary>
    /// Path to the daemon's Unix socket.
    /// </summary>
    public string? SocketPath { get; set; }
    public HashSet<string> SubscribedConnections { get; } = new();

    /// <summary>
    /// Each subscribed connection's <see cref="SubscribeCount"/> at its last subscribe, under <see cref="PrefillLock"/>.
    /// </summary>
    public Dictionary<string, long> LastSubscribeCounts { get; } = new();

    /// <summary>
    /// Counts subscribes, under <see cref="PrefillLock"/>. A send that failed drops its connection unless that connection
    /// subscribed again after the send's snapshot (<see cref="LastSubscribeCounts"/>): that tab is live.
    /// </summary>
    public long SubscribeCount { get; set; }
    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public sealed class LoginRequest
    {
        public long? Attempt { get; set; }
        public bool Canceled { get; set; }
    }
}

/// <summary>
/// High-level lifecycle state of a prefill run, tracked on the session and driven by the
/// terminal socket state rather than the (immediate) daemon start-ack.
/// </summary>
public enum PrefillState
{
    /// <summary>No prefill is active.</summary>
    Idle,

    /// <summary>Prefill was initiated (start-ack received) but no download tick has arrived yet.</summary>
    Started,

    /// <summary>Daemon is actively downloading content (a progress tick has been observed).</summary>
    Downloading,

    /// <summary>Prefill run completed successfully.</summary>
    Completed,

    /// <summary>Prefill run failed (including socket death mid-prefill).</summary>
    Failed,

    /// <summary>Prefill run was cancelled by the user.</summary>
    Cancelled
}
