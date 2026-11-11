using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

internal sealed record RemovalProgressUpdate(
    double PercentComplete,
    string StageKey,
    Dictionary<string, object?>? Context = null,
    int FilesDeleted = 0,
    long BytesFreed = 0);

internal sealed record RemovalOperationConfig<TReport>(
    OperationType OperationType,
    string OperationLabel,
    string Target,
    RemovalMetrics Metrics,
    string StartedEventName,
    Func<Guid, object> BuildStarted,
    string ProgressEventName,
    string InitialStageKey,
    Func<Guid, object> BuildInitialProgress,
    Func<Guid, RemovalProgressUpdate, object> BuildProgress,
    string CompleteEventName,
    string FinalizingStageKey,
    Func<Guid, TReport, object> BuildFinalizingProgress,
    Func<Guid, TReport, object> BuildSuccess,
    Func<Guid, object> BuildCanceled,
    Func<Guid, Exception, object> BuildErrorProgress,
    Func<Guid, Exception, IOperationComplete> BuildErrorComplete,
    Func<Guid, CancellationToken, Func<RemovalProgressUpdate, Task>, Task<TReport>> ExecuteAsync,
    Func<Guid, OperationRepair> BuildRepair,
    Func<OperationRepair, CancellationToken, Task> PrepareRepairAsync,
    // The last argument is the failure's key and values, saved with the outcome so the ending, live or
    // restored, words the failure with them.
    Func<Guid, bool, bool, string?, RunWarning?, Task> FinishRepairAsync,
    Action<RemovalMetrics, RemovalProgressUpdate>? ApplyProgressMetrics = null,
    Action<RemovalMetrics, TReport>? ApplyFinalMetrics = null,
    Func<TReport, Task>? OnSuccessAsync = null,
    Action<Guid, TReport>? LogSuccess = null,
    Action<Guid>? LogCanceled = null,
    Action<Guid, Exception>? LogFailure = null);
