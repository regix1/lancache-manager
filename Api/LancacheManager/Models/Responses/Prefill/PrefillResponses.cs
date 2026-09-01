using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Models;

/// <summary>
/// Response for prefill cache status check
/// </summary>
public class PrefillCacheStatusResponse
{
    public List<string> UpToDateAppIds { get; set; } = new();
    public List<string> OutdatedAppIds { get; set; } = new();

    /// <summary>
    /// A note about the cache check as a whole, such as apps it could not inspect. Null when every
    /// requested app was checked without incident.
    /// </summary>
    public string? Message { get; set; }
}

/// <summary>
/// Result of clearing one app's prefill cache.
/// </summary>
public class PrefillCacheRemovalResponse
{
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Depot rows actually removed. Zero means the app read as cached off another app's rows and
    /// owned none of its own, which is the only case where the badge survives the delete.
    /// </summary>
    public int RemovedDepots { get; set; }
}

/// <summary>
/// Result of the terminate-all sweep. Carries the count on its own rather than a sentence built
/// around it, so the browser writes the line in the reader's language.
/// </summary>
public class TerminatedSessionsResponse
{
    public int Count { get; set; }
}

/// <summary>
/// 404 body for "no running persistent session" lookups (<see cref="Controllers.PersistentPrefillController"/>).
/// Distinguishes a session that exists but flipped to <see cref="DaemonSessionStatus.Error"/>
/// (e.g. the daemon's socket dropped) from no session ever having been started, so the frontend
/// can show "press Start to restart" vs a generic "not running" message.
/// </summary>
public class PersistentSessionNotFoundResponse
{
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// i18n key naming the refusal, read by <c>getErrorMessage</c> so the reader sees it in their
    /// own language; <see cref="Error"/> stays on the wire as the English fallback.
    /// </summary>
    public string? StageKey { get; set; }

    /// <summary>Substitution values for the <see cref="StageKey"/> template.</summary>
    public Dictionary<string, object?>? Context { get; set; }

    public PersistentSessionNotFoundState State { get; set; } = PersistentSessionNotFoundState.NotStarted;
}

/// <summary>
/// Discriminator for <see cref="PersistentSessionNotFoundResponse"/>. Serialized as a camelCase
/// string ("notStarted"/"errored") to match the rest of the codebase's wire-enum convention
/// (see <see cref="OperationStatus"/>).
/// </summary>
[JsonConverter(typeof(PersistentSessionNotFoundStateJsonConverter))]
public enum PersistentSessionNotFoundState
{
    NotStarted,
    Errored
}

internal sealed class PersistentSessionNotFoundStateJsonConverter : JsonStringEnumConverter<PersistentSessionNotFoundState>
{
    public PersistentSessionNotFoundStateJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Stable <c>error</c> discriminators for <see cref="PersistentLoginConflictResponse"/> (RC3).
/// The frontend reads these off <c>error.cause</c> structurally - never
/// by sniffing the message text - so they are a wire contract shared 1:1 with
/// <c>usePersistentPrefillAuth.ts</c>.
/// </summary>
public static class PersistentLoginConflictReasons
{
    /// <summary>The session a login flow was pinned to has been replaced by a different active session.</summary>
    public const string SessionReplaced = "session_replaced";

    /// <summary>The daemon rejected the credential (no matching pending challenge) - see RC4.</summary>
    public const string CredentialRejected = "credential_rejected";
}

/// <summary>
/// 409 body for a persistent-login REST call that was pinned to a session which is no longer the one
/// the server would act on (RC3): either a different session has
/// become active for the service (<see cref="PersistentLoginConflictReasons.SessionReplaced"/>) or the
/// daemon dropped the supplied credential (<see cref="PersistentLoginConflictReasons.CredentialRejected"/>).
/// The frontend reads <see cref="Error"/> + <see cref="State"/> from <c>error.cause</c>.
/// </summary>
public class PersistentLoginConflictResponse
{
    /// <summary>One of <see cref="PersistentLoginConflictReasons"/>.</summary>
    public string Error { get; set; } = string.Empty;

    /// <summary>
    /// Current server-side state of the service's persistent session, for display: "active" when a
    /// different persistent session is now running for the service, "errored" when the current one is
    /// in the Error status, or "notStarted" when none is running.
    /// </summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>
/// Success body for the persistent login/challenge routes when the session is already authenticated
/// (no credential challenge to answer). Carries the resolved <see cref="SessionId"/> so the frontend
/// can pin the session the flow belongs to (RC3), plus the
/// existing <c>status:"logged-in"</c> shape the frontend already type-guards on.
/// </summary>
public class PersistentLoginStatusResponse
{
    /// <summary>Id of the persistent session the login resolved on.</summary>
    public required string SessionId { get; set; }

    /// <summary>Login status; "logged-in" for the already-authenticated case.</summary>
    public string Status { get; set; } = "logged-in";

    /// <summary>Human-readable status message (e.g. "Already logged in"). Null when there is nothing extra to say beyond <see cref="Status"/>.</summary>
    public string? Message { get; set; }
}

/// <summary>
/// Response for paginated prefill sessions
/// </summary>
public class PrefillSessionsResponse
{
    public List<PrefillSessionDto> Sessions { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>
    /// Cache server IP most recently injected into a prefill daemon container (any service), plus
    /// the locator source it was found through (config | dns | dockerInspect | envFile | detected |
    /// none). Both are null until the first prefill container is created this process lifetime.
    /// </summary>
    public string? LastPrefillCacheIp { get; set; }
    public string? LastPrefillCacheIpSource { get; set; }
}

/// <summary>
/// DTO for prefill session information
/// </summary>
public class PrefillSessionDto
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public Guid CreatedBySessionId { get; set; }
    public string? ContainerId { get; set; }
    public string? ContainerName { get; set; }
    public string? AccountUsername { get; set; }
    public string Platform { get; set; } = "Steam";
    public string? Username { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsAuthenticated { get; set; }
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EndedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? TerminationReason { get; set; }
    public string? TerminatedBy { get; set; }
    public bool IsLive { get; set; }

    /// <summary>
    /// True when this row represents a persistent (system-owned) daemon container rather than a
    /// guest/temporary session. See <see cref="LancacheManager.Models.PrefillSession.IsPersistent"/>.
    /// </summary>
    public bool IsPersistent { get; set; }

    /// <summary>
    /// Maps a persisted history row to its wire DTO, enriching with the matching live in-memory
    /// session when one exists. Extracted as a pure factory (mirrors <see cref="DaemonSessionDto.FromSession"/>)
    /// so the enrichment rules - "IsAuthenticated = live-or-persisted", "IsPersistent = persisted OR live" -
    /// stay in one place and are unit-testable without a DbContext or the daemon services
    /// PrefillAdminController depends on.
    /// </summary>
    public static PrefillSessionDto FromEntity(PrefillSession entity, DaemonSession? liveSession)
    {
        return new PrefillSessionDto
        {
            Id = entity.Id,
            SessionId = entity.SessionId,
            CreatedBySessionId = entity.CreatedBySessionId,
            ContainerId = entity.ContainerId,
            ContainerName = entity.ContainerName,
            AccountUsername = liveSession?.AccountUsername ?? entity.AccountUsername,
            Platform = liveSession?.Platform ?? entity.Platform.ToString(),
            Username = liveSession != null ? (liveSession.Username ?? liveSession.AccountUsername) : entity.AccountUsername,
            Status = liveSession?.Status.ToString() ?? entity.Status.ToString(),
            IsAuthenticated = liveSession?.AuthState == DaemonAuthState.Authenticated || entity.IsAuthenticated,
            IsPrefilling = liveSession?.IsPrefilling ?? entity.IsPrefilling,
            CreatedAtUtc = entity.CreatedAtUtc,
            EndedAtUtc = entity.EndedAtUtc,
            ExpiresAtUtc = entity.ExpiresAtUtc,
            TerminationReason = entity.TerminationReason,
            TerminatedBy = entity.TerminatedBy,
            IsLive = liveSession != null,
            IsPersistent = entity.IsPersistent || (liveSession?.IsPersistent ?? false)
        };
    }
}

/// <summary>
/// DTO for banned prefill user information
/// </summary>
public class BannedPrefillUserDto
{
    public long Id { get; set; }

    /// <summary>The banned account's username. Null when the ban was recorded against a session that had no resolved account username at the time.</summary>
    public string? Username { get; set; }

    /// <summary>Resolved account id for the banned user, when one was known. Null when only a username was available.</summary>
    public Guid? BannedUserId { get; set; }

    /// <summary>Admin-supplied reason for the ban. Null when none was given.</summary>
    public string? BanReason { get; set; }

    /// <summary>Session that created the ban. Null for a ban not tied to a specific session (e.g. banned by username with no live session).</summary>
    public Guid? BannedBySessionId { get; set; }

    public DateTime BannedAtUtc { get; set; }
    public string? BannedBy { get; set; }

    /// <summary>UTC instant the ban expires. Null when the ban never expires.</summary>
    public DateTime? ExpiresAtUtc { get; set; }

    public bool IsLifted { get; set; }

    /// <summary>UTC instant the ban was lifted. Null while the ban is still active.</summary>
    public DateTime? LiftedAtUtc { get; set; }

    /// <summary>Who lifted the ban. Null while the ban is still active.</summary>
    public string? LiftedBy { get; set; }

    public bool IsActive { get; set; }
}

/// <summary>
/// DTO for prefill history entries
/// </summary>
public class PrefillHistoryEntryDto
{
    public long Id { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public string AppId { get; set; } = string.Empty;

    /// <summary>Resolved app name. Null when it could not be looked up.</summary>
    public string? AppName { get; set; }

    public DateTime StartedAtUtc { get; set; }

    /// <summary>UTC instant the entry finished. Null while the prefill is still running.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string Status { get; set; } = string.Empty;

    /// <summary>Failure detail. Null unless <see cref="Status"/> reports a failure.</summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// DTO for cached app information
/// </summary>
public class CachedAppDto
{
    public string AppId { get; set; } = string.Empty;

    /// <summary>Resolved app name. Null when it could not be looked up.</summary>
    public string? AppName { get; set; }

    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }

    /// <summary>Who/what triggered the cache (username, or a system source). Null when not recorded.</summary>
    public string? CachedBy { get; set; }
}
