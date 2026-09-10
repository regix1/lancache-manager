using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Progress data from the Rust eviction scan binary (read from progress JSON file)
/// </summary>
internal class EvictionScanProgressData
{
    public OperationStatus Status { get; set; } = OperationStatus.Pending;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string StageKey { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;
    public double PercentComplete { get; set; }
    public int Processed { get; set; }
    public int TotalEstimate { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }
}

/// <summary>
/// Result from the Rust eviction scan binary (parsed from stdout JSON)
/// </summary>
internal class EvictionScanResult
{
    public bool Success { get; set; }
    public int Processed { get; set; }
    public int Evicted { get; set; }
    public int UnEvicted { get; set; }
    public int FilesOnDisk { get; set; }
    public string? Error { get; set; }
}
