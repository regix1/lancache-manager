using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Game cache detection scan type — controls how the detection service scans cached content.
/// Serialized as lowercase strings on the wire ("incremental", "full") to preserve the
/// pre-existing JSON contract with the frontend.
/// </summary>
[JsonConverter(typeof(DetectionScanTypeJsonConverter))]
public enum DetectionScanType
{
    /// <summary>Only re-scan newly cached content since the last detection run.</summary>
    Incremental,

    /// <summary>Re-scan every cached item from scratch.</summary>
    Full
}

/// <summary>
/// Serializes <see cref="DetectionScanType"/> as lowercase strings ("incremental" / "full")
/// and accepts any casing on deserialization to match the pre-existing wire contract.
/// </summary>
internal sealed class DetectionScanTypeJsonConverter : JsonStringEnumConverter<DetectionScanType>
{
    public DetectionScanTypeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="DetectionScanType"/>.
/// </summary>
public static class DetectionScanTypeExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("incremental", "full").
    /// </summary>
    public static string ToWireString(this DetectionScanType scanType) => scanType switch
    {
        DetectionScanType.Incremental => "incremental",
        DetectionScanType.Full => "full",
        _ => scanType.ToString().ToLowerInvariant()
    };
}
