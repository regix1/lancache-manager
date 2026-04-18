using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Import source type for data migration operations.
/// Serialized as lowercase strings on the wire ("develancache", "lancache-manager")
/// to preserve the pre-existing JSON contract with the frontend.
/// </summary>
[JsonConverter(typeof(ImportTypeJsonConverter))]
public enum ImportType
{
    Develancache,
    LancacheManager
}

/// <summary>
/// Serializes <see cref="ImportType"/> as its canonical wire string
/// ("develancache" / "lancache-manager") and accepts any casing on deserialization.
/// </summary>
internal sealed class ImportTypeJsonConverter : JsonConverter<ImportType>
{
    public override ImportType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        return value?.ToLowerInvariant() switch
        {
            "develancache" => ImportType.Develancache,
            "lancache-manager" => ImportType.LancacheManager,
            _ => throw new JsonException($"Unknown ImportType value: '{value}'")
        };
    }

    public override void Write(Utf8JsonWriter writer, ImportType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="ImportType"/>.
/// </summary>
public static class ImportTypeExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("develancache", "lancache-manager").
    /// </summary>
    public static string ToWireString(this ImportType importType) => importType switch
    {
        ImportType.Develancache => "develancache",
        ImportType.LancacheManager => "lancache-manager",
        _ => importType.ToString().ToLowerInvariant()
    };
}
