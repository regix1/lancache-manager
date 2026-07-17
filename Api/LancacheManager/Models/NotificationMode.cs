using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// How a schedulable service surfaces the notifications for a run: on every run, only when the
/// run was manually triggered, or never.
/// </summary>
[JsonConverter(typeof(NotificationModeJsonConverter))]
public enum NotificationMode
{
    All,
    Manual,
    Silent
}

/// <summary>
/// Serializes <see cref="NotificationMode"/> as camelCase strings ("all", "manual", "silent").
/// Mirrors <see cref="PersistenceModeJsonConverter"/>: a dedicated converter is used rather than the
/// bare <c>JsonStringEnumConverter&lt;TEnum&gt;</c> attribute (which ignores the global naming policy
/// and would emit PascalCase member names instead), so the wire value matches the frontend union.
/// </summary>
internal sealed class NotificationModeJsonConverter : JsonStringEnumConverter<NotificationMode>
{
    public NotificationModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Maps a <see cref="NotificationMode"/> and the <see cref="RunTrigger"/> that produced a run to
/// whether that run should surface its notifications. Generalizes the old eviction-only
/// "silent automatic scan" check so every schedulable service can gate the same way.
/// </summary>
public static class NotificationModeExtensions
{
    // The discard arm throws instead of picking a behavior: an out-of-range value cast to this
    // enum can only come from corrupted state, and silently treating it as "always notify" (or
    // "never") would mask that corruption. The lenient JSON converter already normalizes every
    // persisted shape to a named member, so this path is unreachable in practice.
    public static bool AllowsTrigger(this NotificationMode mode, RunTrigger trigger) => mode switch
    {
        NotificationMode.All => true,
        NotificationMode.Manual => trigger == RunTrigger.Manual,
        NotificationMode.Silent => false,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };
}
