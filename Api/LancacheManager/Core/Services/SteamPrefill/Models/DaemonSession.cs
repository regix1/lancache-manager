namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Represents a Steam Prefill daemon session
/// </summary>
public class DaemonSession
{
    public string Id { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string CommandsDir { get; init; } = string.Empty;
    public string ResponsesDir { get; init; } = string.Empty;
    public DaemonSessionStatus Status { get; set; } = DaemonSessionStatus.Active;
    public string? ErrorMessage { get; set; }
    public DaemonAuthState AuthState { get; set; } = DaemonAuthState.NotAuthenticated;
    public bool IsPrefilling { get; set; }
    public DateTime? PrefillStartedAt { get; set; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; init; }

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
