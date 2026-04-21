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
        Func<Guid, Exception, object> BuildErrorCompletePayload,
        Func<CancellationToken, Func<RemovalProgressUpdate, Task>, Task<TReport>> ExecuteAsync,
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
        var operationId = operationTracker.RegisterOperation(
            config.OperationType,
            config.OperationLabel,
            cancellationTokenSource,
            config.Metadata);

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

                operationTracker.CompleteOperation(operationId, success: true);

                await notifications.NotifyAllAsync(
                    config.CompleteEventName,
                    config.BuildSuccessPayload(operationId, report));
            }
            catch (OperationCanceledException)
            {
                config.LogCancelled?.Invoke(operationId);
                operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");

                await notifications.NotifyAllAsync(
                    config.CompleteEventName,
                    config.BuildCancelledPayload(operationId));
            }
            catch (Exception ex)
            {
                config.LogFailure?.Invoke(operationId, ex);

                await notifications.NotifyAllAsync(
                    config.ProgressEventName,
                    config.BuildErrorProgressPayload(operationId, ex));

                operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);

                await notifications.NotifyAllAsync(
                    config.CompleteEventName,
                    config.BuildErrorCompletePayload(operationId, ex));
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }, cancellationTokenSource.Token);

        return operationId;
    }
}
