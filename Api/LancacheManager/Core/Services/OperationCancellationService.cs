using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

/// <summary>
/// Centralizes aggressive operation cancellation and force-kill, mirroring the patterns used by
/// <see cref="CacheClearingService.ForceKillOperationAsync"/>,
/// <see cref="Infrastructure.Services.RustLogProcessorService.ForceKillProcessingAsync"/>, and
/// <see cref="Infrastructure.Services.RustLogRemovalService.ForceKillOperationAsync"/>:
/// cancel token → kill process tree → brief wait → SignalR completion → tracker cleanup.
/// </summary>
public class OperationCancellationService
{
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<OperationCancellationService> _logger;

    public OperationCancellationService(
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        ILogger<OperationCancellationService> logger)
    {
        _operationTracker = operationTracker;
        _notifications = notifications;
        _logger = logger;
    }

    /// <summary>
    /// Aggressive cancel — terminates any associated process tree, then cancels the token.
    /// Matches log-processor <c>CancelProcessingAsync</c> (kill + cancel).
    /// </summary>
    public bool Cancel(Guid operationId)
    {
        return _operationTracker.CancelOperation(operationId);
    }

    /// <summary>
    /// Force kill fallback when cancel alone does not unblock the UI (e.g. stuck managed post-processing).
    /// </summary>
    public async Task<bool> ForceKillAsync(Guid operationId)
    {
        var operation = _operationTracker.GetOperation(operationId);
        if (operation == null)
        {
            _logger.LogWarning("Force kill requested for unknown operation {Id}", operationId);
            return false;
        }

        _logger.LogWarning(
            "Force killing operation {Id} ({Type}: {Name})",
            operationId, operation.Type, operation.Name);

        _operationTracker.ForceKillOperation(operationId);

        // Match cache-clear / log-removal force-kill: give the OS a moment to reap the process.
        await Task.Delay(500);

        // If the worker already completed, nothing left to do.
        if (_operationTracker.GetOperation(operationId) == null)
        {
            return true;
        }

        await NotifyForceKillCompleteAsync(operation);
        _operationTracker.CompleteOperation(operationId, success: false, error: "Force killed by user");
        return true;
    }

    private async Task NotifyForceKillCompleteAsync(OperationInfo operation)
    {
        var operationId = operation.Id;

        switch (operation.Type)
        {
            case OperationType.EvictionScan:
                await _notifications.NotifyAllAsync(SignalREvents.EvictionScanComplete, new EvictionScanComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionScan.complete",
                    Processed: 0,
                    Evicted: 0,
                    UnEvicted: 0,
                    Error: "Cancelled by user"));
                break;

            case OperationType.EvictionRemoval:
                await _notifications.NotifyAllAsync(SignalREvents.EvictionRemovalComplete, new EvictionRemovalComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.evictionRemove.cancelled",
                    DownloadsRemoved: 0,
                    LogEntriesRemoved: 0,
                    Error: "Cancelled by user",
                    Cancelled: true));
                break;

            case OperationType.CorruptionRemoval:
            {
                var service = (operation.Metadata as RemovalMetrics)?.EntityName
                              ?? (operation.Metadata as RemovalMetrics)?.EntityKey
                              ?? "unknown";
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionRemovalComplete,
                    new CorruptionRemovalComplete(
                        Success: false,
                        Service: service,
                        StageKey: "signalr.corruptionRemove.cancelled",
                        OperationId: operationId));
                break;
            }

            case OperationType.CorruptionDetection:
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionComplete, new
                {
                    OperationId = operationId,
                    Success = false,
                    Status = OperationStatus.Cancelled.ToWireString(),
                    StageKey = "signalr.corruptionDetect.cancelled",
                    Cancelled = true
                });
                break;

            case OperationType.GameDetection:
                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete,
                    new GameDetectionComplete(
                        Success: false,
                        OperationId: operationId,
                        StageKey: "signalr.generic.failed"));
                break;

            case OperationType.CacheClearing:
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.CacheClearingComplete,
                    operationId,
                    success: false,
                    message: "Cache clear operation force killed",
                    cancelled: true);
                break;

            case OperationType.LogProcessing:
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogProcessingComplete,
                    operationId,
                    success: false,
                    message: "Log processing was cancelled",
                    cancelled: true);
                break;

            case OperationType.LogRemoval:
            {
                var service = (operation.Metadata as RemovalMetrics)?.EntityName ?? "unknown";
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.LogRemovalComplete,
                    operationId,
                    success: false,
                    message: $"Service removal for {service} was cancelled",
                    cancelled: true,
                    extraData: new { Service = service });
                break;
            }

            case OperationType.GameRemoval:
            case OperationType.ServiceRemoval:
                await _notifications.SendOperationCompleteAsync(
                    operation.Type == OperationType.GameRemoval
                        ? SignalREvents.GameRemovalComplete
                        : SignalREvents.ServiceRemovalComplete,
                    operationId,
                    success: false,
                    message: "Removal was cancelled",
                    cancelled: true);
                break;

            case OperationType.DatabaseReset:
                await _notifications.SendOperationCompleteAsync(
                    SignalREvents.DatabaseResetComplete,
                    operationId,
                    success: false,
                    message: "Database reset was cancelled",
                    cancelled: true);
                break;

            case OperationType.DataImport:
                await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
                {
                    OperationId = operationId,
                    Success = false,
                    Message = "Import was cancelled",
                    Cancelled = true
                });
                break;

            case OperationType.EpicMapping:
                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    OperationId = operationId,
                    Success = false,
                    Status = OperationStatus.Cancelled.ToWireString(),
                    StageKey = "signalr.epicMapping.cancelled",
                    Cancelled = true
                });
                break;

            case OperationType.DepotMapping:
                await _notifications.NotifyAllAsync(SignalREvents.DepotMappingComplete, new
                {
                    OperationId = operationId,
                    Success = false,
                    Status = OperationStatus.Cancelled.ToWireString(),
                    Cancelled = true
                });
                break;

            default:
                _logger.LogDebug(
                    "No typed force-kill notification for operation type {Type} ({Id})",
                    operation.Type, operationId);
                break;
        }
    }
}
