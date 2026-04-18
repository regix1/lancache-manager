using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// The data source the user selected during the setup wizard.
/// Serialized as lowercase strings on the wire ("github", "steam", "epic", "skip")
/// to preserve the pre-existing JSON contract with the frontend and persisted state.
/// </summary>
[JsonConverter(typeof(DataSourceChoiceJsonConverter))]
public enum DataSourceChoice
{
    Github,
    Steam,
    Epic,
    Skip
}

/// <summary>
/// Serializes <see cref="DataSourceChoice"/> as lowercase strings
/// ("github" / "steam" / "epic" / "skip") and accepts any casing on deserialization
/// to match the pre-existing wire contract.
/// </summary>
internal sealed class DataSourceChoiceJsonConverter : JsonStringEnumConverter<DataSourceChoice>
{
    public DataSourceChoiceJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="DataSourceChoice"/>.
/// </summary>
public static class DataSourceChoiceExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("github", "steam", "epic", "skip").
    /// </summary>
    public static string ToWireString(this DataSourceChoice choice) => choice switch
    {
        DataSourceChoice.Github => "github",
        DataSourceChoice.Steam => "steam",
        DataSourceChoice.Epic => "epic",
        DataSourceChoice.Skip => "skip",
        _ => choice.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a wire string into a <see cref="DataSourceChoice"/>. Case-insensitive.
    /// Returns <c>null</c> if the value is null, whitespace, or unrecognised.
    /// </summary>
    public static DataSourceChoice? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<DataSourceChoice>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
