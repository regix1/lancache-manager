using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Maps Blizzard CDN chunks (archive index + offset) to game files
/// Similar to SteamDepotMapping but for Blizzard's TACT system
/// </summary>
public class BlizzardChunkMapping
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Blizzard product code (e.g., "wow", "pro", "hs", "s1", "s2", "hero", "d3", "w3")
    /// </summary>
    public string Product { get; set; } = string.Empty;

    /// <summary>
    /// Archive index (which archive file this chunk is in)
    /// </summary>
    public int ArchiveIndex { get; set; }

    /// <summary>
    /// Byte offset within the archive (typically 4KB-aligned)
    /// </summary>
    public uint ByteOffset { get; set; }

    /// <summary>
    /// The game file path this chunk belongs to (e.g., "Data/models/character.m2")
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public uint FileSize { get; set; }

    /// <summary>
    /// MD5 hash of the file content (EKey)
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>
    /// Human-readable game name (e.g., "World of Warcraft", "Overwatch")
    /// </summary>
    public string? GameName { get; set; }

    /// <summary>
    /// URL to game image/icon
    /// </summary>
    public string? GameImageUrl { get; set; }

    /// <summary>
    /// When this mapping was discovered
    /// </summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// How this mapping was discovered (e.g., "chunk-mapper", "manual")
    /// </summary>
    public string Source { get; set; } = "chunk-mapper";
}
