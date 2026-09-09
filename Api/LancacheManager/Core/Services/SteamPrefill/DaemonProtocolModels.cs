using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Per-transport wording used in a daemon client's connection diagnostics: <c>Connection</c> names the
/// live link ("socket", "TCP connection") and <c>Endpoint</c> names what it was dialled against
/// ("socket", "TCP endpoint").
/// </summary>
/// <param name="Connection">Name of the live link, used in disconnect messages.</param>
/// <param name="Endpoint">Name of the dialled endpoint, used in connect messages.</param>
public sealed record DaemonTransportLabels(string Connection, string Endpoint);

/// <summary>
/// Progress update from prefill operation via socket.
/// Matches daemon's PrefillProgressUpdate class property names.
/// </summary>
public class SocketPrefillProgress
{
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("currentAppId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? CurrentAppId { get; set; }

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

    [JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

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

    [JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// Depot manifest info for cache tracking - sent with app_completed events.
    /// </summary>
    [JsonPropertyName("depots")]
    public List<SocketDepotManifestInfo>? Depots { get; set; }
}

/// <summary>
/// Depot manifest info from socket progress updates.
/// </summary>
public class SocketDepotManifestInfo
{
    [JsonPropertyName("depotId")]
    public long DepotId { get; set; }

    [JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}

/// <summary>
/// Cleartext payload encrypted and sent to the daemon for non-interactive auto-login.
/// Matches the daemon's expected <c>{username, refreshToken}</c> shape.
/// </summary>
public class AutoLoginPayload
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Cleartext payload encrypted and sent to the Epic daemon for non-interactive auto-login.
/// Matches the daemon's expected <c>{refreshToken}</c> shape.
/// </summary>
public class EpicAutoLoginPayload
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}

/// <summary>
/// Cleartext payload encrypted and sent to the Xbox daemon for non-interactive auto-login.
/// The daemon creates and persists its own device key.
/// </summary>
public class XboxAutoLoginPayload
{
    [JsonPropertyName("refreshToken")]
    public string RefreshToken { get; set; } = string.Empty;
}
