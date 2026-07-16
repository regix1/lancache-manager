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
