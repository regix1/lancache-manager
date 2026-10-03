using System.Diagnostics;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Upgrades download history written by older versions. The duplicate-check index gains the byte
/// range key, and the per-pass rows for one download merge into one row. The operation enters through
/// the existing queue as a visible global operation. An empty plan table marks completion. Every
/// transaction uses the database execution strategy, and each batch locks Downloads, LogEntries and
/// EventDownloads in that order so writers outside the conflict checker wait for the batch in flight.
/// </summary>
public sealed class DownloadHistoryUpgradeService : BackgroundService
{
    internal const string OperationName = "Upgrading download history";
    internal const string IndexingStageKey = "signalr.downloadHistoryUpgrade.indexing";
    internal const string MergingStageKey = "signalr.downloadHistoryUpgrade.merging";

    private readonly IDbContextFactory<AppDbContext> _contexts;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IOperationQueue _operationQueue;
    private readonly OperationStateService _operationStateService;
    private readonly Task _startupCleanupFinished;
    private readonly ILogger<DownloadHistoryUpgradeService> _logger;
    private CancellationToken _stoppingToken;
    private int _requested;
    private int _replanRequested;
    private Guid? _waitingOperationId;
    private Guid? _lastFailedOperationId;

    internal Task StartupRequest { get; private set; } = Task.CompletedTask;
    internal Task? LastRun { get; private set; }

    public DownloadHistoryUpgradeService(
        IDbContextFactory<AppDbContext> contexts,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IOperationQueue operationQueue,
        OperationStateService operationStateService,
        DownloadCleanupService downloadCleanup,
        ILogger<DownloadHistoryUpgradeService> logger)
        : this(
            contexts,
            notifications,
            operationTracker,
            operationQueue,
            operationStateService,
            downloadCleanup.StartupCleanupFinished,
            logger)
    {
    }

    internal DownloadHistoryUpgradeService(
        IDbContextFactory<AppDbContext> contexts,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IOperationQueue operationQueue,
        OperationStateService operationStateService,
        Task startupCleanupFinished,
        ILogger<DownloadHistoryUpgradeService> logger)
    {
        _contexts = contexts;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _operationQueue = operationQueue;
        _operationStateService = operationStateService;
        _startupCleanupFinished = startupCleanupFinished;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _operationTracker.OperationTerminal += OnOperationTerminal;
        StartupRequest = Task.Run(RequestRunAsync);

        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        _operationTracker.OperationTerminal -= OnOperationTerminal;
        base.Dispose();
    }

    internal async Task RequestRunAsync()
    {
        if (Interlocked.CompareExchange(ref _requested, 1, 0) != 0)
        {
            return;
        }

        try
        {
            await _startupCleanupFinished.WaitAsync(_stoppingToken);
            if (Volatile.Read(ref _replanRequested) == 0
                && !await NeedsWorkAsync(_stoppingToken))
            {
                Volatile.Write(ref _requested, 0);
                if (Volatile.Read(ref _replanRequested) == 1
                    && !_stoppingToken.IsCancellationRequested)
                {
                    _ = RequestRunAsync();
                }
                return;
            }

            var outcome = await _operationQueue.EnqueueAsync(
                OperationType.DownloadHistoryUpgrade,
                ConflictScope.Bulk(),
                OperationName,
                StartUpgradeAsync,
                _stoppingToken);

            if (outcome.Queued)
            {
                _waitingOperationId = outcome.OperationId;
            }
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            Volatile.Write(ref _requested, 0);
            _logger.LogInformation("Download history upgrade request stopped during shutdown");
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _requested, 0);
            _logger.LogError(ex, "Could not request the download history upgrade");
        }
    }

    private Task<Guid?> StartUpgradeAsync()
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        var operationId = _operationTracker.RegisterOperation(
            OperationType.DownloadHistoryUpgrade,
            OperationName,
            cts);
        _waitingOperationId = null;
        var replan = Interlocked.Exchange(ref _replanRequested, 0) == 1;
        LastRun = Task.Run(() => RunUpgradeAsync(operationId, replan, cts.Token));
        return Task.FromResult<Guid?>(operationId);
    }

    private async Task RunUpgradeAsync(Guid operationId, bool replan, CancellationToken ct)
    {
        try
        {
            await RunAsync(operationId, DownloadHistoryUpgradeSql.BatchSize, replan, ct);
            _operationTracker.CompleteOperation(operationId, success: true);
            CloseLastFailure();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _operationTracker.CompleteOperation(operationId, success: false, cancelled: true);
            _logger.LogInformation(
                "Download history upgrade stopped; the remaining rows merge at the next start");
        }
        catch (Exception ex)
        {
            var message = await FailureMessageAsync(ex);
            _logger.LogError(ex, "{Message}", message);
            CloseLastFailure();
            _operationTracker.CompleteOperation(operationId, success: false, error: message);
            _lastFailedOperationId = operationId;
        }
        finally
        {
            Volatile.Write(ref _requested, 0);
            if (Volatile.Read(ref _replanRequested) == 1 && !_stoppingToken.IsCancellationRequested)
            {
                _ = RequestRunAsync();
            }
        }
    }

    internal async Task<int> RunAsync(
        Guid operationId,
        int batchSize,
        bool replan,
        CancellationToken ct)
    {
        await using var context = await _contexts.CreateDbContextAsync(ct);
        _operationTracker.UpdateProgress(operationId, 0, IndexingStageKey);
        _operationTracker.RefreshRun(operationId);

        var indexState = await context.Database
            .SqlQueryRaw<string>(DownloadHistoryUpgradeSql.DuplicateCheckIndexState)
            .SingleAsync(ct);
        if (indexState == "rebuild")
        {
            context.Database.SetCommandTimeout(0);
            var terminated = await context.Database
                .SqlQueryRaw<int>(DownloadHistoryUpgradeSql.TerminateOrphanIndexBuilds)
                .SingleAsync(ct);
            if (terminated > 0)
            {
                _logger.LogWarning(
                    "Stopped {Count} orphaned LogEntries index builds before the download history upgrade",
                    terminated);
            }

            var stopwatch = Stopwatch.StartNew();
            await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(ct);
                await context.Database.ExecuteSqlRawAsync(
                    DownloadHistoryUpgradeSql.DropMd5IndexIfExists,
                    ct);
                await context.Database.ExecuteSqlRawAsync(
                    DownloadHistoryUpgradeSql.CreateMd5Index,
                    ct);
                await context.Database.ExecuteSqlRawAsync(
                    DownloadHistoryUpgradeSql.DropOldIndexIfExists,
                    ct);
                await context.Database.ExecuteSqlRawAsync(
                    DownloadHistoryUpgradeSql.RenameMd5Index,
                    ct);
                await transaction.CommitAsync(ct);
            });
            stopwatch.Stop();

            // The transaction commits the entire swap or leaves the old index intact. A restart
            // after any failure therefore begins the same swap from the same valid state.
            _logger.LogInformation(
                "Download duplicate-check index rebuilt in {ElapsedMs} ms",
                stopwatch.ElapsedMilliseconds);
        }

        _operationTracker.UpdateProgress(operationId, 5, MergingStageKey);
        context.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));

        var batches = 0;
        var mergeCommitAttempted = false;
        try
        {
            var replanned = replan;
            if (replan)
            {
                await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await context.Database.BeginTransactionAsync(ct);
                    await context.Database.ExecuteSqlRawAsync(DownloadHistoryUpgradeSql.DropPlan, ct);
                    await transaction.CommitAsync(ct);
                });
            }

            while (true)
            {
                if (!await PlanExistsAsync(context, ct))
                {
                    await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                    {
                        await using var transaction = await context.Database.BeginTransactionAsync(ct);
                        if (!await PlanExistsAsync(context, ct))
                        {
                            await context.Database.ExecuteSqlRawAsync(
                                DownloadHistoryUpgradeSql.CreatePlan,
                                ct);
                        }
                        await transaction.CommitAsync(ct);
                    });
                }

                var total = await context.Database
                    .SqlQueryRaw<long>(DownloadHistoryUpgradeSql.RemainingPlanRows)
                    .SingleAsync(ct);
                _logger.LogInformation(
                    "Download history upgrade planned {Count} rows for merging",
                    total);

                long skipped = 0;
                var planBatches = 0;
                while (!await context.Database
                    .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
                    .SingleAsync(ct))
                {
                    long batchSkipped = 0;
                    // One batch at a time pauses the log import, so the dashboard catches up between
                    // batches. The lock wraps the retried transaction and is taken before its table
                    // locks, which an import that writes rows would otherwise deadlock with.
                    await using (await _operationStateService.LockLogFilesAsync(
                        operationId,
                        OperationType.DownloadHistoryUpgrade,
                        LogFileLockKind.Rows,
                        ct))
                    {
                        await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                        {
                            batchSkipped = 0;
                            await using var transaction = await context.Database.BeginTransactionAsync(ct);

                            // The global slot excludes queued writers. Manual tagging, stale-row cleanup,
                            // scheduled resolvers, PICS apply, orphan removal, eviction reset, and a live
                            // pass admitted immediately before registration still need the ordered locks.
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"Downloads\" IN SHARE ROW EXCLUSIVE MODE",
                                ct);
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"LogEntries\" IN SHARE ROW EXCLUSIVE MODE",
                                ct);
                            await context.Database.ExecuteSqlRawAsync(
                                "LOCK TABLE \"EventDownloads\" IN SHARE ROW EXCLUSIVE MODE",
                                ct);
                            await context.Database.ExecuteSqlRawAsync(
                                DownloadHistoryUpgradeSql.PrepareBatch,
                                new[] { new NpgsqlParameter("batchSize", batchSize) },
                                ct);
                            batchSkipped = await context.Database
                                .SqlQueryRaw<long>(DownloadHistoryUpgradeSql.SkippedInBatch)
                                .SingleAsync(ct);
                            await context.Database.ExecuteSqlRawAsync(
                                DownloadHistoryUpgradeSql.FoldBatch,
                                ct);
                            mergeCommitAttempted = true;
                            await transaction.CommitAsync(ct);
                        });
                    }

                    skipped += batchSkipped;
                    batches++;
                    planBatches++;
                    var percent = 5 + 95.0
                        * Math.Min(total, (long)batches * batchSize)
                        / Math.Max(total, 1);
                    _operationTracker.UpdateProgress(operationId, percent, MergingStageKey);
                    _operationTracker.RefreshRun(operationId);

                    if (planBatches % 20 == 0)
                    {
                        _logger.LogInformation(
                            "Download history upgrade applied {Batches} batches in the current plan",
                            planBatches);
                    }
                }

                _logger.LogInformation(
                    "Download history upgrade finished a plan with {Batches} batches and {Skipped} skipped rows",
                    planBatches,
                    skipped);

                if (skipped == 0 || replanned)
                {
                    // Rows still skipped after the one re-plan stay as they were; the card counts them.
                    if (skipped > 0)
                    {
                        _operationTracker.SetWarning(operationId, new RunWarning(
                            "common.notifications.warnings.historyRowsNotMerged",
                            new Dictionary<string, object?> { ["rowCount"] = skipped }));
                    }
                    break;
                }

                await context.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
                {
                    await using var transaction = await context.Database.BeginTransactionAsync(ct);
                    if (await context.Database
                        .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
                        .SingleAsync(ct))
                    {
                        await context.Database.ExecuteSqlRawAsync(
                            DownloadHistoryUpgradeSql.DropPlan,
                            ct);
                    }
                    await transaction.CommitAsync(ct);
                });
                replanned = true;
            }
        }
        finally
        {
            if (mergeCommitAttempted)
            {
                await _notifications.NotifyAllAsync(
                    SignalREvents.DownloadsRefresh,
                    new { source = "download-history-merge" });
                await _notifications.NotifyAllAsync(
                    SignalREvents.DownloadHistoryMergeComplete,
                    new { batches });
            }
        }

        return batches;
    }

    private async Task<bool> NeedsWorkAsync(CancellationToken ct)
    {
        await using var context = await _contexts.CreateDbContextAsync(ct);
        var indexState = await context.Database
            .SqlQueryRaw<string>(DownloadHistoryUpgradeSql.DuplicateCheckIndexState)
            .SingleAsync(ct);
        if (indexState == "rebuild" || !await PlanExistsAsync(context, ct))
        {
            return true;
        }

        return !await context.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanIsEmpty)
            .SingleAsync(ct);
    }

    private static Task<bool> PlanExistsAsync(AppDbContext context, CancellationToken ct) =>
        context.Database
            .SqlQueryRaw<bool>(DownloadHistoryUpgradeSql.PlanExists)
            .SingleAsync(ct);

    private async Task<string> FailureMessageAsync(Exception exception)
    {
        string remaining;
        try
        {
            await using var context = await _contexts.CreateDbContextAsync();
            if (await PlanExistsAsync(context, CancellationToken.None))
            {
                var count = await context.Database
                    .SqlQueryRaw<long>(DownloadHistoryUpgradeSql.RemainingPlanRows)
                    .SingleAsync();
                remaining = $"{count} rows still wait to merge";
            }
            else
            {
                remaining = "The merge has not started";
            }
        }
        catch (Exception readException)
        {
            _logger.LogWarning(
                readException,
                "Could not count remaining rows after the download history upgrade stopped");
            remaining = "The number of rows still waiting is unknown";
        }

        return $"Download history upgrade stopped: {exception.Message}. {remaining}. It tries again at the next start.";
    }

    private void CloseLastFailure()
    {
        if (_lastFailedOperationId is not { } operationId)
        {
            return;
        }

        _operationTracker.CloseRun(operationId);
        _lastFailedOperationId = null;
    }

    private void OnOperationTerminal(OperationInfo operation)
    {
        try
        {
            if (operation.Id == _waitingOperationId
                && operation.Status == OperationStatus.Cancelled)
            {
                _waitingOperationId = null;
                Volatile.Write(ref _requested, 0);
                return;
            }

            if (operation.Type == OperationType.DataImport
                && operation.Metadata is DataImportMetrics { RecordsImported: > 0 })
            {
                Interlocked.Exchange(ref _replanRequested, 1);
                _ = RequestRunAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not process a terminal operation for the download history upgrade");
        }
    }
}
