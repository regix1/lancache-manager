using System.ComponentModel.DataAnnotations;

namespace LancacheManager.Models;

public sealed class PrefillRun
{
    [Key]
    public Guid Id { get; set; }
    [MaxLength(50)]
    public required string SessionId { get; set; }
    [MaxLength(36)]
    public required string DaemonInstanceId { get; set; }
    [MaxLength(32)]
    public string Source { get; set; } = "manual";
    public Guid? ScheduleId { get; set; }
    [MaxLength(200)]
    public string? ScheduleName { get; set; }
    [MaxLength(32)]
    public string? NotificationMode { get; set; }
    public Guid? ParentOperationId { get; set; }
    public required string OptionsJson { get; set; }
    public required string SnapshotJson { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    [MaxLength(32)]
    public string State { get; set; } = "started";
    [MaxLength(500)]
    public string? Reason { get; set; }
    public long Sequence { get; set; }
    public long Revision { get; set; }
    public bool CancelRequested { get; set; }
    public bool HistoryIncomplete { get; set; }
    public PrefillSession? Session { get; set; }
}
