using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Polling / refresh cadence selected by the user.
/// Serialized as UPPER-CASE strings on the wire ("LIVE", "ULTRA", "REALTIME",
/// "STANDARD", "RELAXED", "SLOW") to preserve the pre-existing JSON contract
/// with the frontend and persisted state files (state.json, UserPreferences table).
/// </summary>
[JsonConverter(typeof(RefreshRateJsonConverter))]
public enum RefreshRate
{
    Live,
    Ultra,
    Realtime,
    Standard,
    Relaxed,
    Slow
}

/// <summary>
/// Serializes <see cref="RefreshRate"/> as UPPER-CASE strings
/// ("LIVE", "ULTRA", "REALTIME", "STANDARD", "RELAXED", "SLOW") and accepts
/// any casing on deserialization to match the pre-existing wire contract.
/// </summary>
internal sealed class RefreshRateJsonConverter : JsonConverter<RefreshRate>
{
    public override RefreshRate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            var parsed = RefreshRateExtensions.TryParseWire(value);
            if (parsed.HasValue)
            {
                return parsed.Value;
            }

            throw new JsonException($"Unknown RefreshRate value: '{value}'");
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing RefreshRate");
    }

    public override void Write(Utf8JsonWriter writer, RefreshRate value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="RefreshRate"/>.
/// </summary>
public static class RefreshRateExtensions
{
    /// <summary>
    /// Returns the canonical UPPER-CASE wire value ("LIVE", "ULTRA", "REALTIME",
    /// "STANDARD", "RELAXED", "SLOW").
    /// </summary>
    public static string ToWireString(this RefreshRate rate) => rate switch
    {
        RefreshRate.Live => "LIVE",
        RefreshRate.Ultra => "ULTRA",
        RefreshRate.Realtime => "REALTIME",
        RefreshRate.Standard => "STANDARD",
        RefreshRate.Relaxed => "RELAXED",
        RefreshRate.Slow => "SLOW",
        _ => rate.ToString().ToUpperInvariant()
    };

    /// <summary>
    /// Parses a legacy / wire string value into a <see cref="RefreshRate"/>. Case-insensitive.
    /// Returns <c>null</c> if the value is null, whitespace, or unrecognised.
    /// </summary>
    public static RefreshRate? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<RefreshRate>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
