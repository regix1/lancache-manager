using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Metadata for game detection operations stored in OperationInfo.Metadata.
/// Extends UnifiedOperationTracker with game-detection-specific metrics.
/// </summary>
public class GameDetectionMetrics
{
    /// <summary>
    /// Type of scan performed - <see cref="DetectionScanType.Full"/> or <see cref="DetectionScanType.Incremental"/>.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter<DetectionScanType>))]
    public DetectionScanType ScanType { get; set; } = DetectionScanType.Incremental;

    /// <summary>
    /// List of detected game caches.
    /// </summary>
    public List<GameCacheInfo> Games { get; set; } = new();

    /// <summary>
    /// List of detected service caches.
    /// </summary>
    public List<ServiceCacheInfo> Services { get; set; } = new();

    /// <summary>
    /// Total number of games detected during the scan.
    /// </summary>
    public int TotalGamesDetected { get; set; }

    /// <summary>
    /// Total number of services detected during the scan.
    /// </summary>
    public int TotalServicesDetected { get; set; }

    /// <summary>
    /// Error message if the detection operation failed.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// When this detection was started (or when cached results were last detected).
    /// Used by GetCachedDetectionAsync to carry the last detection timestamp.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// i18n interpolation values for the current/completion stage key (e.g. newGamesCount).
    /// Persisted on the operation so recovery endpoints can translate stage messages.
    /// </summary>
    public Dictionary<string, object?>? CompletionContext { get; set; }

    /// <summary>
    /// i18n interpolation values for the LATEST progress stage key (e.g. processed/total for
    /// signalr.gameDetect.services.progress). The unified tracker stores only the stage KEY in
    /// OperationInfo.Message, so the /api/games/detect/active recovery endpoint reads this to
    /// interpolate placeholder-bearing progress keys mid-run. Updated on every progress tick.
    /// </summary>
    public Dictionary<string, object?>? CurrentContext { get; set; }

    /// <summary>
    /// Run-stable display flag captured when the operation was registered. Lifecycle events are
    /// always emitted so recovery works, but a silent automatic run leaves this false so the
    /// /api/games/detect/active recovery path can decline to resurrect a card on page reload.
    /// </summary>
    public bool ShowNotification { get; set; } = true;
}
