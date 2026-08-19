using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

/// <summary>
/// Represents an Epic Games game discovered through user authentication.
/// Games accumulate over time from all users who log in.
/// </summary>
public class EpicGameMapping
{
    [Key]
    public long Id { get; set; }

    /// <summary>
    /// The Epic Games app ID (string, e.g., "Fortnite", "9d2d0eb64d5c44529cece33fe2a46482").
    /// This is the "appName" field from Epic's asset API.
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// The display name of the game (e.g., "Fortnite", "Rocket League").
    /// May be updated if a newer login returns a different name (game renamed).
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// When this game was first discovered by any user.
    /// </summary>
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this game's information was last confirmed/updated by a user login.
    /// </summary>
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Anonymized hash of the user who first discovered this game.
    /// SHA-256 hash of the session ID, not reversible to any user identity.
    /// </summary>
    public string DiscoveredByHash { get; set; } = string.Empty;

    /// <summary>
    /// How this mapping was discovered.
    /// Values: "user-login", "prefill", "import", "manual"
    /// </summary>
    public string Source { get; set; } = "user-login";

    /// <summary>
    /// URL to the game's image art from Epic's keyImages API.
    /// Used for display in the game detection UI.
    /// </summary>
    public string? ImageUrl { get; set; }
}
