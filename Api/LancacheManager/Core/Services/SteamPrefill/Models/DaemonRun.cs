namespace LancacheManager.Core.Services.SteamPrefill;

public sealed class DaemonRun
{
    public required Guid PrefillRunId { get; init; }
    public required string SessionId { get; init; }
    public required string DaemonInstanceId { get; init; }
    public Guid? PrefillScheduleId { get; init; }
    public string? ScheduleName { get; init; }
    public string? NotificationMode { get; init; }
    public Guid? ParentOperationId { get; init; }
    public required DaemonRunOptions Options { get; init; }
    public required DaemonRunSnapshot Snapshot { get; set; }
    public PrefillState PrefillState { get; set; } = PrefillState.Started;
    public PrefillProgress? LastProgress { get; set; }
    public SemaphoreSlim PrefillWork { get; } = new(1, 1);
    public int TerminalCompletedFlag;
    public long LastProgressTicksUtc = DateTime.UtcNow.Ticks;
    public long LastProgressBytes { get; set; }
    public bool Recovering { get; set; }
    public bool CancelRequested { get; set; }
    public string? CancelReason { get; set; }
    public bool AdmissionPending { get; set; }
    public bool HistoryIncomplete { get; set; }
    public string? ErrorStageKey { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public Dictionary<string, DaemonRunItem> Items { get; } = new(StringComparer.Ordinal);
    public TaskCompletionSource<DaemonRunStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
