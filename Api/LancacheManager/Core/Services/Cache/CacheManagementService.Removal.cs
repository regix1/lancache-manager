using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
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

    public async Task ResumeRepairAsync(
        OperationRepair repair,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRemovalRepair(repair);

        // The job builds its selection from sanitized names; the target keeps the names as given.
        var target = repair.Target!;
        var selection = repair.Type == OperationType.ServiceRemoval
            ? new RemovalSelection(
                [],
                RemovalKind.Service,
                Service: RustProcessHelper.SanitizeProcessArgument(target.Service!).ToLowerInvariant())
            : target.SteamAppId is { } steamAppId
                ? new RemovalSelection([], RemovalKind.Steam, GameAppId: steamAppId)
                : target.EpicGame is { } epicGame
                    ? new RemovalSelection(
                        [],
                        RemovalKind.Epic,
                        GameName: RustProcessHelper.SanitizeProcessArgument(epicGame))
                    : new RemovalSelection(
                        [],
                        RemovalKind.Named,
                        GameName: RustProcessHelper.SanitizeProcessArgument(target.GameName!),
                        Service: RustProcessHelper.SanitizeProcessArgument(target.Service!).ToLowerInvariant());

        // A log step that started is finished (the dispatch already reset its positions); a crash
        // or failure after a cache step rolls its log step forward, a cancel leaves the history.
        foreach (var source in repair.Sources.Where(source => OperationStateService.NeedsLogStepRedo(repair, source)))
        {
            await RunRemovalLogStepAsync(
                repair.Id,
                _datasourceService.GetDatasource(source.Datasource)!,
                selection with { DatasourceNames = [source.Datasource] },
                [],
                onProgress: null,
                cancellationToken);
        }
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
        IReadOnlyCollection<uint> steamDepotIds)
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

                // The record takes the set once, so every later log step and the repair purge by
                // the set the first log step used.
                if (repair.Target!.SteamDepotIds.Count == 0)
                {
                    repair.Target.SteamDepotIds = steamDepotIds.ToList();
                }
            },
            CancellationToken.None);
    }

    /// <summary>
    /// One datasource's log step under the Rewrite log lock: removes the target's access.log lines
    /// (the binary's URLs, the URLs of the rows still there and the stored Steam depots), then
    /// deletes those rows, and keeps the positions with the count in one save. Rows the live import
    /// added while the cache step ran are read under the same lock, so their lines go with them.
    /// </summary>
    private async Task<ulong> RunRemovalLogStepAsync(
        Guid operationId,
        ResolvedDatasource datasource,
        RemovalSelection selection,
        IReadOnlyCollection<string> reportUrls,
        Func<PurgeLogProgressData, Task>? onProgress,
        CancellationToken cancellationToken)
    {
        var repair = _operationStateService.GetPendingRepairs().Single(pending => pending.Id == operationId);
        await using var logLock = await _operationStateService.LockLogFilesAsync(
            operationId,
            repair.Type,
            LogFileLockKind.Rewrite,
            cancellationToken);

        // A Steam line's depot is parsed from its own URL, so a stored depot already covers the
        // lines of its rows and their URLs are left out of the input.
        var depotIds = repair.Target!.SteamDepotIds.Select(depotId => (long)depotId).ToList();
        List<string> rowUrls;
        await using (var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var downloadIds = SelectRemovalDownloads(context, selection).Select(download => download.Id);
            rowUrls = await context.LogEntries
                .Where(logEntry => logEntry.DownloadId != null && downloadIds.Contains(logEntry.DownloadId.Value))
                .Where(logEntry => logEntry.Download!.DepotId == null || !depotIds.Contains(logEntry.Download.DepotId.Value))
                .Select(logEntry => logEntry.Url)
                .Where(url => url != string.Empty)
                .Distinct()
                .ToListAsync(cancellationToken);
        }
        var targets = new LogPurgeTargets(
            reportUrls.Union(rowUrls, StringComparer.Ordinal).ToList(),
            depotIds,
            selection.Kind == RemovalKind.Service ? selection.Service : null);

        // A cancel before this line leaves the history; the purge marks the step started once its checks
        // pass, and the repair finishes a step that started.
        cancellationToken.ThrowIfCancellationRequested();
        ulong linesRemoved = 0;
        // The purge binary publishes no identity result when it has nothing to match, so it is not started.
        if (targets.Urls.Count > 0 || targets.DepotIds.Count > 0)
        {
            var report = await new LogPurgeRunner(
                    _pathResolver,
                    _rustProcessHelper,
                    _nginxLogRotationService,
                    _stateService,
                    _operationStateService,
                    _logger)
                .RunAsync(operationId, datasource, targets, onProgress, cancellationToken);
            linesRemoved = checked((ulong)report.LinesRemoved);
        }
        await CleanupRemovalAsync(selection, cancellationToken);

        await _operationStateService.SaveRepairAsync(
            operationId,
            current =>
            {
                var source = current.Sources.Single(candidate =>
                    string.Equals(
                        candidate.Datasource,
                        datasource.Name,
                        StringComparison.OrdinalIgnoreCase));
                source.LogRewriteStarted = true;
                source.LogPositionsKept = true;
                var metrics = current.Removal
                    ?? throw new InvalidDataException(
                        $"Operation repair {operationId} has no removal metrics.");
                metrics.LogEntriesRemoved = checked(metrics.LogEntriesRemoved + linesRemoved);
            },
            CancellationToken.None);
        return linesRemoved;
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
