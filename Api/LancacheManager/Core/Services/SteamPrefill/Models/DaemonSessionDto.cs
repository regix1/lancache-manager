namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// DTO for daemon session information
/// </summary>
public class DaemonSessionDto
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public string AuthState { get; set; } = string.Empty;
    public bool IsPrefilling { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int TimeRemainingSeconds { get; set; }

    // Client info for admin visibility
    public string? IpAddress { get; set; }
    public string? OperatingSystem { get; set; }
    public string? Browser { get; set; }
    public DateTime LastSeenAt { get; set; }
    public string? SteamUsername { get; set; }
    public string Platform { get; set; } = "Steam";
    public string? Username { get; set; }

    // Current prefill progress info for admin visibility
    public string? CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }

    /// <summary>
    /// Total bytes transferred during this session (cumulative across all games)
    /// </summary>
    public long TotalBytesTransferred { get; set; }

    /// <summary>
    /// Network diagnostics results (internet connectivity and DNS resolution tests)
    /// </summary>
    public NetworkDiagnostics? NetworkDiagnostics { get; set; }

    /// <summary>
    /// Last prefill completion result - for background completion detection
    /// </summary>
    public DateTime? LastPrefillCompletedAt { get; set; }
    public int? LastPrefillDurationSeconds { get; set; }
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
            EndedAt = session.EndedAt,
            ExpiresAt = session.ExpiresAt,
            TimeRemainingSeconds = Math.Max(0, (int)(session.ExpiresAt - DateTime.UtcNow).TotalSeconds),
            IpAddress = session.IpAddress,
            OperatingSystem = session.OperatingSystem,
            Browser = session.Browser,
            LastSeenAt = session.LastSeenAt,
            SteamUsername = session.SteamUsername,
            Platform = session.Platform,
            Username = session.Username ?? session.SteamUsername,
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
