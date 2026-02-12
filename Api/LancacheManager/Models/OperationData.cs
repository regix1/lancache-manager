using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Data model for Steam PICS depot mapping progress
/// </summary>
public class SteamPicsProgress
{
    [JsonPropertyName("isProcessing")]
    public bool IsProcessing { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [JsonPropertyName("processedApps")]
    public int ProcessedApps { get; set; }

    [JsonPropertyName("totalBatches")]
    public int TotalBatches { get; set; }

    [JsonPropertyName("processedBatches")]
    public int ProcessedBatches { get; set; }

    [JsonPropertyName("progressPercent")]
    public double ProgressPercent { get; set; }

    [JsonPropertyName("depotMappingsFound")]
    public int DepotMappingsFound { get; set; }

    [JsonPropertyName("depotMappingsFoundInSession")]
    public int DepotMappingsFoundInSession { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    [JsonPropertyName("lastCrawlTime")]
    public DateTime? LastCrawlTime { get; set; }

    [JsonPropertyName("nextCrawlIn")]
    public double NextCrawlIn { get; set; }

    [JsonPropertyName("crawlIntervalHours")]
    public double CrawlIntervalHours { get; set; }

    [JsonPropertyName("crawlIncrementalMode")]
    public object CrawlIncrementalMode { get; set; } = true;

    [JsonPropertyName("lastScanWasForced")]
    public bool LastScanWasForced { get; set; }

    [JsonPropertyName("automaticScanSkipped")]
    public bool AutomaticScanSkipped { get; set; }

    [JsonPropertyName("isConnected")]
    public bool IsConnected { get; set; }

    [JsonPropertyName("isLoggedOn")]
    public bool IsLoggedOn { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [JsonPropertyName("isWebApiAvailable")]
    public bool IsWebApiAvailable { get; set; }

    [JsonPropertyName("operationId")]
    public string? OperationId { get; set; }
}

/// <summary>
/// Result of checking if incremental PICS update is viable
/// </summary>
public class IncrementalViabilityCheck
{
    [JsonPropertyName("isViable")]
    public bool IsViable { get; set; }

    [JsonPropertyName("lastChangeNumber")]
    public uint LastChangeNumber { get; set; }

    [JsonPropertyName("currentChangeNumber")]
    public uint CurrentChangeNumber { get; set; }

    [JsonPropertyName("changeGap")]
    public uint ChangeGap { get; set; }

    [JsonPropertyName("isLargeGap")]
    public bool IsLargeGap { get; set; }

    [JsonPropertyName("willTriggerFullScan")]
    public bool WillTriggerFullScan { get; set; }

    [JsonPropertyName("estimatedAppsToScan")]
    public int EstimatedAppsToScan { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Theme metadata information
/// </summary>
public class ThemeInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("isDefault")]
    public bool IsDefault { get; set; }

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;
}
