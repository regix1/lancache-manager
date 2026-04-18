using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Depot mapping scan mode — identifies the strategy used for a depot mapping scan.
/// Emitted as the <c>scanMode</c> field on SignalR <c>DepotMappingProgress</c> / related events.
/// Serialized as lowercase strings on the wire ("incremental", "full", "github") to preserve
/// the pre-existing JSON contract with the frontend.
/// </summary>
[JsonConverter(typeof(DepotScanModeJsonConverter))]
public enum DepotScanMode
{
    /// <summary>Incremental PICS scan — only processes changed apps since last change number.</summary>
    Incremental,

    /// <summary>Full PICS scan — processes every known app.</summary>
    Full,

    /// <summary>GitHub-sourced depot data — downloads pre-built depot mappings from GitHub releases.</summary>
    Github
}

/// <summary>
/// Serializes <see cref="DepotScanMode"/> as lowercase strings ("incremental" / "full" / "github")
/// and accepts any casing on deserialization to match the pre-existing wire contract.
/// </summary>
internal sealed class DepotScanModeJsonConverter : JsonStringEnumConverter<DepotScanMode>
{
    public DepotScanModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="DepotScanMode"/>.
/// </summary>
public static class DepotScanModeExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("incremental", "full", "github").
    /// </summary>
    public static string ToWireString(this DepotScanMode mode) => mode switch
    {
        DepotScanMode.Incremental => "incremental",
        DepotScanMode.Full => "full",
        DepotScanMode.Github => "github",
        _ => mode.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Returns a human-readable display name for UI/logging (e.g. "Incremental", "Full", "GitHub (PICS Updates)").
    /// </summary>
    public static string ToDisplayName(this DepotScanMode mode) => mode switch
    {
        DepotScanMode.Incremental => "Incremental",
        DepotScanMode.Full => "Full",
        DepotScanMode.Github => "GitHub (PICS Updates)",
        _ => mode.ToString()
    };
}
