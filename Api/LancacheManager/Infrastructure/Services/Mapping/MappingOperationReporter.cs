using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Thin mapping-specific facade over <see cref="ScheduledRunReporter"/>. It binds a producer to one
/// canonical mapping definition and supplies the standard starting/completed stage keys.
/// </summary>
public sealed class MappingOperationReporter : IAsyncDisposable
{
    private readonly MappingOperationDefinition _definition;
    private readonly ScheduledRunReporter _inner;
    private bool _progressSuppressed;

    public MappingOperationReporter(
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker,
        MappingOperationDefinition definition,
        bool showNotification,
        CancellationToken stoppingToken,
        ILogger logger,
        ScheduledRunPayloadFactories? payloadFactories = null,
        Action? onTerminalCleanup = null)
    {
        _definition = definition;
        _inner = new ScheduledRunReporter(
            notifications,
            tracker,
            definition.ServiceKey,
            definition.OperationType,
            definition.Events,
            $"{definition.StageKeyPrefix}.completed",
            showNotification,
            stoppingToken,
            payloadFactories,
            onTerminalCleanup,
            logger,
            info =>
                $"{definition.StageKeyPrefix}.{GetTerminalSuffix(info.Success, info.Cancelled)}");
    }

    public Guid OperationId => _inner.OperationId;

    public bool IsStarted => _inner.IsStarted;

    public CancellationToken Token => _inner.Token;

    public Task StartAsync(Dictionary<string, object?>? context = null, string? stageKey = null) =>
        _inner.StartAsync(stageKey ?? $"{_definition.StageKeyPrefix}.starting", context);

    /// <summary>
    /// Stops <see cref="ReportAsync"/> emitting anything for the rest of the run. Call it as soon as
    /// the pass knows it may finish as skipped: a run that ends up reporting it did nothing must not
    /// have claimed progress on the way there, or the card shows a bar climbing to 90% and then says
    /// the run changed nothing. One-way on purpose - a pass that has already gone quiet has nothing
    /// truthful to say about progress later.
    /// </summary>
    public void SuppressProgress() => _progressSuppressed = true;

    public Task ReportAsync(
        double percent,
        string stageKey,
        Dictionary<string, object?>? context = null,
        OperationStatus status = OperationStatus.Running) =>
        _progressSuppressed
            ? Task.CompletedTask
            : _inner.ReportAsync(percent, stageKey, context, status);

    /// <summary>
    /// Terminal for a run that did nothing. Reports success (nothing went wrong) with the skipped
    /// flag the wait queue and the notification card read, so the run says it changed nothing instead
    /// of claiming a completed pass. Every mapping service that can decline work ends this way.
    /// </summary>
    public Task CompleteSkippedAsync(string stageKey, Dictionary<string, object?>? context = null) =>
        CompleteAsync(success: true, stageKey: stageKey, context: context, skipped: true);

    public Task CompleteAsync(
        bool success,
        string? error = null,
        bool cancelled = false,
        string? stageKey = null,
        Dictionary<string, object?>? context = null,
        bool skipped = false) =>
        _inner.CompleteAsync(
            success,
            error,
            cancelled,
            stageKey ?? $"{_definition.StageKeyPrefix}.{GetTerminalSuffix(success, cancelled)}",
            context,
            skipped);

    public bool RequestCancellation() => _inner.RequestCancellation();

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private static string GetTerminalSuffix(bool success, bool cancelled) =>
        cancelled ? "cancelled" : success ? "completed" : "failed";
}
