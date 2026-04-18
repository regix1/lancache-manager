using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Corruption detection mode — controls which detection strategy the corruption service uses.
/// Serialized as snake_case strings on the wire ("miss_count", "redownload") to preserve the
/// pre-existing JSON / query-string contract with the frontend and CacheController.
/// </summary>
[JsonConverter(typeof(CorruptionDetectionModeJsonConverter))]
public enum CorruptionDetectionMode
{
    /// <summary>Fallback for unrecognized wire values (never used as a valid input).</summary>
    Unknown,

    /// <summary>Detect corruption by counting repeated cache misses for the same chunk.</summary>
    MissCount,

    /// <summary>Detect corruption by flagging chunks that have been re-downloaded.</summary>
    Redownload
}

/// <summary>
/// Serializes <see cref="CorruptionDetectionMode"/> as snake_case strings ("miss_count", "redownload")
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
    /// Returns the canonical snake_case wire value ("miss_count", "redownload", "unknown").
    /// </summary>
    public static string ToWireString(this CorruptionDetectionMode mode) => mode switch
    {
        CorruptionDetectionMode.MissCount => "miss_count",
        CorruptionDetectionMode.Redownload => "redownload",
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
            "miss_count" => CorruptionDetectionMode.MissCount,
            "misscount" => CorruptionDetectionMode.MissCount,
            "redownload" => CorruptionDetectionMode.Redownload,
            _ => CorruptionDetectionMode.Unknown
        };
    }
}
