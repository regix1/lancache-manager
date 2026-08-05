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

    public MappingOperationReporter(
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker tracker,
        MappingOperationDefinition definition,
        bool showNotification,
        CancellationToken stoppingToken,
        ILogger logger,
        ScheduledRunPayloadFactories? payloadFactories = null,
        Action? onTerminalCleanup = null,
        bool bestEffortNotifications = false)
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
            bestEffortNotifications,
            info =>
                $"{definition.StageKeyPrefix}.{GetTerminalSuffix(info.Success, info.Cancelled)}");
    }

    public Guid OperationId => _inner.OperationId;

    public bool IsStarted => _inner.IsStarted;

    public CancellationToken Token => _inner.Token;

    public Task StartAsync(Dictionary<string, object?>? context = null, string? stageKey = null) =>
        _inner.StartAsync(stageKey ?? $"{_definition.StageKeyPrefix}.starting", context);

    public Task ReportAsync(
        double percent,
        string stageKey,
        Dictionary<string, object?>? context = null,
        OperationStatus status = OperationStatus.Running) =>
        _inner.ReportAsync(percent, stageKey, context, status);

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
