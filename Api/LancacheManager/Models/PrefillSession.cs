using Docker.DotNet;
using Docker.DotNet.Models;

namespace LancacheManager.Models;

/// <summary>
/// Represents an active Steam Prefill terminal session
/// </summary>
public class PrefillSession
{
    /// <summary>
    /// Unique session identifier
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// User/device ID that owns this session
    /// </summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Docker container ID
    /// </summary>
    public string ContainerId { get; set; } = string.Empty;

    /// <summary>
    /// Container name for display purposes
    /// </summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Current session status
    /// </summary>
    public PrefillSessionStatus Status { get; set; } = PrefillSessionStatus.Creating;

    /// <summary>
    /// When the session was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the session will automatically expire
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// When the session ended (if ended)
    /// </summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>
    /// Multiplexed stream for stdin/stdout communication with container
    /// </summary>
    public MultiplexedStream? Stream { get; set; }

    /// <summary>
    /// Cancellation token source for stopping the session
    /// </summary>
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();

    /// <summary>
    /// SignalR connection IDs subscribed to this session's output
    /// </summary>
    public HashSet<string> SubscribedConnections { get; set; } = new();

    /// <summary>
    /// Current Steam authentication state within the container
    /// </summary>
    public SteamAuthState AuthState { get; set; } = SteamAuthState.NotAuthenticated;
}

/// <summary>
/// Steam authentication state within a prefill container
/// </summary>
public enum SteamAuthState
{
    /// <summary>
    /// Not yet authenticated to Steam
    /// </summary>
    NotAuthenticated,

    /// <summary>
    /// Container is prompting for Steam username/password
    /// </summary>
    CredentialsRequired,

    /// <summary>
    /// Container is prompting for 2FA code (Steam Guard)
    /// </summary>
    TwoFactorRequired,

    /// <summary>
    /// Container is prompting for email verification code
    /// </summary>
    EmailCodeRequired,

    /// <summary>
    /// Successfully authenticated to Steam
    /// </summary>
    Authenticated
}

public enum PrefillSessionStatus
{
    Creating,
    Active,
    Completed,
    Terminated,
    Error
}

/// <summary>
/// DTO for returning session info to the frontend (without internal stream details)
/// </summary>
public class PrefillSessionDto
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ContainerId { get; set; } = string.Empty;
    public string ContainerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int TimeRemainingSeconds { get; set; }
    public string AuthState { get; set; } = string.Empty;

    public static PrefillSessionDto FromSession(PrefillSession session)
    {
        return new PrefillSessionDto
        {
            Id = session.Id,
            UserId = session.UserId,
            ContainerId = session.ContainerId,
            ContainerName = session.ContainerName,
            Status = session.Status.ToString(),
            CreatedAt = session.CreatedAt,
            ExpiresAt = session.ExpiresAt,
            EndedAt = session.EndedAt,
            TimeRemainingSeconds = (int)Math.Max(0, (session.ExpiresAt - DateTime.UtcNow).TotalSeconds),
            AuthState = session.AuthState.ToString()
        };
    }
}
