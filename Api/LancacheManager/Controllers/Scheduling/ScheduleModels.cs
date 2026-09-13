using LancacheManager.Infrastructure.Services.Scheduling;

namespace LancacheManager.Controllers;

public class TriggerAllResponse
{
    /// <summary>Services that were idle when this call reached them, so it started a new run.</summary>
    public int TriggeredCount { get; set; }

    /// <summary>Services with a pending, starting or active run when this call reached them.</summary>
    public int AlreadyRunningCount { get; set; }

    /// <summary>Already-running services that retained one additional manual run.</summary>
    public int FollowUpCount { get; set; }

    /// <summary>Services retained until downloads finish before their loop is armed.</summary>
    public int SkippedCount { get; set; }

    /// <summary>The one reason behind every skip in this call, or null when nothing was skipped.
    /// Every schedule asks the identical question, so one sentence covers them all and no per-service
    /// list is reported.</summary>
    public string? SkippedReason { get; set; }
}

public class UpdateScheduleIntervalRequest
{
    public double IntervalHours { get; set; }
}

public class UpdateScheduleRunOnStartupRequest
{
    public bool RunOnStartup { get; set; }
}

public class UpdateScheduleCustomScheduleRequest
{
    /// <summary>
    /// Null clears the schedule and returns the service to its interval, which is why this is a
    /// wrapper rather than the schedule posted directly: an empty body is the clear operation.
    /// </summary>
    public CustomSchedule? CustomSchedule { get; set; }
}
