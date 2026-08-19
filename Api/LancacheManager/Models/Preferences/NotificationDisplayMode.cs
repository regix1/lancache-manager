using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// How a schedule card's run notifications render inside the universal notification bar: as the
/// full card, or as a thin condensed status line.
/// </summary>
[JsonConverter(typeof(NotificationDisplayModeJsonConverter))]
public enum NotificationDisplayMode
{
    Full,
    Condensed
}

/// <summary>
/// Serializes <see cref="NotificationDisplayMode"/> as camelCase strings ("full", "condensed").
/// Mirrors <see cref="NotificationModeJsonConverter"/>: a dedicated converter is used rather than the
/// bare <c>JsonStringEnumConverter&lt;TEnum&gt;</c> attribute (which ignores the global naming policy
/// and would emit PascalCase member names instead), so the wire value matches the frontend union.
/// </summary>
internal sealed class NotificationDisplayModeJsonConverter : JsonStringEnumConverter<NotificationDisplayMode>
{
    public NotificationDisplayModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
