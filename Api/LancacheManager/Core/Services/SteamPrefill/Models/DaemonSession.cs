namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Represents a Steam Prefill daemon session
/// </summary>
public class DaemonSession
{
    public string Id { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string CommandsDir { get; init; } = string.Empty;
    public string ResponsesDir { get; init; } = string.Empty;
    public DaemonSessionStatus Status { get; set; } = DaemonSessionStatus.Active;
    public string? ErrorMessage { get; set; }
    public DaemonAuthState AuthState { get; set; } = DaemonAuthState.NotAuthenticated;
    public bool IsPrefilling { get; set; }

    /// <summary>
    /// High-level lifecycle state of the current/last prefill run. Driven by the terminal
    /// socket state (not the start ack), so it stays <see cref="PrefillState.Downloading"/>
    /// for the duration of the real download.
    /// </summary>
    public PrefillState PrefillState { get; set; } = PrefillState.Idle;

    /// <summary>
    /// The latest live <see cref="PrefillProgress"/> snapshot broadcast for this session,
    /// retained so a client that connects/refreshes/reconnects mid-prefill can immediately
    /// re-hydrate the progress bar without waiting for the next periodic tick. Set on every
    /// progress tick in NotifyPrefillProgressAsync (before broadcasting) and cleared by the
    /// terminal funnel. Null when no prefill is in flight.
    /// </summary>
    public PrefillProgress? LastProgress { get; set; }

    /// <summary>
    /// UTC ticks of the last progress tick that transferred new bytes (i.e. where
    /// <see cref="TotalBytesTransferred"/> increased), or 0 when no prefill clock is set.
    /// Stored as a plain <see cref="long"/> field (not a <c>DateTime?</c> property) so the
    /// stall watchdog on the cleanup-timer thread and the socket/progress thread can read and
    /// write it atomically via <see cref="System.Threading.Volatile"/>; a torn read of the
    /// multi-field <c>DateTime?</c> struct could otherwise mis-fire the watchdog. Seeded when a
    /// prefill starts, advanced only on byte-increasing ticks, and reset to 0 by the terminal
    /// funnel so a completed/failed session is never re-flagged.
    /// </summary>
    public long LastProgressTicksUtc;

    /// <summary>
    /// Snapshot of <see cref="TotalBytesTransferred"/> at the last byte-increasing tick.
    /// Used by the stall watchdog to detect zero-progress prefill sessions.
    /// </summary>
    public long LastProgressBytes { get; set; }

    /// <summary>
    /// Per-run idempotency guard for the terminal funnel. 0 = not yet terminal, 1 = terminal
    /// already fired. Reset to 0 at the start of each prefill run; flipped once via
    /// <see cref="System.Threading.Interlocked.CompareExchange(ref int, int, int)"/> so a
    /// socket-death + late daemon terminal event can never double-fire the terminal transition.
    /// </summary>
    public int TerminalCompletedFlag;

    public DateTime? PrefillStartedAt { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// True when this session is a guest/temporary container subject to the manager-enforced
    /// <c>GuestPrefillMaxLifetimeHours</c> cap. When true, <see cref="ExpiresAt"/> was stamped to
    /// createdAt + the configured cap and the session is reaped by <c>CleanupExpiredSessions</c>
    /// at that expiry. Admin/persistent sessions leave this false and keep the standard timeout.
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
    /// The Steam username (set when user provides username credential).
    /// Used for ban display and admin visibility.
    /// </summary>
    public string? SteamUsername { get; set; }

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
    /// Current prefill progress info for admin visibility
    /// </summary>
    public string? CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }

    /// <summary>
    /// Previous app ID for tracking history transitions
    /// </summary>
    public string? PreviousAppId { get; set; }
    public string? PreviousAppName { get; set; }

    /// <summary>
    /// Total bytes transferred during this session (cumulative across all games)
    /// This includes completed games + current game progress for real-time display
    /// </summary>
    public long TotalBytesTransferred { get; set; }
    
    /// <summary>
    /// Bytes from completed games (used to calculate TotalBytesTransferred)
    /// </summary>
    public long CompletedBytesTransferred { get; set; }

    /// <summary>
    /// Current app's bytes downloaded (tracked before transition to record final values)
    /// </summary>
    public long CurrentBytesDownloaded { get; set; }
    public long CurrentTotalBytes { get; set; }

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

    /// <summary>
    /// Last prefill completion result - used for background completion detection
    /// when client was disconnected during prefill
    /// </summary>
    public DateTime? LastPrefillCompletedAt { get; set; }
    public int? LastPrefillDurationSeconds { get; set; }
    public string? LastPrefillStatus { get; set; }

    public IDaemonClient Client { get; set; } = null!;

    /// <summary>
    /// Path to the daemon's Unix socket.
    /// </summary>
    public string? SocketPath { get; set; }
    public HashSet<string> SubscribedConnections { get; } = new();
    public CancellationTokenSource CancellationTokenSource { get; } = new();
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
