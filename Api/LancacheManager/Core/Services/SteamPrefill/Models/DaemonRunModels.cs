using System.Text.Json.Serialization;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.SteamPrefill;

public sealed record DaemonRunOptions
{
    public IReadOnlyList<string>? AppIds { get; init; }
    public string Selection { get; init; } = "selected";
    public bool Force { get; init; }
    public IReadOnlyList<string> OperatingSystems { get; init; } = [];
    public int MaxConcurrency { get; init; }
    public int? TopCount { get; init; }
}

public sealed record DaemonRunItem
{
    [JsonConverter(typeof(FlexibleStringConverter))]
    public required string AppId { get; init; }
    public string? Name { get; init; }
    public string State { get; init; } = "pending";
    public string? Result { get; init; }
    public string? Reason { get; init; }
    public long Sequence { get; init; }
    public long BytesTransferred { get; init; }
    public long? TotalBytes { get; init; }
    public IReadOnlyList<DepotManifestProgressInfo>? Depots { get; init; }
}

public sealed record DaemonRunSnapshot
{
    public required string OperationId { get; init; }
    public required string DaemonInstanceId { get; init; }
    public long Sequence { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string State { get; init; } = "preparing";
    public string? Reason { get; init; }
    public bool SelectionResolved { get; init; }
    public DaemonRunItem? CurrentItem { get; init; }
    public int TotalApps { get; init; }
    public int CompletedApps { get; init; }
    public int CachedApps { get; init; }
    public int FailedApps { get; init; }
    public int CancelledApps { get; init; }
    public int SkippedApps { get; init; }
    public long BytesTransferred { get; init; }
}

public sealed record DaemonOperationPage(
    DaemonRunSnapshot Operation,
    DaemonRunOptions Options,
    bool SelectionResolved,
    int TotalItems,
    IReadOnlyList<DaemonRunItem> Items,
    int? NextOffset);

public sealed record DaemonRunStatus
{
    public required Guid RunId { get; init; }
    public required string SessionId { get; init; }
    public required string DaemonInstanceId { get; init; }
    public Guid? ScheduleId { get; init; }
    public string? ScheduleName { get; init; }
    public string? NotificationMode { get; init; }
    public Guid? ParentOperationId { get; init; }
    public required DaemonRunOptions Options { get; init; }
    public required DaemonRunSnapshot Snapshot { get; init; }
    public PrefillProgress? Progress { get; init; }
    public bool Recovering { get; init; }
    public bool CancelRequested { get; init; }
    public bool HistoryIncomplete { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
}
