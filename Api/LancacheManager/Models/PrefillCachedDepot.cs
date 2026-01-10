using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Models;

/// <summary>
/// Tracks which Steam depots have been successfully prefilled.
/// This is used to skip re-downloading games that are already cached
/// and to prevent multiple users from downloading the same games.
/// </summary>
[Index(nameof(AppId))]
[Index(nameof(DepotId), nameof(ManifestId), IsUnique = true)]
public class PrefillCachedDepot
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Steam App ID of the game
    /// </summary>
    public uint AppId { get; set; }

    /// <summary>
    /// Steam Depot ID (games have multiple depots)
    /// </summary>
    public uint DepotId { get; set; }

    /// <summary>
    /// Manifest ID - unique identifier for the depot version.
    /// If this changes on Steam's end, the game needs re-downloading.
    /// </summary>
    public ulong ManifestId { get; set; }

    /// <summary>
    /// Name of the game (for display purposes)
    /// </summary>
    [MaxLength(200)]
    public string? AppName { get; set; }

    /// <summary>
    /// When this depot was cached
    /// </summary>
    public DateTime CachedAtUtc { get; set; }

    /// <summary>
    /// Who prefilled this (Steam username or session ID)
    /// </summary>
    [MaxLength(100)]
    public string? CachedBy { get; set; }

    /// <summary>
    /// Total bytes in this depot (for display)
    /// </summary>
    public long TotalBytes { get; set; }
}
