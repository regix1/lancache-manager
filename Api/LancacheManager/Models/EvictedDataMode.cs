using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Controls how evicted downloads are displayed across the dashboard and related APIs.
/// Serialized as camelCase strings on the wire ("show", "hide", "showClean", "remove")
/// to preserve the pre-existing JSON contract with the frontend and persisted state.
/// </summary>
[JsonConverter(typeof(EvictedDataModeJsonConverter))]
public enum EvictedDataMode
{
    /// <summary>Visible with badge + dimmed, included in stats.</summary>
    Show,

    /// <summary>Hidden from all pages + excluded from stats.</summary>
    Hide,

    /// <summary>Visible without badge or dimming, included in stats.</summary>
    ShowClean,

    /// <summary>Removed from the database by the reconciliation service.</summary>
    Remove
}

/// <summary>
/// Serializes <see cref="EvictedDataMode"/> as camelCase strings
/// ("show", "hide", "showClean", "remove") and accepts any casing on deserialization
/// to match the pre-existing wire contract.
/// </summary>
internal sealed class EvictedDataModeJsonConverter : JsonStringEnumConverter<EvictedDataMode>
{
    public EvictedDataModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="EvictedDataMode"/>.
/// </summary>
public static class EvictedDataModeExtensions
{
    /// <summary>
    /// Returns the canonical camelCase wire value ("show", "hide", "showClean", "remove").
    /// </summary>
    public static string ToWireString(this EvictedDataMode mode) => mode switch
    {
        EvictedDataMode.Show => "show",
        EvictedDataMode.Hide => "hide",
        EvictedDataMode.ShowClean => "showClean",
        EvictedDataMode.Remove => "remove",
        _ => mode.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a legacy string value into an <see cref="EvictedDataMode"/>. Case-insensitive.
    /// Returns <c>null</c> if the value is null, whitespace, or unrecognised.
    /// </summary>
    public static EvictedDataMode? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<EvictedDataMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
