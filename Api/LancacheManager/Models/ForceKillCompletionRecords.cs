namespace LancacheManager.Models;

/// <summary>
/// Strongly-typed SignalR completion payloads emitted by
/// <see cref="Core.Services.OperationCancellationService"/> when an operation is force-killed
/// (critic-4: no anonymous payloads). Each record's property names mirror the previous anonymous
/// objects exactly so the wire contract is unchanged — they serialize camelCase via the global
/// <c>JsonNamingPolicy.CamelCase</c> policy.
/// </summary>
public record CorruptionDetectionCancelled(Guid OperationId, bool Success, string Status, string StageKey, bool Cancelled);

/// <summary>
/// Force-kill completion payload for <see cref="OperationType.DataImport"/>
/// (emitted on the <c>DataImportComplete</c> SignalR event). See <see cref="CorruptionDetectionCancelled"/>.
/// </summary>
public record DataImportCancelled(Guid OperationId, bool Success, string Message, bool Cancelled);

/// <summary>
/// Force-kill completion payload for <see cref="OperationType.EpicMapping"/>
/// (emitted on the <c>EpicMappingProgress</c> SignalR event). See <see cref="CorruptionDetectionCancelled"/>.
/// </summary>
public record EpicMappingCancelled(Guid OperationId, bool Success, string Status, string StageKey, bool Cancelled);

/// <summary>
/// Force-kill completion payload for <see cref="OperationType.DepotMapping"/>
/// (emitted on the <c>DepotMappingComplete</c> SignalR event). See <see cref="CorruptionDetectionCancelled"/>.
/// </summary>
public record DepotMappingCancelled(Guid OperationId, bool Success, string Status, bool Cancelled);
