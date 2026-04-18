using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Known operation kinds tracked by <c>IUnifiedOperationTracker</c>, SignalR progress
/// events, and the persisted operation history file.
/// Serialized as camelCase strings on the wire ("cacheClearing", "logProcessing", ...)
/// to match the pre-existing JSON contract. Legacy snake_case values
/// (e.g. "log_processing") are accepted on deserialization for backward compatibility
/// with older persisted state.
/// </summary>
[JsonConverter(typeof(OperationTypeJsonConverter))]
public enum OperationType
{
    CacheClearing,
    CorruptionRemoval,
    CorruptionDetection,
    GameDetection,
    LogProcessing,
    GameRemoval,
    ServiceRemoval,
    DepotMapping,
    DataImport,
    DatabaseReset,
    LogRemoval,
    EpicMapping,
    EvictionScan,
    EvictionRemoval
}

/// <summary>
/// Serializes <see cref="OperationType"/> as camelCase strings and accepts any casing
/// plus common legacy snake_case values (e.g. "log_processing" → <see cref="OperationType.LogProcessing"/>)
/// on deserialization so persisted operation history files continue to load.
/// </summary>
internal sealed class OperationTypeJsonConverter : JsonConverter<OperationType>
{
    public override OperationType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            var parsed = OperationTypeExtensions.TryParseWire(value);
            if (parsed.HasValue)
            {
                return parsed.Value;
            }

            throw new JsonException($"Unknown OperationType value: '{value}'");
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing OperationType");
    }

    public override void Write(Utf8JsonWriter writer, OperationType value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Helpers for interop between <see cref="OperationType"/> and legacy string-based
/// state (operation_history.json previously stored both snake_case and camelCase).
/// </summary>
public static class OperationTypeExtensions
{
    /// <summary>
    /// Returns the canonical camelCase wire value (e.g. "logProcessing", "cacheClearing").
    /// </summary>
    public static string ToWireString(this OperationType type) => type switch
    {
        OperationType.CacheClearing => "cacheClearing",
        OperationType.CorruptionRemoval => "corruptionRemoval",
        OperationType.CorruptionDetection => "corruptionDetection",
        OperationType.GameDetection => "gameDetection",
        OperationType.LogProcessing => "logProcessing",
        OperationType.GameRemoval => "gameRemoval",
        OperationType.ServiceRemoval => "serviceRemoval",
        OperationType.DepotMapping => "depotMapping",
        OperationType.DataImport => "dataImport",
        OperationType.DatabaseReset => "databaseReset",
        OperationType.LogRemoval => "logRemoval",
        OperationType.EpicMapping => "epicMapping",
        OperationType.EvictionScan => "evictionScan",
        OperationType.EvictionRemoval => "evictionRemoval",
        _ => JsonNamingPolicy.CamelCase.ConvertName(type.ToString())
    };

    /// <summary>
    /// Parses a wire / legacy string into an <see cref="OperationType"/>.
    /// Accepts camelCase ("logProcessing"), PascalCase ("LogProcessing"), and legacy
    /// snake_case ("log_processing") forms. Returns <c>null</c> for null / whitespace /
    /// unrecognised values.
    /// </summary>
    public static OperationType? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace("_", string.Empty).Replace("-", string.Empty);

        if (Enum.TryParse<OperationType>(normalized, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
