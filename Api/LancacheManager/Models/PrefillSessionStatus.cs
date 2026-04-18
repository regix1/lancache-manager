using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Status values for a persisted <see cref="PrefillSession"/> record.
/// Serialized as PascalCase strings on the wire to preserve the existing JSON contract.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrefillSessionStatus
{
    Active,
    Terminated,
    Orphaned,
    Cleaned,
    Cancelled
}

/// <summary>
/// Status values for a <see cref="PrefillHistoryEntry"/> record.
/// Serialized as PascalCase strings on the wire to preserve the existing JSON contract.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PrefillHistoryEntryStatus
{
    InProgress,
    Completed,
    Cached,
    Failed,
    Cancelled
}
