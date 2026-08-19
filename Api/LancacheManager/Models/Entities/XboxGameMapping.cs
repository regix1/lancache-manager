using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Represents an Xbox / Microsoft Store game discovered through user authentication.
/// Games accumulate over time from all users who log in (a SHARED catalog, like Epic).
/// </summary>
public class XboxGameMapping
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The Xbox / Microsoft Store ProductId (the "bigId", e.g. "9NBLGGH537BL").
    /// Used to fetch DisplayCatalog art and to map a cached download back to a title.
    /// </summary>
    public string ProductId { get; set; } = string.Empty;

    /// <summary>
    /// The display name of the game (e.g., "Minecraft", "Forza Horizon 5").
    /// May be updated if a newer login returns a different name (game renamed).
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// When this game was first discovered by any user.
    /// </summary>
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this game's information was last confirmed/updated by a user login.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// URL to the game's banner art from the DisplayCatalog (or a Steam-quality fallback).
    /// Used for display in the game detection UI.
    /// </summary>
    public string? ImageUrl { get; set; }
}
