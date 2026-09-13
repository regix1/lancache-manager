using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Controllers;

/// <summary>Identifies a platform for persistent session operations.</summary>
public sealed class PersistentServiceRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public Guid? RunId { get; init; }
}

/// <summary>Sets selected apps on the running persistent session.</summary>
public sealed class PersistentSelectedAppsRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public required List<string> AppIds { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>Starts prefill on the running persistent session (mirrors guest StartPrefillRequest).</summary>
public sealed class PersistentStartPrefillRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public List<string>? AppIds { get; init; }
    public bool All { get; init; }
    public bool Recent { get; init; }
    public bool RecentlyPurchased { get; init; }
    public int? Top { get; init; }
    public bool Force { get; init; }
    public List<string>? OperatingSystems { get; init; }
    public int? MaxConcurrency { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>Request body for starting a persistent session.</summary>
public sealed class StartPersistentSessionRequest
{
    /// <summary>Platform whose daemon should own the persistent session.</summary>
    public required PrefillPlatform Service { get; init; }

    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>
/// Request body for the persistent interactive-login routes that only need to identify the platform
/// (login start / cancel-login). The running persistent session is resolved server-side.
/// </summary>
public sealed class PersistentLoginRequest
{
    /// <summary>Platform whose running persistent session should be logged in / cancelled.</summary>
    public required PrefillPlatform Service { get; init; }
    public string? SessionId { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
    public bool ReuseIntegration { get; init; }
}

public sealed class PersistentPrefillEditSessionCleanupRequest
{
    public required string EditSessionId { get; init; }
    public required string CleanupId { get; init; }
    public required List<PersistentPrefillEditSessionCleanupServiceRequest> Services { get; init; }
}

public sealed class PersistentPrefillEditSessionCleanupServiceRequest
{
    public required PrefillPlatform Service { get; init; }
    public string? BaselineSessionId { get; init; }
    public required List<string> BaselineSelectedAppIds { get; init; }
    public string? StartSessionId { get; init; }
    public string? LoginSessionId { get; init; }
    public string? PrefillSessionId { get; init; }
    public string? SelectionSessionId { get; init; }
}

/// <summary>
/// Request body for providing a credential to the running persistent session. Carries the platform
/// plus the exact same <see cref="CredentialChallenge"/> + credential payload the user route uses
/// (<see cref="ProvideCredentialRequest"/>), so the frontend can reuse its login challenge types.
/// </summary>
public sealed class PersistentProvideCredentialRequest
{
    /// <summary>Platform whose running persistent session the credential targets.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>
    /// Id of the persistent session this credential answers a challenge for. REQUIRED (RC3):
    /// a mismatch against the currently-active session yields a 409
    /// <c>session_replaced</c> rather than letting the credential land on a replacement session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>The challenge being answered (same shape as the user credential route).</summary>
    public CredentialChallenge? Challenge { get; init; }

    /// <summary>The encrypted credential value (same shape as the user credential route).</summary>
    public string? Credential { get; init; }
    public string? EditSessionId { get; init; }
    public string? EditActionId { get; init; }
}

/// <summary>
/// Request body for cancelling a persistent interactive login. Carries the pinned session id so a
/// cancel for a since-replaced session (RC3) is an idempotent
/// no-op that never cancels the replacement session's login.
/// </summary>
public sealed class PersistentCancelLoginRequest
{
    /// <summary>Platform whose running persistent session's login should be cancelled.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>
    /// Id of the persistent session whose in-flight login should be cancelled. REQUIRED - a mismatch is
    /// treated as an idempotent no-op (200) that does not touch the currently-active session's login.
    /// </summary>
    public string? SessionId { get; init; }
}

/// <summary>Request body for stopping a persistent session.</summary>
public sealed class StopPersistentSessionRequest
{
    /// <summary>Id of the persistent session to stop.</summary>
    public required string SessionId { get; init; }
}

/// <summary>Result of a persistent-session logout attempt.</summary>
public sealed class PersistentLogoutResponseDto
{
    /// <summary>
    /// True when the daemon acknowledged the in-place logout; the container was not restarted. Not a
    /// hard guarantee the account file was deleted - an un-updated steam/epic daemon image also
    /// reports success while only tearing down the live session (see
    /// <see cref="PrefillDaemonServiceBase.LogoutPersistentSessionAsync(string, CancellationToken)"/>).
    /// </summary>
    public required bool Forgotten { get; init; }

    /// <summary>
    /// Present only when <see cref="Forgotten"/> is false: the attempt genuinely failed (daemon
    /// reported failure, or the round-trip threw), so the caller must fall back to a stop+restart to
    /// clear the session's auth state.
    /// </summary>
    public string? Fallback { get; init; }
}

/// <summary>Typed view of a persistent prefill session.</summary>
public sealed class PersistentPrefillSessionDto
{
    public IReadOnlyList<DaemonRunStatus> Runs { get; init; } = [];
    public string? DaemonInstanceId { get; init; }
    public IReadOnlyList<string> Features { get; init; } = [];
    public int MaxConcurrentRuns { get; init; } = 1;
    public int ActiveRunCount { get; init; }
    public bool Recovering { get; init; }
    /// <summary>Daemon session id.</summary>
    public required string SessionId { get; init; }

    /// <summary>Platform that owns the session.</summary>
    public required PrefillPlatform Service { get; init; }

    /// <summary>True while the daemon session is in the Active status.</summary>
    public required bool IsRunning { get; init; }

    /// <summary>
    /// True when the daemon reports it is actually logged in (live <c>status</c> == "logged-in").
    /// This is the UI's source of truth for authentication, not <see cref="NeedsRelogin"/>. Defaults
    /// to false when the session is not running or the status call is unavailable.
    /// </summary>
    public required bool IsAuthenticated { get; init; }

    /// <summary>
    /// The date the admin has to log in again by, and the one every screen shows. The EARLIER of the
    /// manager's validity window (DaemonSession.ExpiresAt) and the daemon's real token expiry, capped
    /// by <see cref="PersistentPrefillController.ComputeEffectiveRelogin"/> at display time so no
    /// screen promises a window that outlives the token.
    /// </summary>
    public required DateTime AuthExpiresAtUtc { get; init; }

    /// <summary>
    /// UTC instant the session was created. The validity window is anchored here rather than on the
    /// login, so the UI can show what a changed window would move the re-login date to before the
    /// change is saved (see <see cref="PrefillDaemonServiceBase.UpdatePersistentSessionExpiryAsync"/>).
    /// </summary>
    public required DateTime CreatedAtUtc { get; init; }

    /// <summary>Seconds remaining until <see cref="AuthExpiresAtUtc"/> (0 once elapsed).</summary>
    public required long AuthTimeRemainingSeconds { get; init; }

    /// <summary>True when the session is past expiry and the admin must re-authenticate in place.</summary>
    public required bool NeedsRelogin { get; init; }

    /// <summary>
    /// The daemon's REAL underlying token expiry queried live from its <c>status</c> command
    /// (Steam JWT ValidTo / Epic refresh_expires_at / Xbox refresh-token expiry). One of the two
    /// inputs <see cref="AuthExpiresAtUtc"/> is capped from, and NOT a date to show on its own: the
    /// manager flags a session on its validity window, so this one can sit months past the login the
    /// admin actually owes. The config modal reads it to preview what a changed window would produce.
    /// Null when the session is not running, the status call fails, or the daemon does not report it.
    /// </summary>
    public DateTimeOffset? DaemonAuthExpiresAtUtc { get; init; }

    /// <summary>True while a prefill download is in progress on this session.</summary>
    public bool IsPrefilling { get; init; }

    /// <summary>Identity of the active prefill run, or null while no prefill is running.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Aggregate bytes transferred during the current or last prefill run.</summary>
    public long TotalBytesTransferred { get; init; }

    /// <summary>Name of the app currently being prefilled, if any.</summary>
    public string? CurrentAppName { get; init; }
}

/// <summary>Persistent login validity window, in days.</summary>
public sealed class PersistentLoginValidityDto
{
    /// <summary>Validity window in days (1-365).</summary>
    public required int Days { get; init; }
}

/// <summary>
/// Owned games plus up-to-date cached app ids for a persistent session. Matches the shape the
/// frontend GameSelectionModal expects (games[], cachedAppIds[]).
/// </summary>
public sealed class PersistentPrefillGamesDto
{
    /// <summary>Owned games for the persistent session (same payload as the user games route).</summary>
    public required List<OwnedGame> Games { get; init; }

    /// <summary>App ids whose cached content is up to date for the session.</summary>
    public required List<string> CachedAppIds { get; init; }
    public List<string> UnknownAppIds { get; init; } = [];
}
