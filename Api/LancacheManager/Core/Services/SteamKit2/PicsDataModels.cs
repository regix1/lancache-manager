namespace LancacheManager.Core.Services.SteamKit2;

/// <summary>
/// Root structure for PICS JSON data
/// </summary>
public class PicsJsonData
{
    public PicsMetadata? Metadata { get; set; }
    public Dictionary<string, PicsDepotMapping>? DepotMappings { get; set; }
}

/// <summary>
/// Metadata for PICS JSON file
/// </summary>
public class PicsMetadata
{
    public DateTime LastUpdated { get; set; }
    public int TotalMappings { get; set; }
    public string Version { get; set; } = "1.0";
    public DateTime NextUpdateDue { get; set; }
    public uint LastChangeNumber { get; set; }   // PICS changelist position used for incremental updates
}

/// <summary>
/// Depot mapping data in JSON format
/// </summary>
public class PicsDepotMapping
{
    public uint? OwnerId { get; set; }  // The app that owns this depot (from depotfromapp PICS field)
    public string? DepotName { get; set; }  // Name of the depot from PICS (e.g., "Ubisoft Connect PC Client Content")
    public List<uint>? AppIds { get; set; }
    public List<string>? AppNames { get; set; }
    public string Source { get; set; } = "SteamKit2-PICS";
    public DateTime DiscoveredAt { get; set; }
}
