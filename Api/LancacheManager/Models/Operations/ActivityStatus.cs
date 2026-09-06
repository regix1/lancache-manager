namespace LancacheManager.Models;

/// <summary>
/// Canonical domain identifiers for a unified activity/presence entry. Kept as string constants (not
/// an enum) so the SignalR wire value is deterministic and matches the frontend <c>ActivityDomain</c>
/// union exactly, independent of any enum-serialization configuration.
/// </summary>
public static class ActivityDomains
{
    public const string Schedule = "schedule";
    public const string Operation = "operation";
    public const string UserSession = "userSession";
    public const string PrefillSession = "prefillSession";
    public const string PersistentContainer = "persistentContainer";
    public const string Integration = "integration";
    public const string Download = "download";
}

/// <summary>
/// The kind of "active" state an entry represents. A single entity may report more than one aspect
/// (e.g. an integration can be both <see cref="Present"/> and <see cref="Authenticated"/>).
/// </summary>
public static class ActivityAspects
{
    public const string Running = "running";
    public const string Present = "present";
    public const string Authenticated = "authenticated";
    public const string Connected = "connected";
    public const string Downloading = "downloading";
}

/// <summary>
/// One unified activity/presence entry. Identity is (<see cref="Domain"/>, <see cref="Key"/>,
/// <see cref="Aspect"/>). Only active entries appear in a broadcast snapshot; an entity that drops out
/// of the snapshot is treated as inactive by consumers.
/// </summary>
public sealed record ActivityItem
{
    public required string Domain { get; init; }
    public required string Key { get; init; }
    public required string Aspect { get; init; }
    public bool IsActive { get; init; } = true;
    public int ActiveCount { get; init; } = 1;
}

/// <summary>
/// Full activity snapshot broadcast over <see cref="Hubs.SignalREvents.ActivityUpdated"/>.
/// <see cref="Revision"/> is a process-monotonic counter so a client can ignore an out-of-order older
/// snapshot (the same reorder guard the Schedules dot uses).
/// </summary>
public sealed record ActivitySnapshot
{
    public required long Revision { get; init; }
    public required IReadOnlyList<ActivityItem> Activities { get; init; }
}
