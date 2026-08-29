using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// The snake_case progress fields every Rust processor's progress JSON carries (percent, status,
/// stage key, context), shared by the log-processor and log-removal progress mirrors. The cache
/// removal binaries write camelCase progress files and keep their own DTOs.
/// </summary>
public abstract class RustProgressBase
{
    [JsonPropertyName("percent_complete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stage_key")]
    public string StageKey { get; set; } = string.Empty;

    [JsonPropertyName("context")]
    public Dictionary<string, object?> Context { get; set; } = new();
}
