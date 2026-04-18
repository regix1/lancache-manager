using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Cache clearing delete mode — controls how files are removed during a cache clear.
/// Serialized as lowercase strings on the wire ("preserve", "full", "rsync")
/// to preserve the pre-existing JSON contract with the frontend and the Rust
/// cache_clear binary CLI argument.
/// </summary>
[JsonConverter(typeof(CacheDeleteModeJsonConverter))]
public enum CacheDeleteMode
{
    Preserve,
    Full,
    Rsync
}

/// <summary>
/// Serializes <see cref="CacheDeleteMode"/> as lowercase strings
/// ("preserve" / "full" / "rsync") and accepts any casing on deserialization
/// to match the pre-existing wire contract.
/// </summary>
internal sealed class CacheDeleteModeJsonConverter : JsonStringEnumConverter<CacheDeleteMode>
{
    public CacheDeleteModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="CacheDeleteMode"/>.
/// </summary>
public static class CacheDeleteModeExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire / CLI value ("preserve", "full", "rsync").
    /// This is the value passed directly to the Rust cache_clear binary.
    /// </summary>
    public static string ToWireString(this CacheDeleteMode mode) => mode switch
    {
        CacheDeleteMode.Preserve => "preserve",
        CacheDeleteMode.Full => "full",
        CacheDeleteMode.Rsync => "rsync",
        _ => mode.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Returns a human-readable display name for UI/logging.
    /// </summary>
    public static string ToDisplayName(this CacheDeleteMode mode) => mode switch
    {
        CacheDeleteMode.Preserve => "Preserve",
        CacheDeleteMode.Full => "Remove All",
        CacheDeleteMode.Rsync => "Rsync",
        _ => mode.ToString()
    };
}
