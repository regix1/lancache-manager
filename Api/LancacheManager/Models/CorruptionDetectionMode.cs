using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Corruption detection mode used from scan input through persisted evidence and removal.
/// Serialized as canonical snake_case strings on the wire.
/// </summary>
[JsonConverter(typeof(CorruptionDetectionModeJsonConverter))]
public enum CorruptionDetectionMode
{
    /// <summary>Fallback for unrecognized wire values (never used as a valid input).</summary>
    Unknown,

    /// <summary>Review bounded MISS evidence without requiring a cache file on disk.</summary>
    LogsOnly,

    /// <summary>Require the exact MISS-evidence cache slice to exist on disk.</summary>
    CacheAndLogs,

    /// <summary>Detect corruption by flagging chunks that have been re-downloaded.</summary>
    Redownload
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
        CorruptionDetectionMode.LogsOnly => "logs_only",
        CorruptionDetectionMode.CacheAndLogs => "cache_and_logs",
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
            "logs_only" => CorruptionDetectionMode.LogsOnly,
            "logsonly" => CorruptionDetectionMode.LogsOnly,
            "cache_and_logs" => CorruptionDetectionMode.CacheAndLogs,
            "cacheandlogs" => CorruptionDetectionMode.CacheAndLogs,
            // Backward-compatible scan-input alias. Persisted scans always use
            // the canonical cache_and_logs value.
            "miss_count" => CorruptionDetectionMode.CacheAndLogs,
            "misscount" => CorruptionDetectionMode.CacheAndLogs,
            "redownload" => CorruptionDetectionMode.Redownload,
            _ => CorruptionDetectionMode.Unknown
        };
    }
}
