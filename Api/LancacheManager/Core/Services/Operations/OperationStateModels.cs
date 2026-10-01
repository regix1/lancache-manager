using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Core.Services;

public class OperationState
{
    public string Key { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    [JsonPropertyName("Data")]
    public JsonElement? Fields { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public string? Status { get; set; }
    public string? Message { get; set; }

    /// <summary>
    /// Returns the stored fields in their legacy dictionary shape.
    /// </summary>
    public Dictionary<string, object> GetDictionary()
    {
        if (Fields == null
            || Fields.Value.ValueKind == JsonValueKind.Null
            || Fields.Value.ValueKind == JsonValueKind.Undefined)
            return new Dictionary<string, object>();

        if (Fields.Value.ValueKind == JsonValueKind.Object)
        {
            var dict = new Dictionary<string, object>();
            foreach (var prop in Fields.Value.EnumerateObject())
            {
                dict[prop.Name] = ConvertJsonElementToObject(prop.Value);
            }
            return dict;
        }

        return new Dictionary<string, object>();
    }

    private static object ConvertJsonElementToObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null!,
            JsonValueKind.Object => element.Deserialize<Dictionary<string, object>>() ?? new Dictionary<string, object>(),
            JsonValueKind.Array => element.Deserialize<List<object>>() ?? new List<object>(),
            _ => element.GetRawText()
        };
    }
}
