using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Step identifier in the first-run / setup wizard.
/// Serialized as kebab-case strings on the wire ("database-setup", "permissions-check",
/// "import-historical-data", "platform-setup", "steam-api-key", "steam-auth",
/// "depot-init", "pics-progress", "epic-auth", "log-processing", "depot-mapping")
/// to preserve the pre-existing JSON contract with the frontend and persisted state.
/// </summary>
[JsonConverter(typeof(SetupStepJsonConverter))]
public enum SetupStep
{
    /// <summary>Fallback used when a persisted value can no longer be mapped.</summary>
    Unknown,

    DatabaseSetup,
    PermissionsCheck,
    ImportHistoricalData,
    PlatformSetup,
    SteamApiKey,
    SteamAuth,
    DepotInit,
    PicsProgress,
    EpicAuth,
    LogProcessing,
    DepotMapping
}

/// <summary>
/// Serializes <see cref="SetupStep"/> as kebab-case strings and accepts any casing
/// on deserialization. Unknown values map to <see cref="SetupStep.Unknown"/> rather
/// than throwing, matching the feedback guidance for external-wire enums.
/// </summary>
internal sealed class SetupStepJsonConverter : JsonConverter<SetupStep>
{
    public override SetupStep Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return SetupStep.Unknown;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var raw = reader.GetString();
            var parsed = SetupStepExtensions.TryParseWire(raw);
            return parsed ?? SetupStep.Unknown;
        }

        throw new JsonException($"Unexpected token {reader.TokenType} when parsing SetupStep");
    }

    public override void Write(Utf8JsonWriter writer, SetupStep value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="SetupStep"/>.
/// </summary>
public static class SetupStepExtensions
{
    /// <summary>
    /// Returns the canonical kebab-case wire value.
    /// </summary>
    public static string ToWireString(this SetupStep step) => step switch
    {
        SetupStep.DatabaseSetup => "database-setup",
        SetupStep.PermissionsCheck => "permissions-check",
        SetupStep.ImportHistoricalData => "import-historical-data",
        SetupStep.PlatformSetup => "platform-setup",
        SetupStep.SteamApiKey => "steam-api-key",
        SetupStep.SteamAuth => "steam-auth",
        SetupStep.DepotInit => "depot-init",
        SetupStep.PicsProgress => "pics-progress",
        SetupStep.EpicAuth => "epic-auth",
        SetupStep.LogProcessing => "log-processing",
        SetupStep.DepotMapping => "depot-mapping",
        SetupStep.Unknown => "unknown",
        _ => step.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a wire string into a <see cref="SetupStep"/>. Case-insensitive.
    /// Returns <c>null</c> if the value is null, whitespace, or unrecognised.
    /// </summary>
    public static SetupStep? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "database-setup" => SetupStep.DatabaseSetup,
            "permissions-check" => SetupStep.PermissionsCheck,
            "import-historical-data" => SetupStep.ImportHistoricalData,
            "platform-setup" => SetupStep.PlatformSetup,
            "steam-api-key" => SetupStep.SteamApiKey,
            "steam-auth" => SetupStep.SteamAuth,
            "depot-init" => SetupStep.DepotInit,
            "pics-progress" => SetupStep.PicsProgress,
            "epic-auth" => SetupStep.EpicAuth,
            "log-processing" => SetupStep.LogProcessing,
            "depot-mapping" => SetupStep.DepotMapping,
            "unknown" => SetupStep.Unknown,
            _ => null
        };
    }
}
