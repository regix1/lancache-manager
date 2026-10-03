using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// One completed schedule run. Actor fields are copied at admission time so the record remains
/// meaningful after an account is renamed or deleted.
/// </summary>
public class ScheduleExecution
{
    public long Id { get; set; }

    public Guid OperationId { get; set; }

    [MaxLength(64)]
    public string ServiceKey { get; set; } = string.Empty;

    public OperationStatus Status { get; set; }

    [JsonConverter(typeof(ScheduleRunTriggerJsonConverter))]
    public RunTrigger? Trigger { get; set; }

    public ScheduleActorKind ActorKind { get; set; }

    public Guid? AccountId { get; set; }

    [MaxLength(256)]
    public string? Username { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime CompletedAt { get; set; }

    [MaxLength(4096)]
    public string? Detail { get; set; }

    /// <summary>The first warning of a completed run (it worked, with errors); drawn amber with its sentence.</summary>
    public RunWarning? Warning { get; set; }

    public Guid? ScheduleId { get; set; }

    [MaxLength(256)]
    public string? ScheduleName { get; set; }

    public PrefillPlatform? Platform { get; set; }

    public bool WorkerStarted { get; set; }
}
