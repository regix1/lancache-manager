using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Prefill operation lifecycle state — emitted by the prefill daemons (Steam / Epic) over
/// their socket protocol and rebroadcast to the frontend via SignalR.
///
/// The daemon is a separate binary process that we do not control, so an <see cref="Unknown"/>
/// fallback is provided for forward compatibility with daemon versions that emit new values.
///
/// Serialized as lowercase strings on the wire ("idle", "downloading", "started",
/// "completed", "failed", "error", "cancelled", "app_completed", "already_cached") to
/// preserve the pre-existing JSON contract with the frontend and the external daemon protocol.
/// </summary>
[JsonConverter(typeof(PrefillProgressStateJsonConverter))]
public enum PrefillProgressState
{
    /// <summary>
    /// Catch-all for future daemon-emitted states that this version of the C# server
    /// does not recognise. Preserved as an explicit member so deserialization never
    /// throws on unrecognised daemon output.
    /// </summary>
    Unknown,

    /// <summary>Default / idle state — no prefill is active.</summary>
    Idle,

    /// <summary>Daemon is actively downloading content.</summary>
    Downloading,

    /// <summary>Prefill operation has been initiated (fired by the C# service, not the daemon).</summary>
    Started,

    /// <summary>Prefill operation completed successfully.</summary>
    Completed,

    /// <summary>Prefill operation failed.</summary>
    Failed,

    /// <summary>
    /// Daemon-emitted error state. Normalised to <see cref="Failed"/> before the
    /// state is forwarded to SignalR consumers.
    /// </summary>
    Error,

    /// <summary>Prefill operation was cancelled.</summary>
    Cancelled,

    /// <summary>
    /// Per-app completion event emitted by C# after observing a daemon app_completed
    /// (successful download). Wire value: <c>"app_completed"</c>.
    /// </summary>
    AppCompleted,

    /// <summary>
    /// Per-app already-cached event emitted by C# when a daemon app_completed has
    /// Result = AlreadyUpToDate / Skipped / NoDepotsToDownload. Wire value:
    /// <c>"already_cached"</c>.
    /// </summary>
    AlreadyCached
}

/// <summary>
/// Serializes <see cref="PrefillProgressState"/> as its canonical lowercase wire string
/// and maps any unknown / empty input to <see cref="PrefillProgressState.Unknown"/> on
/// deserialization (never throws).
/// </summary>
internal sealed class PrefillProgressStateJsonConverter : JsonConverter<PrefillProgressState>
{
    public override PrefillProgressState Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        // Null tokens map to Unknown rather than throwing; callers treat Unknown as "fall-back".
        if (reader.TokenType == JsonTokenType.Null)
        {
            return PrefillProgressState.Unknown;
        }

        var raw = reader.GetString();
        return PrefillProgressStateExtensions.ParseOrUnknown(raw);
    }

    public override void Write(Utf8JsonWriter writer, PrefillProgressState value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToWireString());
    }
}

/// <summary>
/// Extension helpers for <see cref="PrefillProgressState"/>.
/// </summary>
public static class PrefillProgressStateExtensions
{
    /// <summary>
    /// Returns the canonical lowercase wire value.
    /// </summary>
    public static string ToWireString(this PrefillProgressState state) => state switch
    {
        PrefillProgressState.Idle => "idle",
        PrefillProgressState.Downloading => "downloading",
        PrefillProgressState.Started => "started",
        PrefillProgressState.Completed => "completed",
        PrefillProgressState.Failed => "failed",
        PrefillProgressState.Error => "error",
        PrefillProgressState.Cancelled => "cancelled",
        PrefillProgressState.AppCompleted => "app_completed",
        PrefillProgressState.AlreadyCached => "already_cached",
        PrefillProgressState.Unknown => "unknown",
        _ => state.ToString().ToLowerInvariant()
    };

    /// <summary>
    /// Parses a raw state string (case-insensitive) into a <see cref="PrefillProgressState"/>,
    /// returning <see cref="PrefillProgressState.Unknown"/> for null, whitespace, or
    /// unrecognised values. Never throws. Accepts both snake_case wire values
    /// (<c>"app_completed"</c>, <c>"already_cached"</c>) and PascalCase enum names.
    /// </summary>
    public static PrefillProgressState ParseOrUnknown(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return PrefillProgressState.Unknown;
        }

        // Snake-case wire values for the multi-word members must be mapped explicitly
        // because Enum.TryParse does not understand "app_completed" → AppCompleted.
        switch (value.Trim().ToLowerInvariant())
        {
            case "app_completed": return PrefillProgressState.AppCompleted;
            case "already_cached": return PrefillProgressState.AlreadyCached;
        }

        if (Enum.TryParse<PrefillProgressState>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return PrefillProgressState.Unknown;
    }

    /// <summary>
    /// Applies the daemon-side <c>error</c> → <c>failed</c> normalisation. All other
    /// values pass through unchanged. Use before broadcasting state to SignalR consumers.
    /// </summary>
    public static PrefillProgressState NormaliseErrorToFailed(this PrefillProgressState state)
        => state == PrefillProgressState.Error ? PrefillProgressState.Failed : state;

    /// <summary>
    /// True if the state is one of the terminal lifecycle states (completed / failed / cancelled / error).
    /// </summary>
    public static bool IsTerminal(this PrefillProgressState state) => state switch
    {
        PrefillProgressState.Completed => true,
        PrefillProgressState.Failed => true,
        PrefillProgressState.Cancelled => true,
        PrefillProgressState.Error => true,
        _ => false
    };
}
