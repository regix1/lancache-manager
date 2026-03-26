using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class SteamDepotMapping
{
    [Key]
    public long Id { get; set; }

    public long DepotId { get; set; }

    /// <summary>
    /// The name of the depot from PICS data (e.g., "Ubisoft Connect PC Client Content")
    /// Used as fallback display name for redistributable depots
    /// </summary>
    public string? DepotName { get; set; }

    public long AppId { get; set; }

    public string? AppName { get; set; }

    /// <summary>
    /// True if this app owns the depot (from depotfromapp PICS field)
    /// False if this app just references/uses the depot
    /// </summary>
    public bool IsOwner { get; set; } = false;

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public string Source { get; set; } = "observed";
}
