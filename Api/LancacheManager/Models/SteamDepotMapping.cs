using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class SteamDepotMapping
{
    [Key]
    public int Id { get; set; }
    
    public uint DepotId { get; set; }
    
    public uint AppId { get; set; }
    
    public string? AppName { get; set; }

    /// <summary>
    /// True if this app owns the depot (from depotfromapp PICS field)
    /// False if this app just references/uses the depot
    /// </summary>
    public bool IsOwner { get; set; } = false;

    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    public string Source { get; set; } = "observed";
}