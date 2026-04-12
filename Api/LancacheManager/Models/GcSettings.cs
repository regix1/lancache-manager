namespace LancacheManager.Models;

public enum GcAggressiveness
{
    Disabled,
    OnPageLoad,
    Every60Minutes,
    Every60Seconds,
    Every30Seconds,
    Every10Seconds,
    Every5Seconds,
    Every1Second
}

public class GcSettings
{
    /// <summary>
    /// When true, the scheduled Performance Optimizations service is visible on the
    /// unified Schedules page and will run on its configured interval. When false, the
    /// service is hidden and performs no work. Replaces the legacy
    /// <see cref="Aggressiveness"/>-driven ladder.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Legacy Aggressiveness field. Kept only so pre-existing <c>gc-settings.json</c>
    /// files on disk still deserialize cleanly. New code MUST NOT branch on this value —
    /// use <see cref="Enabled"/> plus the interval from <c>ServiceScheduleRegistry</c>.
    /// <c>SettingsService</c> performs a one-time migration that converts non-Disabled
    /// legacy files into <see cref="Enabled"/>=true.
    /// </summary>
    [Obsolete("Replaced by Enabled + ServiceScheduleRegistry interval. Kept for legacy JSON deserialization.")]
    public GcAggressiveness Aggressiveness { get; set; } = GcAggressiveness.Disabled;

    public long MemoryThresholdMB { get; set; } = 4096; // 4GB default
}
