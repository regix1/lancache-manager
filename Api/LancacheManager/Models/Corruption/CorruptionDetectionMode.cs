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

    /// <summary>Legacy v3 cache-and-logs mode. It is never interpreted as v4 evidence.</summary>
    CacheAndLogs,

    /// <summary>Repeated-MISS v4 evidence.</summary>
    RepeatedMiss,

    /// <summary>Structural nginx cache-file v4 evidence.</summary>
    Structural
}

/// <summary>
/// Serializes <see cref="CorruptionDetectionMode"/> as canonical snake_case strings
/// and accepts only canonical persistence values on deserialization. Unrecognized values deserialize to
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
        CorruptionDetectionMode.RepeatedMiss => "repeated_miss",
        CorruptionDetectionMode.Structural => "structural",
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
            "repeated_miss" => CorruptionDetectionMode.RepeatedMiss,
            "structural" => CorruptionDetectionMode.Structural,
            _ => CorruptionDetectionMode.Unknown
        };
    }

    public static CorruptionDetectionMode ToPersistenceMode(this CorruptionDetectionMethod method) => method switch
    {
        CorruptionDetectionMethod.RepeatedMiss => CorruptionDetectionMode.RepeatedMiss,
        CorruptionDetectionMethod.Structural => CorruptionDetectionMode.Structural,
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported corruption detection method")
    };

    public static CorruptionDetectionMethod ToDetectionMethod(this CorruptionDetectionMode mode) => mode switch
    {
        CorruptionDetectionMode.RepeatedMiss => CorruptionDetectionMethod.RepeatedMiss,
        CorruptionDetectionMode.Structural => CorruptionDetectionMethod.Structural,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Persistence mode is not a supported v4 method")
    };
}
