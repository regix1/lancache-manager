using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

internal static class TrackedRemovalOperationRunner
{
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
            config.Metrics,
            onTerminalEmit: info => info.Cancelled
                ? notifications.NotifyAllAsync(
                    config.CompleteEventName,
                    config.BuildCancelled(operationId))
                : info.Success
                    ? notifications.NotifyAllAsync(
                        config.CompleteEventName,
                        config.BuildSuccess(operationId, capturedReport!))
                    // Genuine failure (not cancel/success) → the uniform failure broadcast: central
                    // LogWarning + guaranteed IOperationComplete shape, still through the one send path.
                    : notifications.NotifyOperationFailedAsync(
                        config.CompleteEventName,
                        config.BuildErrorComplete(
                            operationId,
                            GetTerminalError(capturedException, info.Error))),
            ownerCompletes: true,
            target: config.Target);

        await notifications.NotifyAllAsync(config.StartedEventName, config.BuildStarted(operationId));

        _ = Task.Run(async () =>
        {
            var repairPrepared = false;
            var finishingRepair = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                var repair = config.BuildRepair(operationId);
                await config.PrepareRepairAsync(repair, cancellationToken);
                repairPrepared = true;

                var initialAccepted = false;
                operationTracker.UpdateProgress(operationId, 0, config.InitialStageKey,
                    onProgress: _ => initialAccepted = true);
                if (initialAccepted)
                {
                    await notifications.NotifyAllAsync(
                        config.ProgressEventName,
                        config.BuildInitialProgress(operationId));
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
                                config.ApplyProgressMetrics?.Invoke(config.Metrics, captured);
                                accepted = true;
                            });
                        if (accepted)
                        {
                            await notifications.NotifyAllAsync(
                                config.ProgressEventName,
                                config.BuildProgress(operationId, captured));
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
                        config.BuildFinalizingProgress(operationId, report));
                }

                if (config.OnSuccessAsync != null)
                {
                    await config.OnSuccessAsync(report);
                }

                var current = operationTracker.GetOperation(operationId);
                if (current?.Status.IsTerminal() == true)
                {
                    finishingRepair = true;
                    await config.FinishRepairAsync(
                        operationId,
                        current.Status == OperationStatus.Completed,
                        current.Status == OperationStatus.Cancelled,
                        current.Status == OperationStatus.Failed ? current.Message : null);
                    finishingRepair = false;
                    return;
                }

                finishingRepair = true;
                await config.FinishRepairAsync(
                    operationId,
                    true,
                    false,
                    null);
                finishingRepair = false;

                config.LogSuccess?.Invoke(operationId, report);

                // Capture the report BY VALUE before completing so the onTerminalEmit closure
                // (fired inside CompleteOperation) can build the success payload. The runner no
                // longer emits the Complete event directly — that happens exactly once in the tracker.
                operationTracker.CompleteOperation(operationId, success: true, onCompleting: _ =>
                {
                    capturedReport = report;
                    config.ApplyFinalMetrics?.Invoke(config.Metrics, report);
                });
            }
            catch (OperationCanceledException) when (finishingRepair)
            {
                // Application shutdown owns the unfinished retained repair. A later startup restores it.
            }
            catch (OperationCanceledException)
            {
                if (repairPrepared)
                {
                    try
                    {
                        finishingRepair = true;
                        await config.FinishRepairAsync(
                            operationId,
                            false,
                            true,
                            null);
                        finishingRepair = false;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        config.LogFailure?.Invoke(operationId, ex);
                        return;
                    }
                }

                config.LogCancelled?.Invoke(operationId);
                // onTerminalEmit sends the cancelled Complete event (info.Cancelled) — no direct emit here.
                operationTracker.CompleteOperation(operationId, success: false, cancelled: true);
            }
            catch (Exception ex) when (finishingRepair)
            {
                config.LogFailure?.Invoke(operationId, ex);
            }
            catch (Exception ex)
            {
                if (repairPrepared)
                {
                    try
                    {
                        finishingRepair = true;
                        await config.FinishRepairAsync(
                            operationId,
                            false,
                            false,
                            ex.Message);
                        finishingRepair = false;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception repairError)
                    {
                        config.LogFailure?.Invoke(operationId, repairError);
                        return;
                    }
                }

                config.LogFailure?.Invoke(operationId, ex);

                var errorAccepted = false;
                operationTracker.UpdateProgress(operationId,
                    operationTracker.GetOperation(operationId)?.PercentComplete ?? 0, ex.Message,
                    onProgress: _ => errorAccepted = true);
                if (errorAccepted)
                {
                    await notifications.NotifyAllAsync(
                        config.ProgressEventName,
                        config.BuildErrorProgress(operationId, ex));
                }

                // Capture the exception so the onTerminalEmit closure can build the error Complete payload.
                operationTracker.CompleteOperation(operationId, success: false, error: ex.Message,
                    onCompleting: _ => capturedException = ex);
            }
            // The tracker owns cancellation-source disposal.
        }, CancellationToken.None);

        return operationId;
    }

    private static Exception GetTerminalError(Exception? current, string? error)
    {
        if (current is not null)
        {
            return current;
        }
        if (error is null)
        {
            throw new InvalidDataException("Failed removal terminal has no error.");
        }
        return new Exception(error);
    }
}
