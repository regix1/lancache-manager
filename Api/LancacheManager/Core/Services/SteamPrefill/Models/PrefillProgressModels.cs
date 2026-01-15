using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Prefill progress update from the daemon.
/// This class is used for SignalR serialization to the frontend - do NOT add JsonPropertyName attributes.
/// </summary>
public class PrefillProgress
{
    public string State { get; set; } = "idle";
    public string? Message { get; set; }
    public uint CurrentAppId { get; set; }
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

    /// <summary>
    /// Creates a PrefillProgress from the daemon's JSON format (snake_case).
    /// Internal because it uses the internal DaemonPrefillProgressDto type.
    /// </summary>
    internal static PrefillProgress FromDaemonJson(DaemonPrefillProgressDto dto)
    {
        return new PrefillProgress
        {
            State = dto.State ?? "idle",
            Message = dto.Message,
            CurrentAppId = dto.CurrentAppId,
            CurrentAppName = dto.CurrentAppName,
            TotalBytes = dto.TotalBytes,
            BytesDownloaded = dto.BytesDownloaded,
            PercentComplete = dto.PercentComplete,
            BytesPerSecond = dto.BytesPerSecond,
            ElapsedSeconds = dto.ElapsedSeconds,
            Result = dto.Result,
            ErrorMessage = dto.ErrorMessage,
            TotalApps = dto.TotalApps,
            UpdatedApps = dto.UpdatedApps,
            AlreadyUpToDate = dto.AlreadyUpToDate,
            FailedApps = dto.FailedApps,
            TotalBytesTransferred = dto.TotalBytesTransferred,
            TotalTimeSeconds = dto.TotalTimeSeconds,
            UpdatedAt = dto.UpdatedAt,
            Depots = dto.Depots?.Select(d => new DepotManifestProgressInfo
            {
                DepotId = d.DepotId,
                ManifestId = d.ManifestId,
                TotalBytes = d.TotalBytes
            }).ToList()
        };
    }
}

/// <summary>
/// Depot manifest info for cache tracking in progress updates.
/// </summary>
public class DepotManifestProgressInfo
{
    public uint DepotId { get; set; }
    public ulong ManifestId { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>
/// Internal DTO for deserializing the daemon's prefill_progress.json file.
/// Uses JsonPropertyName attributes to map camelCase JSON to PascalCase properties.
/// The daemon writes camelCase (e.g., currentAppId, bytesDownloaded, totalBytes).
/// This class is NOT used for SignalR - only for file deserialization.
/// </summary>
internal class DaemonPrefillProgressDto
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("currentAppId")]
    public uint CurrentAppId { get; set; }

    [JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds { get; set; }

    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("depots")]
    public List<DaemonDepotManifestDto>? Depots { get; set; }
}

/// <summary>
/// Internal DTO for deserializing depot manifest info from the daemon.
/// </summary>
internal class DaemonDepotManifestDto
{
    [JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}
