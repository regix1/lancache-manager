namespace LancacheManager.Models;

public class SteamApp
{
    public string AppId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}