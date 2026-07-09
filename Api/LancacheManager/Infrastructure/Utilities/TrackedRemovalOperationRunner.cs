using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

internal static class TrackedRemovalOperationRunner
{
    internal sealed record RemovalProgressUpdate(
        double PercentComplete,
        string StageKey,
        Dictionary<string, object?>? Context = null,
        int FilesDeleted = 0,
        long BytesFreed = 0);

    internal sealed record RemovalOperationConfig<TReport>(
        OperationType OperationType,
        string OperationLabel,
        RemovalMetrics Metadata,
        string StartedEventName,
        Func<Guid, object> BuildStartedPayload,
        string ProgressEventName,
        string InitialStageKey,
        Func<Guid, object> BuildInitialProgressPayload,
        Func<Guid, RemovalProgressUpdate, object> BuildProgressPayload,
        string CompleteEventName,
        string FinalizingStageKey,
        Func<Guid, TReport, object> BuildFinalizingProgressPayload,
        Func<Guid, TReport, object> BuildSuccessPayload,
        Func<Guid, object> BuildCancelledPayload,
        Func<Guid, Exception, object> BuildErrorProgressPayload,
        Func<Guid, Exception, IOperationComplete> BuildErrorCompletePayload,
        Func<Guid, CancellationToken, Func<RemovalProgressUpdate, Task>, Task<TReport>> ExecuteAsync,
        Action<RemovalMetrics, RemovalProgressUpdate>? ApplyProgressMetrics = null,
        Action<RemovalMetrics, TReport>? ApplyFinalMetrics = null,
        Func<TReport, Task>? OnSuccessAsync = null,
        Action<Guid, TReport>? LogSuccess = null,
        Action<Guid>? LogCancelled = null,
        Action<Guid, Exception>? LogFailure = null);

    internal static async Task<Guid> StartAsync<TReport>(
        IUnifiedOperationTracker operationTracker,
        ISignalRNotificationService notifications,
        RemovalOperationConfig<TReport> config)
    {
        var cancellationTokenSource = new CancellationTokenSource();

        // pr2-B: the terminal Complete event is emitted EXACTLY ONCE from inside CompleteOperation
        // (CompletedFlag-gated) via this onTerminalEmit closure — never directly from the worker body.
        // The success/error payload factories need data only known at completion (TReport / Exception),
        // so the worker captures them into these locals BY VALUE just before calling CompleteOperation,
        // and the closure reads them. operationId is captured below once RegisterOperation returns.
        Guid operationId = Guid.Empty;
        TReport? capturedReport = default;
        Exception? capturedException = null;

        operationId = operationTracker.RegisterOperation(
            config.OperationType,
            config.OperationLabel,
            cancellationTokenSource,
            config.Metadata,
            onTerminalEmit: info => info.Cancelled
                ? notifications.NotifyAllAsync(
                    config.CompleteEventName,
                    config.BuildCancelledPayload(operationId))
                : info.Success
                    ? notifications.NotifyAllAsync(
                        config.CompleteEventName,
                        config.BuildSuccessPayload(operationId, capturedReport!))
                    // Genuine failure (not cancel/success) → the uniform failure broadcast: central
                    // LogWarning + guaranteed IOperationComplete shape, still through the one send path.
                    : notifications.NotifyOperationFailedAsync(
                        config.CompleteEventName,
                        config.BuildErrorCompletePayload(
                            operationId,
                            capturedException ?? new Exception(info.Error ?? "Operation failed"))));

        await notifications.NotifyAllAsync(config.StartedEventName, config.BuildStartedPayload(operationId));

        _ = Task.Run(async () =>
        {
            try
            {
                var cancellationToken = cancellationTokenSource.Token;
                cancellationToken.ThrowIfCancellationRequested();

                await notifications.NotifyAllAsync(
                    config.ProgressEventName,
                    config.BuildInitialProgressPayload(operationId));
                operationTracker.UpdateProgress(operationId, 0, config.InitialStageKey);

                cancellationToken.ThrowIfCancellationRequested();

                var report = await config.ExecuteAsync(
                    operationId,
                    cancellationToken,
                    async update =>
                    {
                        await notifications.NotifyAllAsync(
                            config.ProgressEventName,
                            config.BuildProgressPayload(operationId, update));
                        operationTracker.UpdateProgress(operationId, update.PercentComplete, update.StageKey);

                        if (config.ApplyProgressMetrics != null)
                        {
                            operationTracker.UpdateMetadata(operationId, metadata =>
                            {
                                config.ApplyProgressMetrics((RemovalMetrics)metadata, update);
                            });
                        }
                    });

                cancellationToken.ThrowIfCancellationRequested();

                await notifications.NotifyAllAsync(
                    config.ProgressEventName,
                    config.BuildFinalizingProgressPayload(operationId, report));
                operationTracker.UpdateProgress(operationId, 100.0, config.FinalizingStageKey);

                if (config.ApplyFinalMetrics != null)
                {
                    operationTracker.UpdateMetadata(operationId, metadata =>
                    {
                        config.ApplyFinalMetrics((RemovalMetrics)metadata, report);
                    });
                }

                if (config.OnSuccessAsync != null)
                {
                    await config.OnSuccessAsync(report);
                }

                config.LogSuccess?.Invoke(operationId, report);

                // Capture the report BY VALUE before completing so the onTerminalEmit closure
                // (fired inside CompleteOperation) can build the success payload. The runner no
                // longer emits the Complete event directly — that happens exactly once in the tracker.
                capturedReport = report;
                operationTracker.CompleteOperation(operationId, success: true);
            }
            catch (OperationCanceledException)
            {
                config.LogCancelled?.Invoke(operationId);
                // onTerminalEmit sends the cancelled Complete event (info.Cancelled) — no direct emit here.
                operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
            }
            catch (Exception ex)
            {
                config.LogFailure?.Invoke(operationId, ex);

                // Progress (error) emit is NOT terminal — keep it.
                await notifications.NotifyAllAsync(
                    config.ProgressEventName,
                    config.BuildErrorProgressPayload(operationId, ex));

                // Capture the exception so the onTerminalEmit closure can build the error Complete payload.
                capturedException = ex;
                operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
            }
            // core-3: no finally-dispose of the CTS here — the tracker owns its lifetime and disposes
            // it inside CompleteOperation. Disposing it from the worker would race the tracker's
            // cancel/force-kill path and double-dispose.
        }, cancellationTokenSource.Token);

        return operationId;
    }
}
