namespace LancacheManager.Models;

/// <summary>
/// Whether a cache scan would be refused right now, and the reason if so. Read by the controls
/// that start scans so a button is never offered for an action the server is about to refuse.
/// </summary>
public class CacheScanBlockedResponse
{
    /// <summary>True when a scan requested now would be refused.</summary>
    public bool Blocked { get; set; }

    /// <summary>
    /// The sentence explaining the refusal, or null when a scan may start. It distinguishes a
    /// download in progress from the tracker not having reported yet.
    /// </summary>
    public string? Reason { get; set; }
}
