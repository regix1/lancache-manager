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
    public string? Message { get; set; }
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
/// Stable <c>error</c> discriminators for <see cref="PersistentLoginConflictResponse"/> (RC3, session
/// 20260703-221336-2070027597). The frontend reads these off <c>error.cause</c> structurally - never
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
/// the server would act on (RC3, session 20260703-221336-2070027597): either a different session has
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
/// can pin the session the flow belongs to (RC3, session 20260703-221336-2070027597), plus the
/// existing <c>status:"logged-in"</c> shape the frontend already type-guards on.
/// </summary>
public class PersistentLoginStatusResponse
{
    /// <summary>Id of the persistent session the login resolved on.</summary>
    public required string SessionId { get; set; }

    /// <summary>Login status; "logged-in" for the already-authenticated case.</summary>
    public string Status { get; set; } = "logged-in";

    /// <summary>Optional human-readable message (e.g. "Already logged in").</summary>
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
    public string? SteamUsername { get; set; }
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
            SteamUsername = liveSession?.SteamUsername ?? entity.SteamUsername,
            Platform = liveSession?.Platform ?? entity.Platform.ToString(),
            Username = liveSession != null ? (liveSession.Username ?? liveSession.SteamUsername) : entity.SteamUsername,
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
/// DTO for banned Steam user information
/// </summary>
public class BannedSteamUserDto
{
    public long Id { get; set; }
    public string? Username { get; set; }
    public Guid? BannedUserId { get; set; }
    public string? BanReason { get; set; }
    public Guid? BannedBySessionId { get; set; }
    public DateTime BannedAtUtc { get; set; }
    public string? BannedBy { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public bool IsLifted { get; set; }
    public DateTime? LiftedAtUtc { get; set; }
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
    public string? AppName { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// DTO for cached app information
/// </summary>
public class CachedAppDto
{
    public string AppId { get; set; } = string.Empty;
    public string? AppName { get; set; }
    public int DepotCount { get; set; }
    public long TotalBytes { get; set; }
    public DateTime CachedAtUtc { get; set; }
    public string? CachedBy { get; set; }
}

/// <summary>
/// Response for cache check operation
/// </summary>
public class CacheCheckResponse
{
    public List<string> CachedAppIds { get; set; } = new();
    public List<string> UncachedAppIds { get; set; } = new();
    public List<CachedAppDto> CacheInfo { get; set; } = new();
}

