using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Event kind emitted by Rust binaries on stdout (cache clearing / reconciliation / corruption / detection).
/// Serialized as lowercase strings on the wire ("progress", "started", "completed", "error") to match
/// the JSON emitted by the Rust processors.
/// </summary>
[JsonConverter(typeof(RustProgressEventKindJsonConverter))]
public enum RustProgressEventKind
{
    /// <summary>Fallback for unrecognized / missing wire values.</summary>
    Unknown,

    /// <summary>Per-item / per-tick progress update.</summary>
    Progress,

    /// <summary>Operation start marker.</summary>
    Started,

    /// <summary>Operation completed successfully.</summary>
    Completed,

    /// <summary>Operation failed with an error.</summary>
    Error
}

/// <summary>
/// Serializes <see cref="RustProgressEventKind"/> as lowercase strings ("progress", "started", ...)
/// and accepts any casing on deserialization. Unrecognized values deserialize to
/// <see cref="RustProgressEventKind.Unknown"/> so downstream code can handle new Rust emissions
/// gracefully without crashing JSON deserialization.
/// </summary>
internal sealed class RustProgressEventKindJsonConverter : JsonConverter<RustProgressEventKind>
{
    public override RustProgressEventKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            return RustProgressEventKind.Unknown;
        }

        var value = reader.GetString();
        return RustProgressEventKindExtensions.Parse(value);
    }

    public override void Write(Utf8JsonWriter writer, RustProgressEventKind value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="RustProgressEventKind"/>.
/// </summary>
public static class RustProgressEventKindExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value ("progress", "started", "completed", "error", "unknown").
    /// </summary>
    public static string ToWireString(this RustProgressEventKind kind) => kind switch
    {
        RustProgressEventKind.Progress => "progress",
        RustProgressEventKind.Started => "started",
        RustProgressEventKind.Completed => "completed",
        RustProgressEventKind.Error => "error",
        RustProgressEventKind.Unknown => "unknown",
        _ => "unknown"
    };

    /// <summary>
    /// Parses a wire value into a <see cref="RustProgressEventKind"/>. Returns
    /// <see cref="RustProgressEventKind.Unknown"/> for null / unrecognized input.
    /// </summary>
    public static RustProgressEventKind Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RustProgressEventKind.Unknown;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "progress" => RustProgressEventKind.Progress,
            "started" => RustProgressEventKind.Started,
            "completed" => RustProgressEventKind.Completed,
            "complete" => RustProgressEventKind.Completed,
            "error" => RustProgressEventKind.Error,
            "failed" => RustProgressEventKind.Error,
            _ => RustProgressEventKind.Unknown
        };
    }
}
