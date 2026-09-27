using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

[JsonConverter(typeof(ScheduleActorKindJsonConverter))]
public enum ScheduleActorKind
{
    Account,
    Server,
    Unknown
}

/// <summary>
/// Immutable actor snapshot attached to a run at admission.
/// </summary>
public sealed record ScheduleActor(ScheduleActorKind Kind, Guid? AccountId, string? Username);

public sealed class ScheduleExecutionResponse
{
    public IReadOnlyList<ScheduleExecution> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

internal sealed class ScheduleActorKindJsonConverter : JsonStringEnumConverter<ScheduleActorKind>
{
    public ScheduleActorKindJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

internal sealed class ScheduleRunTriggerJsonConverter : JsonStringEnumConverter<RunTrigger>
{
    public ScheduleRunTriggerJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}
