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
    Func<Guid, object> BuildCancelled,
    Func<Guid, Exception, object> BuildErrorProgress,
    Func<Guid, Exception, IOperationComplete> BuildErrorComplete,
    Func<Guid, CancellationToken, Func<RemovalProgressUpdate, Task>, Task<TReport>> ExecuteAsync,
    Func<Guid, OperationRepair> BuildRepair,
    Func<OperationRepair, CancellationToken, Task> PrepareRepairAsync,
    Func<Guid, bool, bool, string?, Task> FinishRepairAsync,
    Action<RemovalMetrics, RemovalProgressUpdate>? ApplyProgressMetrics = null,
    Action<RemovalMetrics, TReport>? ApplyFinalMetrics = null,
    Func<TReport, Task>? OnSuccessAsync = null,
    Action<Guid, TReport>? LogSuccess = null,
    Action<Guid>? LogCancelled = null,
    Action<Guid, Exception>? LogFailure = null);
