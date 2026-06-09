using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services;

/// <summary>
/// Centralizes aggressive operation cancellation and force-kill for ALL operation types so the dead
/// per-service <c>ForceKill*</c> endpoints are no longer needed. Force-kill flow:
/// graceful CANCEL to the Rust child (await its real exit, escalate to a hard kill on timeout) →
/// token cancel → single SignalR completion → tracker cleanup (which runs the owning service's
/// <see cref="Models.OperationInfo.OnTerminalCleanup"/> and disposes the CTS exactly once).
/// </summary>
public class OperationCancellationService
{
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly ISignalRNotificationService _notifications;
    private readonly ProcessManager _processManager;
    private readonly ILogger<OperationCancellationService> _logger;

    public OperationCancellationService(
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        ProcessManager processManager,
        ILogger<OperationCancellationService> logger)
    {
        _operationTracker = operationTracker;
        _notifications = notifications;
        _processManager = processManager;
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

        // Capture the process BEFORE ForceKillOperation nulls AssociatedProcess.
        var process = operation.AssociatedProcess;

        try
        {
            if (process is { HasExited: false })
            {
                // P2-B / rust-kill-6: graceful-then-force, awaiting the REAL exit (replaces the blind
                // Task.Delay(500)). Writes "CANCEL" to stdin, waits up to the grace period, then escalates
                // to a hard kill and waits for the process tree to actually exit.
                await _processManager.GracefulCancelAsync(process, TimeSpan.FromSeconds(5), $"force-kill op {operationId}");
            }
        }
        catch (ObjectDisposedException)
        {
            // A racing CompleteOperation disposed the Process between capture and the HasExited
            // check (or during GracefulCancelAsync). The op is already finishing — treat as exited.
            _logger.LogDebug("Process for operation {Id} was disposed concurrently during force kill — treating as already exited", operationId);
        }

        _operationTracker.ForceKillOperation(operationId); // cancels token, best-effort kill (idempotent via HasExited)

        var current = _operationTracker.GetOperation(operationId);
        if (current == null
            || current.Status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled)
        {
            // The worker observed cancellation and already completed the op (A.3 flag). Avoid a duplicate
            // SignalR completion — the op is already terminal.
            return true;
        }

        await NotifyForceKillCompleteAsync(current);
        _operationTracker.CompleteOperation(operationId, success: false, error: "Force killed by user");
        return true;
    }

    private async Task NotifyForceKillCompleteAsync(OperationInfo operation)
    {
        // MIGRATED ops own a single terminal emitter: their registered OnTerminalEmit fires the
        // terminal SignalR event EXACTLY ONCE from inside CompleteOperation (CompletedFlag-gated),
        // which ForceKillAsync calls immediately AFTER this method. Emitting here too would
        // double-fire, so this legacy switch is a NO-OP for them. UNMIGRATED ops (depot_mapping,
        // epic_game_mapping, data_import, ...) have no OnTerminalEmit yet and keep the switch below.
        if (operation.OnTerminalEmit != null)
        {
            return;
        }

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
                await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionComplete,
                    new CorruptionDetectionCancelled(
                        OperationId: operationId,
                        Success: false,
                        Status: OperationStatus.Cancelled.ToWireString(),
                        StageKey: "signalr.corruptionDetect.cancelled",
                        Cancelled: true));
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
                var service = GetMetadataString(operation.Metadata, "service")
                              ?? (operation.Metadata as RemovalMetrics)?.EntityName
                              ?? "unknown";
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
                await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete,
                    new DataImportCancelled(
                        OperationId: operationId,
                        Success: false,
                        Message: "Import was cancelled",
                        Cancelled: true));
                break;

            case OperationType.EpicMapping:
                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress,
                    new EpicMappingCancelled(
                        OperationId: operationId,
                        Success: false,
                        Status: OperationStatus.Cancelled.ToWireString(),
                        StageKey: "signalr.epicMapping.cancelled",
                        Cancelled: true));
                break;

            case OperationType.DepotMapping:
                await _notifications.NotifyAllAsync(SignalREvents.DepotMappingComplete,
                    new DepotMappingCancelled(
                        OperationId: operationId,
                        Success: false,
                        Status: OperationStatus.Cancelled.ToWireString(),
                        Cancelled: true));
                break;

            default:
                _logger.LogDebug(
                    "No typed force-kill notification for operation type {Type} ({Id})",
                    operation.Type, operationId);
                break;
        }
    }

    private static string? GetMetadataString(object? metadata, string key)
    {
        if (metadata == null)
        {
            return null;
        }

        if (metadata is System.Collections.Generic.IDictionary<string, object?> dict
            && dict.TryGetValue(key, out var value))
        {
            return value?.ToString();
        }

        var prop = metadata.GetType().GetProperty(key);
        return prop?.GetValue(metadata)?.ToString();
    }
}
