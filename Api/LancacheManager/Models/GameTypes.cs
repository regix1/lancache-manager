using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// JSON converter that reads both JSON numbers and JSON strings as string.
/// Needed because Steam returns numeric appIds (730) while Epic returns string appIds ("abc123").
/// Shared between Steam and Epic game types.
/// </summary>
public class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetInt64().ToString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Unexpected token type: {reader.TokenType}")
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        // Write numeric strings as JSON numbers for backward compatibility with daemons
        if (long.TryParse(value, out var numericValue))
            writer.WriteNumberValue(numericValue);
        else
            writer.WriteStringValue(value);
    }
}

public class OwnedGame
{
    [JsonPropertyName("appId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string? ImageUrl { get; set; }
}

public class CdnInfo
{
    [JsonPropertyName("appId")]
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("cdnHost")]
    public string CdnHost { get; set; } = string.Empty;

    [JsonPropertyName("chunkBaseUrl")]
    public string ChunkBaseUrl { get; set; } = string.Empty;
}

public class CdnInfoResult
{
    [JsonPropertyName("apps")]
    public List<CdnInfo> Apps { get; set; } = new();

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
