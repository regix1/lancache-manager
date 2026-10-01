using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services;

public partial class CacheManagementService
{
    public Task RestoreRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRemovalRepair(repair);
        var metrics = repair.Removal!;
        var source = new CancellationTokenSource();
        var restored = _operationTracker.TryRestoreOperation(
            repair.Id,
            repair.Type,
            repair.Name,
            source,
            metrics,
            onTerminalEmit: terminal => EmitRestoredRemovalAsync(repair, metrics, terminal),
            startedAt: repair.StartedAt,
            notice: repair.Notice,
            ownerCompletes: true);
        if (!restored)
        {
            source.Dispose();
        }
        return Task.CompletedTask;
    }

    public Task ResumeRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRemovalRepair(repair);
        return Task.CompletedTask;
    }

    private Task EmitRestoredRemovalAsync(
        OperationRepair repair,
        RemovalRepair metrics,
        OperationTerminalInfo terminal)
    {
        var target = repair.Target!;
        if (repair.Type == OperationType.ServiceRemoval)
        {
            var stageKey = terminal.Success
                ? "signalr.serviceRemove.success"
                : terminal.Cancelled
                    ? "signalr.serviceRemove.cancelled"
                    : "signalr.serviceRemove.failed.generic";
            return _notifications.NotifyAllAsync(
                SignalREvents.ServiceRemovalComplete,
                new ServiceRemovalComplete(
                    terminal.Success,
                    target.Service!,
                    repair.Id,
                    stageKey,
                    metrics.FilesDeleted,
                    metrics.BytesFreed,
                    metrics.LogEntriesRemoved,
                    new Dictionary<string, object?> { ["name"] = target.Service },
                    terminal.Error,
                    terminal.Cancelled));
        }

        var epic = target.EpicGame is not null;
        var named = target.GameName is not null;
        var gameStageKey = terminal.Success
            ? epic
                ? "signalr.epicRemove.complete"
                : named
                    ? "signalr.namedRemove.complete"
                    : "signalr.gameRemove.complete"
            : terminal.Cancelled
                ? epic
                    ? "signalr.epicRemove.cancelled"
                    : "signalr.gameRemove.cancelled"
                : epic
                    ? "signalr.epicRemove.error.fatal"
                    : "signalr.gameRemove.error.fatal";
        var gameName = epic ? target.EpicGame : named ? target.GameName : metrics.EntityName;
        return _notifications.NotifyAllAsync(
            SignalREvents.GameRemovalComplete,
            new GameRemovalComplete(
                terminal.Success,
                repair.Id,
                target.SteamAppId,
                epic ? metrics.EpicAppId : null,
                gameStageKey,
                gameName,
                metrics.FilesDeleted,
                metrics.BytesFreed,
                metrics.LogEntriesRemoved,
                new Dictionary<string, object?>
                {
                    ["gameName"] = gameName,
                    ["service"] = target.Service
                },
                terminal.Error,
                terminal.Cancelled));
    }

    private static void ValidateRemovalRepair(OperationRepair repair)
    {
        if (repair.Type is not (OperationType.GameRemoval or OperationType.ServiceRemoval)
            || repair.Removal is null
            || repair.Target is null)
        {
            throw new InvalidDataException(
                $"Operation repair {repair.Id} has no targeted-removal contract.");
        }
    }

    internal OperationRepair BuildRemovalRepair(
        Guid operationId,
        OperationType operationType,
        string name,
        RemovalMetrics metrics,
        CacheRepairTarget target)
    {
        return new OperationRepair
        {
            Id = operationId,
            Type = operationType,
            Name = name,
            StartedAt = DateTime.UtcNow,
            Target = target,
            Removal = CopyRemovalRepair(metrics),
            Sources = _datasourceService.GetDatasources()
                .Select(datasource => new OperationRepairSource
                {
                    Datasource = datasource.Name,
                    LogRoot = datasource.LogPath,
                    CacheRoot = datasource.CachePath,
                    KeyScheme = _capabilityService.GetKeySchemeWireValue(datasource),
                    ReceiptPath = Path.Combine(
                        datasource.CachePath,
                        $".lancache-repair-{operationId:N}.json"),
                    ResetLogPositions = true,
                    RefreshDownloads = true,
                    ReconcileCache = true,
                    RefreshDetection = true,
                    InvalidateCorruption = true
                })
                .ToList()
        };
    }

    internal Task PrepareRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        return _operationStateService.PrepareRepairAsync(repair, cancellationToken);
    }

    internal async Task FinishRemovalRepairAsync(
        Guid operationId,
        bool success,
        bool cancelled,
        string? error)
    {
        await _operationStateService.FinishRepairAsync(
            operationId,
            success,
            cancelled,
            error);
    }

    private async Task SaveRemovalSourceAsync(
        Guid? operationId,
        string datasource,
        int filesDeleted,
        long bytesFreed,
        ulong logEntriesRemoved)
    {
        if (!operationId.HasValue)
        {
            return;
        }

        await _operationStateService.SaveRepairAsync(
            operationId.Value,
            repair =>
            {
                var source = repair.Sources.Single(candidate =>
                    string.Equals(
                        candidate.Datasource,
                        datasource,
                        StringComparison.OrdinalIgnoreCase));
                source.NativeCompletionAccepted = true;

                var metrics = repair.Removal
                    ?? throw new InvalidDataException(
                        $"Operation repair {operationId.Value} has no removal metrics.");
                metrics.FilesDeleted = filesDeleted;
                metrics.BytesFreed = bytesFreed;
                metrics.LogEntriesRemoved = logEntriesRemoved;
            },
            CancellationToken.None);
    }

    private static RemovalRepair CopyRemovalRepair(RemovalMetrics metrics)
    {
        return new RemovalRepair
        {
            EntityKey = metrics.EntityKey,
            EntityName = metrics.EntityName,
            EntityKind = metrics.EntityKind,
            Service = metrics.Service,
            EpicAppId = metrics.EpicAppId,
            FilesDeleted = metrics.FilesDeleted,
            BytesFreed = metrics.BytesFreed,
            FilesProcessed = metrics.FilesProcessed,
            TotalFiles = metrics.TotalFiles,
            DetectionMethod = metrics.DetectionMethod,
            CorruptionScanId = metrics.CorruptionScanId
        };
    }

    private async Task ValidateRemovalSelectionAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        await ValidateRemovalSelectionAsync(context, selection, cancellationToken);
    }

    internal static async Task ValidateRemovalSelectionAsync(
        AppDbContext context,
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        var downloads = SelectRemovalDownloads(context, selection);
        var mismatch = await (
            from download in downloads
            join logEntry in context.LogEntries on download.Id equals logEntry.DownloadId
            where logEntry.Datasource != download.Datasource
            select logEntry.Id).AnyAsync(cancellationToken);
        if (mismatch)
        {
            throw new InvalidDataException(
                "Selected removal history contains a log entry attributed to a different datasource");
        }
    }

    private async Task<RemovalCleanupResult> CleanupRemovalAsync(
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await CleanupRemovalAsync(context, selection, cancellationToken);
    }

    internal static async Task<RemovalCleanupResult> CleanupRemovalAsync(
        AppDbContext context,
        RemovalSelection selection,
        CancellationToken cancellationToken)
    {
        // Large set-based deletes can exceed Npgsql's 30-second default. A command timeout would
        // otherwise retry the whole transaction while the ordered table locks are held.
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        // The pooled context can retry transient database failures only when it owns the complete
        // transaction. EF rejects a user transaction outside this retry block at the first query.
        return await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            if (context.Database.IsNpgsql())
            {
                await context.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                    cancellationToken);
                await context.Database.ExecuteSqlRawAsync(
                    "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                    cancellationToken);
            }

            await ValidateRemovalSelectionAsync(context, selection, cancellationToken);
            var datasourceNames = selection.DatasourceNames
                .Select(name => name.ToLowerInvariant())
                .ToList();
            var downloads = SelectRemovalDownloads(context, selection);
            var downloadIds = downloads.Select(download => download.Id);
            var logEntriesDeleted = await context.LogEntries
                .Where(logEntry =>
                    datasourceNames.Contains(logEntry.Datasource.ToLower()) &&
                    logEntry.DownloadId.HasValue &&
                    downloadIds.Contains(logEntry.DownloadId.Value))
                .ExecuteDeleteAsync(cancellationToken);
            var downloadsDeleted = await downloads.ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RemovalCleanupResult(downloadsDeleted, logEntriesDeleted);
        });
    }

    internal static IQueryable<Download> SelectRemovalDownloads(
        AppDbContext context,
        RemovalSelection selection)
    {
        var datasourceNames = selection.DatasourceNames
            .Select(name => name.ToLowerInvariant())
            .ToList();
        var downloads = context.Downloads.Where(download =>
            datasourceNames.Contains(download.Datasource.ToLower()));

        return selection.Kind switch
        {
            RemovalKind.Steam => downloads.Where(download =>
                download.GameAppId == selection.GameAppId),
            RemovalKind.Epic => downloads.Where(download =>
                download.GameName == selection.GameName && download.EpicAppId != null),
            RemovalKind.Named => downloads.Where(download =>
                download.GameName == selection.GameName &&
                download.GameAppId == null &&
                download.EpicAppId == null &&
                download.Service.ToLower() == selection.Service),
            RemovalKind.Service => downloads.Where(download =>
                download.Service.ToLower() == selection.Service),
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
    }
}
