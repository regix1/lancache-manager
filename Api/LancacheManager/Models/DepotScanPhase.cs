using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Phases of the Steam PICS depot scan / mapping pipeline.
/// The enum is the internal / wire representation (camelCase via <see cref="DepotScanPhaseJsonConverter"/>).
/// Use <see cref="DepotScanPhaseExtensions.ToDisplayString"/> for user-facing strings.
/// </summary>
[JsonConverter(typeof(DepotScanPhaseJsonConverter))]
public enum DepotScanPhase
{
    Idle,
    Connecting,
    Processing,
    Saving,
    ResolvingOrphans,
    Importing,
    ApplyingMappings,
    Authenticating,
    RefreshingCatalog,
    Completed,
    Cancelled,
    Error,
    Unknown
}

/// <summary>
/// Serializes <see cref="DepotScanPhase"/> as camelCase strings and tolerates legacy
/// verbose status text (e.g. "Connecting and enumerating apps") on deserialization
/// so existing persisted state.json files continue to load.
/// </summary>
internal sealed class DepotScanPhaseJsonConverter : JsonConverter<DepotScanPhase>
{
    public override DepotScanPhase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return DepotScanPhase.Idle;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return DepotScanPhaseExtensions.ParseLegacy(reader.GetString());
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing DepotScanPhase");
    }

    public override void Write(Utf8JsonWriter writer, DepotScanPhase value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));
    }
}

/// <summary>
/// Helpers to map <see cref="DepotScanPhase"/> to verbose user-facing display text
/// and parse legacy status strings that used to be stored as free-form text.
/// </summary>
public static class DepotScanPhaseExtensions
{
    /// <summary>
    /// Returns the verbose user-facing text that the status used to be written as.
    /// </summary>
    public static string ToDisplayString(this DepotScanPhase phase) => phase switch
    {
        DepotScanPhase.Idle => "Idle",
        DepotScanPhase.Connecting => "Connecting and enumerating apps",
        DepotScanPhase.Processing => "Processing app data",
        DepotScanPhase.Saving => "Saving PICS data to JSON",
        DepotScanPhase.ResolvingOrphans => "Resolving orphan depots",
        DepotScanPhase.Importing => "Importing to database",
        DepotScanPhase.ApplyingMappings => "Applying depot mappings",
        DepotScanPhase.Authenticating => "Authenticating",
        DepotScanPhase.RefreshingCatalog => "Refreshing catalog",
        DepotScanPhase.Completed => "Completed",
        DepotScanPhase.Cancelled => "Cancelled",
        DepotScanPhase.Error => "Error occurred",
        DepotScanPhase.Unknown => "Unknown",
        _ => "Unknown"
    };

    /// <summary>
    /// Parse a persisted / wire string (camelCase, PascalCase, or legacy verbose text)
    /// into a <see cref="DepotScanPhase"/>. Falls back to <see cref="DepotScanPhase.Unknown"/>
    /// rather than throwing so that a single bad persisted value cannot prevent startup.
    /// </summary>
    public static DepotScanPhase ParseLegacy(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return DepotScanPhase.Idle;
        }

        return status switch
        {
            "Idle" => DepotScanPhase.Idle,
            "Connecting and enumerating apps" => DepotScanPhase.Connecting,
            "Processing app data" => DepotScanPhase.Processing,
            "Saving PICS data to JSON" => DepotScanPhase.Saving,
            "Resolving orphan depots" => DepotScanPhase.ResolvingOrphans,
            "Importing to database" => DepotScanPhase.Importing,
            "Applying depot mappings" => DepotScanPhase.ApplyingMappings,
            "Authenticating" => DepotScanPhase.Authenticating,
            "Refreshing catalog" => DepotScanPhase.RefreshingCatalog,
            "completed" or "Completed" => DepotScanPhase.Completed,
            "cancelled" or "Cancelled" => DepotScanPhase.Cancelled,
            "Error occurred" or "Error" => DepotScanPhase.Error,
            "Unknown" => DepotScanPhase.Unknown,
            _ => Enum.TryParse<DepotScanPhase>(status, ignoreCase: true, out var parsed)
                ? parsed
                : DepotScanPhase.Unknown
        };
    }
}
