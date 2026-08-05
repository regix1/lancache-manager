namespace LancacheManager.Models;

public class ServiceScheduleInfo
{
    public string Key { get; set; } = "";
    public double IntervalHours { get; set; }
    public bool RunOnStartup { get; set; }
    public bool IsRunning { get; set; }
    public DateTime? LastRunUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public NotificationMode NotificationMode { get; set; }
    public bool SupportsNotifications { get; set; }
    public NotificationDisplayMode NotificationDisplayMode { get; set; }

    /// <summary>
    /// Set only on the Steam depot mapping schedule, and only while the scheduler has abandoned an
    /// incremental scan because Steam demands a full one. Null on every other service and whenever
    /// incremental is viable, so its presence alone answers "is a full scan required right now".
    /// </summary>
    public FullScanRequirement? PendingFullScan { get; set; }
}

/// <summary>
/// The two figures the viability check measured at the moment it gave up on an incremental depot
/// scan. They travel with the schedule so the Schedules card can put the "Full Scan Required" prompt
/// back on screen with the numbers the backend actually found, including after a page reload has
/// thrown away the SignalR event that carried them the first time.
/// </summary>
public class FullScanRequirement
{
    public uint ChangeGap { get; set; }
    public int EstimatedAppsToScan { get; set; }
}

/// <summary>
/// Live run status for a single scheduled service, consumed by the frontend notification recovery
/// pipeline to rehydrate an in-progress card after a page refresh. Serialized camelCase on the wire.
/// </summary>
public class ScheduleRunStatus
{
    public bool IsRunning { get; set; }
    public string? OperationId { get; set; }
    public double PercentComplete { get; set; }
    public string? StageKey { get; set; }
    public IReadOnlyDictionary<string, object?>? Context { get; set; }
    public bool ShowNotification { get; set; }
}
