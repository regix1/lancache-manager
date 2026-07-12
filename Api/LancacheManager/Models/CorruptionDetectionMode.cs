using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Internal corruption detection mode retained for the existing persistence column.
/// </summary>
[JsonConverter(typeof(CorruptionDetectionModeJsonConverter))]
public enum CorruptionDetectionMode
{
    /// <summary>Fallback for unrecognized wire values (never used as a valid input).</summary>
    Unknown,

    /// <summary>Require the exact MISS-evidence cache slice to exist on disk.</summary>
    CacheAndLogs
}

/// <summary>
/// Serializes <see cref="CorruptionDetectionMode"/> as canonical snake_case strings
/// and accepts any casing on deserialization. Unrecognized values deserialize to
/// <see cref="CorruptionDetectionMode.Unknown"/>.
/// </summary>
internal sealed class CorruptionDetectionModeJsonConverter : JsonConverter<CorruptionDetectionMode>
{
    public override CorruptionDetectionMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return CorruptionDetectionMode.Unknown;
        }

        var value = reader.GetString();
        return CorruptionDetectionModeExtensions.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, CorruptionDetectionMode value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="CorruptionDetectionMode"/>.
/// </summary>
public static class CorruptionDetectionModeExtensions
{
    /// <summary>
    /// Returns the canonical snake_case wire value.
    /// </summary>
    public static string ToWireString(this CorruptionDetectionMode mode) => mode switch
    {
        CorruptionDetectionMode.CacheAndLogs => "cache_and_logs",
        CorruptionDetectionMode.Unknown => "unknown",
        _ => "unknown"
    };

    /// <summary>
    /// Parses a wire value into a <see cref="CorruptionDetectionMode"/>. Returns
    /// <see cref="CorruptionDetectionMode.Unknown"/> for null / unrecognized input.
    /// </summary>
    public static CorruptionDetectionMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CorruptionDetectionMode.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "cache_and_logs" => CorruptionDetectionMode.CacheAndLogs,
            "cacheandlogs" => CorruptionDetectionMode.CacheAndLogs,
            _ => CorruptionDetectionMode.Unknown
        };
    }
}
