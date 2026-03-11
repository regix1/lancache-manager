using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Maps an Epic Games CDN URL pattern to a game.
/// Used to identify which game a cached download belongs to by matching
/// the org/build path segments in Epic CDN URLs.
/// </summary>
public class EpicCdnPattern
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// The Epic Games app ID (links to EpicGameMapping.AppId)
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// The game name at time of discovery
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The CDN hostname (e.g., "epicgames-download1.akamaized.net")
    /// </summary>
    public string CdnHost { get; set; } = string.Empty;

    /// <summary>
    /// The chunk base URL path from the manifest.
    /// e.g., "/Builds/Org/o-sdmmvl8pftrkwfy86bjp286kuwhnsq/0293ace10f2a46b481f736f3e06c491e/default/"
    /// </summary>
    public string ChunkBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// When this CDN pattern was first discovered.
    /// </summary>
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this pattern was last confirmed still valid (build version may change).
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}
