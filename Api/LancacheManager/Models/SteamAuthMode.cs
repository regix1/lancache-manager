using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Steam connection authentication mode.
/// Serialized as lowercase strings on the wire ("anonymous", "authenticated")
/// to preserve the pre-existing JSON contract with the frontend and persisted
/// state files.
/// </summary>
[JsonConverter(typeof(SteamAuthModeJsonConverter))]
public enum SteamAuthMode
{
    Anonymous,
    Authenticated
}

/// <summary>
/// Serializes <see cref="SteamAuthMode"/> as lowercase strings
/// ("anonymous" / "authenticated") and accepts any casing on deserialization
/// to match the pre-existing wire contract.
/// </summary>
internal sealed class SteamAuthModeJsonConverter : JsonStringEnumConverter<SteamAuthMode>
{
    public SteamAuthModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Helpers for interop between <see cref="SteamAuthMode"/> and legacy string-based
/// state (<see cref="Infrastructure.Services.SteamAuthStorageService"/> persists
/// the mode as lowercase).
/// </summary>
public static class SteamAuthModeExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("anonymous", "authenticated").
    /// </summary>
    public static string ToWireString(this SteamAuthMode mode) => mode switch
    {
        SteamAuthMode.Anonymous => "anonymous",
        SteamAuthMode.Authenticated => "authenticated",
        _ => mode.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a legacy string value into a <see cref="SteamAuthMode"/>. Case-insensitive.
    /// Returns <c>null</c> if the value is null, whitespace, or unrecognised.
    /// </summary>
    public static SteamAuthMode? TryParseWire(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Enum.TryParse<SteamAuthMode>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
