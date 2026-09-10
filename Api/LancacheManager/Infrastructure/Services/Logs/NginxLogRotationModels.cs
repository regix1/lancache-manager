using System.Text.Json.Serialization;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Schema for the legacy log-rotation-settings.json file. Used only for one-time
/// migration into state.json - after migration this file is deleted and never
/// re-created. Do not reference outside the migration code path.
/// </summary>
internal class LegacyLogRotationSettings
{
    public int ScheduleHours { get; set; } = 24;
}

/// <summary>
/// Action that can make nginx log reopen available to the manager.
/// </summary>
[JsonConverter(typeof(NginxReopenHintJsonConverter))]
public enum NginxReopenHint
{
    None,
    GrantSignalPrivilege,
    EnablePidHost,
    MountDockerSocket
}

/// <summary>
/// Current nginx reopen availability and the applicable remedy when unavailable.
/// </summary>
public sealed record NginxReopenAvailability(bool Available, NginxReopenHint Hint);
