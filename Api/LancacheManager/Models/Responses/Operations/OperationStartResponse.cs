using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Accepted-response body returned by endpoints that used to reply 409 on operation
/// conflicts. Under the wait-queue model a conflicting request is never rejected:
/// it waits (<see cref="OperationStartStatus.Waiting"/>, <see cref="OperationId"/> = the waiting
/// operation's id, cancelable via the universal cancel endpoint; a request that joins an identical
/// waiting one gets that one's id), is answered by the identical operation already running
/// (<see cref="OperationStartStatus.AlreadyRunning"/>, that operation's id), or starts immediately
/// when the blocker finished before the enqueue committed.
/// </summary>
public sealed class OperationStartResponse
{
    public bool? FollowUpWaiting { get; init; }

    public Guid OperationId { get; init; }

    /// <summary>What happened to the request. "skipped" is the same word the operation tracker already
    /// puts on the wire for a run that stopped without doing anything, so one idea keeps one name.</summary>
    public OperationStartStatus Status { get; init; } = OperationStartStatus.Waiting;

    /// <summary>Why nothing was started, set only when <see cref="Status"/> is
    /// <see cref="OperationStartStatus.Skipped"/>. Null on every other status.</summary>
    public string? SkippedReason { get; init; }
}

/// <summary>What the queue did with a request, sent as "waiting", "started", "alreadyRunning" or "skipped".</summary>
[JsonConverter(typeof(OperationStartStatusJsonConverter))]
public enum OperationStartStatus
{
    Waiting,
    Started,
    AlreadyRunning,
    Skipped
}

internal sealed class OperationStartStatusJsonConverter : JsonStringEnumConverter<OperationStartStatus>
{
    public OperationStartStatusJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
