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
        var cancellationToken = cancellationTokenSource.Token;

        // Only the winning terminal claim publishes the report used by this run's emitter.
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
                cancellationToken.ThrowIfCancellationRequested();

                var initialAccepted = false;
                operationTracker.UpdateProgress(operationId, 0, config.InitialStageKey,
                    onProgress: _ => initialAccepted = true);
                if (initialAccepted)
                {
                    await notifications.NotifyAllAsync(
                        config.ProgressEventName,
                        config.BuildInitialProgressPayload(operationId));
                }

                cancellationToken.ThrowIfCancellationRequested();

                var report = await config.ExecuteAsync(
                    operationId,
                    cancellationToken,
                    async update =>
                    {
                        var captured = update with
                        {
                            Context = update.Context == null ? null : new(update.Context)
                        };
                        var accepted = false;
                        operationTracker.UpdateProgress(operationId, captured.PercentComplete, captured.StageKey,
                            onProgress: _ =>
                            {
                                config.ApplyProgressMetrics?.Invoke(config.Metadata, captured);
                                accepted = true;
                            });
                        if (accepted)
                        {
                            await notifications.NotifyAllAsync(
                                config.ProgressEventName,
                                config.BuildProgressPayload(operationId, captured));
                        }
                    });

                cancellationToken.ThrowIfCancellationRequested();

                var finalizingAccepted = false;
                operationTracker.UpdateProgress(operationId, 100.0, config.FinalizingStageKey,
                    onProgress: _ => finalizingAccepted = true);
                if (finalizingAccepted)
                {
                    await notifications.NotifyAllAsync(
                        config.ProgressEventName,
                        config.BuildFinalizingProgressPayload(operationId, report));
                }

                if (config.OnSuccessAsync != null)
                {
                    await config.OnSuccessAsync(report);
                }

                config.LogSuccess?.Invoke(operationId, report);

                // Capture the report BY VALUE before completing so the onTerminalEmit closure
                // (fired inside CompleteOperation) can build the success payload. The runner no
                // longer emits the Complete event directly — that happens exactly once in the tracker.
                operationTracker.CompleteOperation(operationId, success: true, onCompleting: _ =>
                {
                    capturedReport = report;
                    config.ApplyFinalMetrics?.Invoke(config.Metadata, report);
                });
            }
            catch (OperationCanceledException)
            {
                config.LogCancelled?.Invoke(operationId);
                // onTerminalEmit sends the cancelled Complete event (info.Cancelled) — no direct emit here.
                operationTracker.CompleteOperation(operationId, success: false, cancelled: true);
            }
            catch (Exception ex)
            {
                config.LogFailure?.Invoke(operationId, ex);

                var errorAccepted = false;
                operationTracker.UpdateProgress(operationId,
                    operationTracker.GetOperation(operationId)?.PercentComplete ?? 0, ex.Message,
                    onProgress: _ => errorAccepted = true);
                if (errorAccepted)
                {
                    await notifications.NotifyAllAsync(
                        config.ProgressEventName,
                        config.BuildErrorProgressPayload(operationId, ex));
                }

                // Capture the exception so the onTerminalEmit closure can build the error Complete payload.
                operationTracker.CompleteOperation(operationId, success: false, error: ex.Message,
                    onCompleting: _ => capturedException = ex);
            }
            // The tracker owns cancellation-source disposal.
        }, cancellationToken);

        return operationId;
    }
}
