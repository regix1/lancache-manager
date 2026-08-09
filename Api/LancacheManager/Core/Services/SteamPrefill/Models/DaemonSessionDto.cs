namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// DTO for daemon session information
/// </summary>
public class DaemonSessionDto
{
    public string Id { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// The failure reason recorded on the session. Null while the session has not recorded a failure,
    /// which is the normal state for every healthy session.
    /// </summary>
    public string? ErrorMessage { get; set; }

    public string AuthState { get; set; } = string.Empty;
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int TimeRemainingSeconds { get; set; }

    /// <summary>
    /// True when this is a guest/temporary container whose <see cref="ExpiresAt"/> reflects the
    /// manager-enforced, per-service guest permission duration cap. The UI can use this to render a
    /// lifetime countdown (via <see cref="TimeRemainingSeconds"/>) distinct from the standard session timeout.
    /// </summary>
    public bool IsTemporary { get; set; }

    /// <summary>
    /// True when this is a persistent admin session that the reaper will never tear down. When past
    /// its expiry the session is flagged <see cref="NeedsRelogin"/> instead of being terminated.
    /// </summary>
    public bool IsPersistent { get; set; }

    /// <summary>
    /// True when a persistent session has expired and now requires the admin to re-login.
    /// </summary>
    public bool NeedsRelogin { get; set; }

    // Client info for admin visibility

    /// <summary>
    /// The client address the session was created from. Null when the request carried no remote
    /// address, which happens for sessions the manager itself starts rather than a browser.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Operating system parsed from the creating request's user agent. Null when the request sent no
    /// user agent or the agent could not be recognised.
    /// </summary>
    public string? OperatingSystem { get; set; }

    /// <summary>
    /// Browser parsed from the creating request's user agent. Null when the request sent no user
    /// agent or the agent could not be recognised.
    /// </summary>
    public string? Browser { get; set; }

    public DateTime LastSeenAt { get; set; }

    /// <summary>
    /// The prefill account this session is logged in as. Null before the login completes, and always
    /// null for the anonymous platforms that have no account at all.
    /// </summary>
    public string? AccountUsername { get; set; }

    public string Platform { get; set; } = "Steam";

    /// <summary>
    /// Platform-agnostic display name, falling back to <see cref="AccountUsername"/>. Null for the
    /// same reason that name is null: no login has completed, or the platform has no account.
    /// </summary>
    public string? Username { get; set; }

    // Current prefill progress info for admin visibility

    /// <summary>
    /// The app currently downloading. Null whenever the session is not downloading, and cleared again
    /// as soon as a prefill run ends.
    /// </summary>
    public string? CurrentAppId { get; set; }

    /// <summary>
    /// Display name of the app currently downloading. Null under the same conditions as
    /// <see cref="CurrentAppId"/>.
    /// </summary>
    public string? CurrentAppName { get; set; }

    /// <summary>
    /// Total bytes transferred during this session (cumulative across all games)
    /// </summary>
    public long TotalBytesTransferred { get; set; }

    /// <summary>
    /// Network diagnostics results (internet connectivity and DNS resolution tests). Null when the
    /// container's connectivity test did not run or did not finish, so the session was created
    /// without a result to report.
    /// </summary>
    public NetworkDiagnostics? NetworkDiagnostics { get; set; }

    /// <summary>
    /// When the last prefill on this session finished, used for background completion detection.
    /// Null before any prefill has completed, and cleared again while a new prefill is running.
    /// </summary>
    public DateTime? LastPrefillCompletedAt { get; set; }

    /// <summary>
    /// How long the last completed prefill took. Null under the same conditions as
    /// <see cref="LastPrefillCompletedAt"/>.
    /// </summary>
    public int? LastPrefillDurationSeconds { get; set; }

    /// <summary>
    /// How the last prefill ended (for example completed or cancelled). Null under the same
    /// conditions as <see cref="LastPrefillCompletedAt"/>.
    /// </summary>
    public string? LastPrefillStatus { get; set; }

    public static DaemonSessionDto FromSession(DaemonSession session)
    {
        return new DaemonSessionDto
        {
            Id = session.Id,
            UserId = session.UserId,
            ContainerName = session.ContainerName,
            Status = session.Status.ToString(),
            ErrorMessage = session.ErrorMessage,
            AuthState = session.AuthState.ToString(),
            IsPrefilling = session.IsPrefilling,
            CreatedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            TimeRemainingSeconds = Math.Max(0, (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds),
            IsTemporary = session.IsTemporary,
            IsPersistent = session.IsPersistent,
            NeedsRelogin = session.NeedsRelogin,
            IpAddress = session.IpAddress,
            OperatingSystem = session.OperatingSystem,
            Browser = session.Browser,
            LastSeenAt = session.LastSeenAt,
            AccountUsername = session.AccountUsername,
            Platform = session.Platform,
            Username = session.Username ?? session.AccountUsername,
            CurrentAppId = session.CurrentAppId,
            CurrentAppName = session.CurrentAppName,
            TotalBytesTransferred = session.TotalBytesTransferred,
            NetworkDiagnostics = session.NetworkDiagnostics,
            LastPrefillCompletedAt = session.LastPrefillCompletedAt,
            LastPrefillDurationSeconds = session.LastPrefillDurationSeconds,
            LastPrefillStatus = session.LastPrefillStatus
        };
    }
}
