using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// The scan the game detection schedule runs on each automatic tick and on its Run Now button.
/// Unlike <see cref="DetectionScanType"/>, which records the scan a finished run actually performed,
/// this is the user's stored choice, so it carries a third value that names no single run.
/// Serialized as camelCase strings on the wire to match the frontend union.
/// </summary>
[JsonConverter(typeof(GameDetectionScanModeJsonConverter))]
public enum GameDetectionScanMode
{
    /// <summary>Re-scan every cached item from scratch. First member, so an absent stored value
    /// deserializes to Full rather than to whichever mode happened to be declared first.</summary>
    Full,

    /// <summary>Only re-scan content cached since the last detection run.</summary>
    Incremental,

    /// <summary>Incremental until the last full scan is a week old, then one full scan.</summary>
    Hybrid
}

/// <summary>
/// Serializes <see cref="GameDetectionScanMode"/> as camelCase strings ("full", "incremental",
/// "hybrid"). Mirrors <see cref="NotificationDisplayModeJsonConverter"/>: a dedicated converter
/// rather than the bare <c>JsonStringEnumConverter&lt;TEnum&gt;</c> attribute, which ignores the
/// global naming policy and would emit PascalCase member names instead.
/// </summary>
internal sealed class GameDetectionScanModeJsonConverter : JsonStringEnumConverter<GameDetectionScanMode>
{
    public GameDetectionScanModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Extension helpers for <see cref="GameDetectionScanMode"/>.
/// </summary>
public static class GameDetectionScanModeExtensions
{
    /// <summary>
    /// The scan this mode takes right now. Hybrid resolves to the run it takes this time, so callers
    /// get the same answer whether the mode names a run or picks one.
    /// </summary>
    public static bool IsIncremental(
        this GameDetectionScanMode mode,
        DateTime? lastFullScanUtc,
        DateTime nowUtc) => mode switch
        {
            GameDetectionScanMode.Incremental => true,
            // A hybrid week scans incrementally until the last full scan is a week old, then runs one
            // full scan and re-anchors on it. The decision reads a timestamp rather than a tally of
            // runs so that a tick missed while the container was down still lands the full scan on the
            // next tick instead of shifting the week, and a restart changes nothing. With no full scan
            // recorded there is no baseline to build on, so the first run is a full one.
            GameDetectionScanMode.Hybrid =>
                lastFullScanUtc.HasValue && nowUtc - lastFullScanUtc.Value < TimeSpan.FromDays(7),
            _ => false
        };
}
