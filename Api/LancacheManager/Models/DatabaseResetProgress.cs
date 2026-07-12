namespace LancacheManager.Models;

/// <summary>Shared typed SignalR payload for both full and selected-table database resets.</summary>
public sealed record DatabaseResetProgress(
    Guid? OperationId,
    bool IsProcessing,
    double PercentComplete,
    OperationStatus Status,
    string StageKey,
    string? Message,
    int? TablesCleared,
    int? TotalTables,
    int? FilesDeleted,
    DateTime Timestamp,
    IReadOnlyDictionary<string, object?> Context);
