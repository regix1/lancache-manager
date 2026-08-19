namespace LancacheManager.Models;

/// <summary>
/// Metadata for eviction removal operations stored in OperationInfo.Metadata.
/// Captures scope/key so the active-removals endpoint can reconstruct the notification
/// context on page refresh (recovery path).
/// </summary>
public class EvictionRemovalMetadata
{
    /// <summary>
    /// The eviction scope: "steam", "epic", or "service".
    /// Null means a bulk (all-evicted) removal triggered by the reconciliation scan.
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// The entity key within the scope:
    /// - steam: gameAppId as string (e.g. "480")
    /// - epic:  epicAppId (e.g. "fn")
    /// - service: service name (e.g. "steam")
    /// - null: bulk removal (no specific entity)
    /// </summary>
    public string? Key { get; set; }

    /// <summary>
    /// Optional resolved game name for display in the frontend notification bar.
    /// Populated for steam/epic scopes when a CachedGameDetection row exists.
    /// </summary>
    public string? GameName { get; set; }
}
