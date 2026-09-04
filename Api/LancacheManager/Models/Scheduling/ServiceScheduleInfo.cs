using LancacheManager.Infrastructure.Services.Scheduling;

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
    /// The custom schedule driving this service, or null when it runs on <see cref="IntervalHours"/>.
    /// When one is set it decides the next run outright and the interval is only what the service
    /// falls back to once the schedule is cleared, so the UI must read this before the interval.
    /// </summary>
    public CustomSchedule? CustomSchedule { get; set; }

    /// <summary>
    /// Set only on the Steam depot mapping schedule, and only while the scheduler has abandoned an
    /// incremental scan because Steam demands a full one. Null on every other service and whenever
    /// incremental is viable, so its presence alone answers "is a full scan required right now".
    /// </summary>
    public FullScanRequirement? PendingFullScan { get; set; }

    /// <summary>
    /// Set only on the Xbox mapping schedule, and only while its sign-in is waiting for the user to
    /// approve a device code. The wait registers a tracked XboxMapping operation, which is what turns
    /// <see cref="IsRunning"/> true and greys out Run Now, so its presence alone answers "why is this
    /// row busy when no refresh was scheduled". Null on every other service.
    /// </summary>
    public bool? AwaitingSignIn { get; set; }

    /// <summary>
    /// Set only on the scheduled prefill schedule, which is the one service that runs several
    /// platforms under a single key and lets each choose its own notification style. Keyed by the
    /// platform name as it appears on the wire (Steam, Epic, Xbox, BattleNet, Riot), holding only the
    /// platforms that have made a choice; a platform absent here falls back to
    /// <see cref="NotificationDisplayMode"/>. It rides this snapshot rather than being fetched on its
    /// own so it reaches the notification bar through the same broadcast, which is also the only
    /// channel a guest session gets (the config endpoint itself is account-holder only).
    /// Null on every other service.
    /// </summary>
    public IReadOnlyDictionary<string, NotificationDisplayMode>? PlatformNotificationDisplayModes { get; set; }
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
