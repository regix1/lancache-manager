using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Prefill progress update from the daemon.
/// This class is used for SignalR serialization to the frontend - do NOT add JsonPropertyName attributes.
/// </summary>
public class PrefillProgress
{
    public string State { get; set; } = PrefillProgressState.Idle.ToWireString();
    public string? Message { get; set; }
    public string? CurrentAppId { get; set; }
    public string? CurrentAppName { get; set; }
    public long TotalBytes { get; set; }
    public long BytesDownloaded { get; set; }
    public double PercentComplete { get; set; }
    public double BytesPerSecond { get; set; }
    public double ElapsedSeconds { get; set; }
    public string? Result { get; set; }
    public string? ErrorMessage { get; set; }
    public int TotalApps { get; set; }
    public int UpdatedApps { get; set; }
    public int AlreadyUpToDate { get; set; }
    public int FailedApps { get; set; }
    public long TotalBytesTransferred { get; set; }
    public double TotalTimeSeconds { get; set; }
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Depot manifest info for cache tracking - sent with app_completed events.
    /// </summary>
    public List<DepotManifestProgressInfo>? Depots { get; set; }
}

/// <summary>
/// Thrown when a prefill is requested for a session that already has a prefill in flight.
/// Mapped to HTTP 409 (Conflict) by the controller and to the daemon's
/// "A prefill is already in progress" rejection.
/// </summary>
public class PrefillAlreadyRunningException : InvalidOperationException
{
    public PrefillAlreadyRunningException(string message) : base(message)
    {
    }
}

/// <summary>
/// Depot manifest info for cache tracking in progress updates.
/// </summary>
public class DepotManifestProgressInfo
{
    public long DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public long TotalBytes { get; set; }
}
