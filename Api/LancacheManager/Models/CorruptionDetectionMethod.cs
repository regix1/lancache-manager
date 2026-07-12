using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>Public corruption detection method used by the API and Rust v4 contract.</summary>
[JsonConverter(typeof(CorruptionDetectionMethodJsonConverter))]
public enum CorruptionDetectionMethod
{
    RepeatedMiss,
    Structural
}

/// <summary>Closed wire-format helpers for corruption detection methods.</summary>
public static class CorruptionDetectionMethodExtensions
{
    public static string ToWireString(this CorruptionDetectionMethod method) => method switch
    {
        CorruptionDetectionMethod.RepeatedMiss => "repeated_miss",
        CorruptionDetectionMethod.Structural => "structural",
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "Unsupported corruption detection method")
    };

    public static bool TryParseWire(string? value, out CorruptionDetectionMethod method)
    {
        method = value switch
        {
            "repeated_miss" => CorruptionDetectionMethod.RepeatedMiss,
            "structural" => CorruptionDetectionMethod.Structural,
            _ => default
        };
        return value is "repeated_miss" or "structural";
    }
}

internal sealed class CorruptionDetectionMethodJsonConverter : JsonConverter<CorruptionDetectionMethod>
{
    public override CorruptionDetectionMethod Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String
            || !CorruptionDetectionMethodExtensions.TryParseWire(reader.GetString(), out var method))
        {
            throw new JsonException("Unknown corruption detection method");
        }

        return method;
    }

    public override void Write(
        Utf8JsonWriter writer,
        CorruptionDetectionMethod value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToWireString());
}
