using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public class SteamDepotMapping
{
    [Key]
    public int Id { get; set; }
    
    public uint DepotId { get; set; }
    
    public uint AppId { get; set; }
    
    public string? AppName { get; set; }
    
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;
    
    public string Source { get; set; } = "observed";
    
    public int Confidence { get; set; } = 50;
}