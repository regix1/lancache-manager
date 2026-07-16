using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using LancacheManager.Core.Services.SteamKit2;
using System.Data;


namespace LancacheManager.Infrastructure.Services;

public class DatabaseService : IDatabaseService
{
    private readonly AppDbContext _context;
    private readonly ISignalRNotificationService _notifications;
    private readonly ILogger<DatabaseService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly StateService _stateRepository;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private const string CachedCorruptionDetectionsTable = "CachedCorruptionDetections";
    private const string CachedCorruptionScansTable = "CachedCorruptionScans";
    private static readonly HashSet<string> _validResetTables =
    [
        "LogEntries",
        "Downloads",
        "ClientStats",
        "ServiceStats",
        "SteamDepotMappings",
        "CachedGameDetections",
        "CachedServiceDetections",
        CachedCorruptionDetectionsTable,
        CachedCorruptionScansTable,
        "ClientGroups",
        "UserSessions",
        "UserPreferences",
        "Events",
        "EventDownloads",
        "PrefillSessions",
        "PrefillHistoryEntries",
        "PrefillCachedDepots",
        "BannedSteamUsers",
        "CacheSnapshots",
        "EpicGameMappings",
        "EpicCdnPatterns"
    ];
    private static readonly ConcurrentDictionary<Guid, bool> _activeResetOperations = new();
    private static Guid? _currentResetOperationId;
    private static ResetProgressInfo? _currentResetProgress;
    private long _resetProgressRevision;
    private readonly ProgressEmitGate _resetProgressEmitGate = new();

    public sealed record ResetProgressInfo(
        Guid OperationId,
        bool IsProcessing,
        OperationStatus Status,
        string Message,
        OperationProgressSnapshot Snapshot,
        int? TablesCleared = null,
        int? TotalTables = null,
        int? FilesDeleted = null);

    public static ResetProgressInfo? CurrentResetProgress => Volatile.Read(ref _currentResetProgress);
    public static Guid? CurrentResetOperationId => _currentResetOperationId;

    public DatabaseService(
        AppDbContext context,
        ISignalRNotificationService notifications,
        ILogger<DatabaseService> logger,
        IPathResolver pathResolver,
        IDbContextFactory<AppDbContext> dbContextFactory,
        SteamKit2Service steamKit2Service,
        StateService stateRepository,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
    {
        _context = context;
        _notifications = notifications;
        _logger = logger;
        _pathResolver = pathResolver;
        _dbContextFactory = dbContextFactory;
        _steamKit2Service = steamKit2Service;
        _stateRepository = stateRepository;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;
    }

    public bool IsResetOperationRunning => _activeResetOperations.Any();

    /// <summary>
    /// Gets the count of log entries (async wrapper)
    /// </summary>
    public Task<int> GetLogEntriesCountAsync()
    {
        return GetLogCount();
    }

    public async Task<int> GetLogCount()
    {
        return await _context.LogEntries.CountAsync();
    }

    /// <summary>
    /// Starts a background task to reset selected tables and returns immediately
    /// </summary>
    public Guid StartResetAsync(List<string> tableNames)
    {
        // Create cancellation support
        var cts = new CancellationTokenSource();

        // Register with unified operation tracker for cancel support.
        // Declare-then-assign so the onTerminalCleanup lambda closes over the variable (the lambda runs
        // later, after opId is assigned). onTerminalCleanup is the safety net for the universal force-kill
        // path: this is an in-process EF worker with no associated process, so force-kill cancels the CTS
        // and completes the op immediately WITHOUT unwinding the worker finally — leaving the static
        // _activeResetOperations dict populated (IsResetOperationRunning stays true, blocking all future
        // resets process-wide). The lambda mirrors the worker finally (lines 900-904).
        // onTerminalEmit fires the typed DatabaseResetComplete event EXACTLY ONCE (CompletedFlag-gated) for
        // the normal success/error path AND the universal force-kill/cancel path. The legacy
        // All phase progress is routed through ReportResetProgressAsync below; terminal completion
        // remains centralized in the tracker's onTerminalEmit callback.
        Guid opId = default;
        opId = _operationTracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database Reset",
            cts,
            onTerminalCleanup: () =>
            {
                _activeResetOperations.TryRemove(opId, out _);
                if (_currentResetOperationId == opId)
                {
                    _currentResetOperationId = null;
                }
                var progress = Volatile.Read(ref _currentResetProgress);
                if (progress?.OperationId == opId)
                {
                    Volatile.Write(ref _currentResetProgress, null);
                }
            },
            onTerminalEmit: info => info.Cancelled
                ? _notifications.NotifyAllAsync(
                    SignalREvents.DatabaseResetComplete,
                    new LancacheManager.Infrastructure.Utilities.SignalRNotifications.DatabaseResetComplete(
                        OperationId: opId,
                        Success: false,
                        StageKey: "signalr.dbReset.cancelled",
                        Status: OperationStatus.Cancelled,
                        Cancelled: true,
                        Error: info.Error))
                : info.Success
                    ? _notifications.NotifyAllAsync(
                        SignalREvents.DatabaseResetComplete,
                        new LancacheManager.Infrastructure.Utilities.SignalRNotifications.DatabaseResetComplete(
                            OperationId: opId,
                            Success: true,
                            StageKey: "signalr.dbReset.complete",
                            Status: OperationStatus.Completed))
                    : _notifications.NotifyAllAsync(
                        SignalREvents.DatabaseResetComplete,
                        new LancacheManager.Infrastructure.Utilities.SignalRNotifications.DatabaseResetComplete(
                            OperationId: opId,
                            Success: false,
                            StageKey: "signalr.dbReset.failed",
                            Status: OperationStatus.Failed,
                            Error: info.Error,
                            Context: new Dictionary<string, object?> { ["errorDetail"] = info.Error })));
        var operationId = opId;

        if (_activeResetOperations.TryAdd(operationId, true))
        {
            _currentResetOperationId = operationId;
            _resetProgressEmitGate.Reset();
            _logger.LogInformation("Starting background reset operation {OperationId} for tables: {Tables}", operationId, string.Join(", ", tableNames));

            var startingContext = new Dictionary<string, object?> { ["count"] = tableNames.Count };
            var startingSnapshot = OperationProgressSnapshot.Create(
                "signalr.dbReset.startingTables",
                0,
                startingContext,
                Interlocked.Increment(ref _resetProgressRevision));
            Volatile.Write(ref _currentResetProgress, new ResetProgressInfo(
                operationId,
                IsProcessing: true,
                Status: OperationStatus.Pending,
                Message: $"Starting reset of {tableNames.Count} table(s)...",
                Snapshot: startingSnapshot,
                TablesCleared: 0,
                TotalTables: tableNames.Count,
                FilesDeleted: 0));
            _operationTracker.UpdateProgress(operationId, 0, startingSnapshot.StageKey);

            // Send started event via SignalR
            _notifications.NotifyAllFireAndForget(SignalREvents.DatabaseResetStarted, new
            {
                OperationId = operationId,
                Message = $"Starting reset of {tableNames.Count} table(s)...",
                startingSnapshot.StageKey,
                startingSnapshot.Context
            });

            // Start background task with cancellation token
            _ = Task.Run(async () => await DoResetAsync(operationId, tableNames, cts.Token));
        }
        else
        {
            _logger.LogWarning("Failed to start reset operation - already running?");
        }

        return operationId;
    }

    /// <summary>
    /// Internal method that performs the actual reset operation
    /// </summary>
    private async Task DoResetAsync(Guid operationId, List<string> tableNames, CancellationToken cancellationToken)
    {
        var terminalOutcome = OperationStatus.Failed;

        try
        {
            // Context acquisition is part of the reset lifecycle: a database outage here must flow
            // through the same failed terminal report/cleanup as an exception during deletion.
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            _logger.LogInformation($"Starting selective database reset for tables: {string.Join(", ", tableNames)}");

            // Send initial progress update
            await ReportResetProgressAsync(
                operationId,
                isProcessing: true,
                percentComplete: 0,
                status: OperationStatus.Running,
                stageKey: "signalr.dbReset.startingTables",
                context: new Dictionary<string, object?> { ["count"] = tableNames.Count },
                message: $"Starting reset of {tableNames.Count} table(s)...",
                tablesCleared: 0,
                totalTables: tableNames.Count,
                filesDeleted: 0);

            // Validate table names to prevent SQL injection. Corruption evidence is one logical
            // snapshot: clearing logs or either physical table must clear both candidate rows and
            // the scan header, including a zero-result header with no candidate rows.
            var tablesToClear = ResolveResetTables(tableNames);

            if (tablesToClear.Count == 0)
            {
                _logger.LogWarning("No valid tables selected for reset");
                terminalOutcome = OperationStatus.Failed;
                await ReportResetProgressAsync(
                    operationId,
                    isProcessing: false,
                    percentComplete: 0,
                    status: OperationStatus.Failed,
                    stageKey: "signalr.dbReset.noTablesSelected",
                    context: new Dictionary<string, object?>(),
                    message: "No valid tables selected for reset");
                _operationTracker.CompleteOperation(operationId, success: false, error: "No valid tables selected for reset");
                return;
            }

            // Count total rows for progress calculation - wrap in transaction for consistent snapshot
            int totalRows = 0;
            var countStrategy = context.Database.CreateExecutionStrategy();
            await countStrategy.ExecuteAsync(async () =>
            {
                using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
                {
                    try
                    {
                        foreach (var tableName in tablesToClear)
                        {
                            var count = await CountResetTableRowsAsync(
                                context,
                                tableName,
                                cancellationToken);
                            totalRows += count;
                        }

                        await transaction.CommitAsync(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error getting consistent counts, rolling back transaction");
                        await transaction.RollbackAsync();
                        throw;
                    }
                }
            });

            _logger.LogInformation($"Clearing {totalRows:N0} total rows from {tablesToClear.Count} table(s)");

            int deletedRows = 0;
            double progressPerTable = 85.0 / tablesToClear.Count;
            double currentProgress = 0;

            // Track which preference reset event needs to be broadcast AFTER operation completes
            bool shouldBroadcastPreferencesReset = false;

            // Temporarily disable foreign key triggers for bulk deletion (PostgreSQL)
            // This prevents FK constraint errors during table deletions
            _logger.LogInformation("Disabling foreign key triggers for bulk deletion");
            await context.Database.ExecuteSqlRawAsync("SET session_replication_role = replica;");

            // Wrap all deletion operations in a transaction for atomicity
            var deleteStrategy = context.Database.CreateExecutionStrategy();
            await deleteStrategy.ExecuteAsync(async () =>
            {
                using var deleteTransaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
                try
                {
                // Delete tables based on selection
                // PRIORITY ORDER:
                // 1. UserSessions - ALWAYS FIRST to immediately invalidate all sessions
                // 2. UserPreferences - Must be deleted before UserSessions FK cascade
                // 3. PrefillHistoryEntries - FK dependency on PrefillSessions
                // 4. PrefillSessions - Must be after PrefillHistoryEntries
                // 5. EventDownloads - FK dependency from Events and Downloads
                // 6. LogEntries - Must be deleted before Downloads FK
                // 7. Downloads - Has FK dependency on LogEntries
                // 8. Events - EventDownloads depend on this
                // 9. ClientGroupMembers - FK dependency on ClientGroups
                // 10. ClientGroups - Must be after ClientGroupMembers
                // 11. All others - No dependencies
                var orderedTables = tablesToClear.OrderBy(t =>
                    t == "UserSessions" ? 0 :           // HIGHEST PRIORITY - invalidate sessions first
                    t == "UserPreferences" ? 1 :        // Second - FK dependency on UserSessions
                    t == "PrefillHistoryEntries" ? 2 :  // Third - FK dependency on PrefillSessions
                    t == "PrefillSessions" ? 3 :        // Fourth - must be after PrefillHistoryEntries
                    t == "EventDownloads" ? 4 :         // Fifth - FK dependency from Events and Downloads
                    t == "LogEntries" ? 5 :             // Sixth - FK dependency from Downloads
                    t == "Downloads" ? 6 :              // Seventh - depends on LogEntries
                    t == "Events" ? 7 :                 // Eighth - EventDownloads depend on this
                    t == "ClientGroups" ? 8 :           // Ninth - ClientGroupMembers FK dependency (cascade will handle)
                    9).ToList();

                // Special case: If deleting Downloads but NOT LogEntries, we must null out the foreign keys first
                if (tablesToClear.Contains("Downloads") && !tablesToClear.Contains("LogEntries"))
                {
                    _logger.LogInformation("Nullifying LogEntry.DownloadId foreign keys before deleting Downloads table");
                    await context.LogEntries.Where(le => le.DownloadId != null)
                        .ExecuteUpdateAsync(s => s.SetProperty(le => le.DownloadId, (int?)null), cancellationToken);
                }

                var corruptionEvidenceCleared = false;

                foreach (var tableName in orderedTables)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _logger.LogInformation($"Clearing table: {tableName}");

                    switch (tableName)
                    {
                        case "LogEntries":
                            // Use batched deletion to avoid locking the database for too long
                            // This allows other operations to proceed between batches
                            var logEntriesTotal = await context.LogEntries.CountAsync(cancellationToken);
                            var logEntriesDeleted = 0;
                            const int batchSize = 10000;

                            _logger.LogInformation($"Starting batched deletion of {logEntriesTotal:N0} log entries (batch size: {batchSize:N0})");

                            while (true)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                // Delete a batch using raw SQL for efficiency
                                // Use a subquery for batched deletion
                                var deleted = await context.Database.ExecuteSqlInterpolatedAsync(
                                    $"DELETE FROM \"LogEntries\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"LogEntries\" LIMIT {batchSize})", cancellationToken);

                                // Check for cancellation immediately after batch completes
                                // Check for cancellation immediately after batch completes for fastest response
                                cancellationToken.ThrowIfCancellationRequested();

                                if (deleted == 0)
                                    break;

                                logEntriesDeleted += deleted;
                                var percentDone = logEntriesTotal > 0 ? (double)logEntriesDeleted / logEntriesTotal * 100 : 100;

                                _logger.LogInformation($"Deleted {logEntriesDeleted:N0}/{logEntriesTotal:N0} log entries ({percentDone:F1}%)");

                                var progressPercent = Math.Min(currentProgress + progressPerTable * (logEntriesDeleted / (double)Math.Max(logEntriesTotal, 1)), 85.0);
                                var progressMessage = $"Clearing log entries... {logEntriesDeleted:N0}/{logEntriesTotal:N0} ({percentDone:F1}%)";

                                await ReportResetProgressAsync(
                                    operationId,
                                    true,
                                    progressPercent,
                                    OperationStatus.Running,
                                    "signalr.dbReset.clearingLogEntries",
                                    new Dictionary<string, object?>
                                    {
                                        ["deleted"] = logEntriesDeleted,
                                        ["total"] = logEntriesTotal,
                                        ["percent"] = Math.Round(percentDone, 1)
                                    },
                                    progressMessage);

                                // Small delay to allow other operations to acquire the lock
                                await Task.Delay(50, cancellationToken);
                            }

                            _logger.LogInformation($"Cleared {logEntriesDeleted:N0} log entries");
                            deletedRows += logEntriesDeleted;

                            // Reset log positions for all datasources so the log processor knows to start from beginning
                            _logger.LogInformation("Resetting log positions for all datasources");
                            foreach (var ds in _datasourceService.GetDatasources())
                            {
                                _stateRepository.SetLogPosition(ds.Name, 0);
                                _stateRepository.SetLogTotalLines(ds.Name, 0);
                            }
                            // Also reset legacy position
                            _stateRepository.SetLogPosition(0);

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedLogEntries",
                                new Dictionary<string, object?> { ["count"] = logEntriesDeleted },
                                $"Cleared log entries ({logEntriesDeleted:N0} rows)");
                            break;

                        case "Downloads":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var downloadsCount = await context.Downloads.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {downloadsCount:N0} downloads");
                            deletedRows += downloadsCount;

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedDownloads",
                                new Dictionary<string, object?> { ["count"] = downloadsCount },
                                $"Cleared downloads ({downloadsCount:N0} rows)");
                            break;

                        case "ClientStats":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var clientStatsCount = await context.ClientStats.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {clientStatsCount:N0} client stats");
                            deletedRows += clientStatsCount;

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedClientStats",
                                new Dictionary<string, object?> { ["count"] = clientStatsCount },
                                $"Cleared client stats ({clientStatsCount:N0} rows)");
                            break;

                        case "ServiceStats":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var serviceStatsCount = await context.ServiceStats.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {serviceStatsCount:N0} service stats");
                            deletedRows += serviceStatsCount;

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedServiceStats",
                                new Dictionary<string, object?> { ["count"] = serviceStatsCount },
                                $"Cleared service stats ({serviceStatsCount:N0} rows)");
                            break;

                        case "SteamDepotMappings":
                            // Use ExecuteDeleteAsync for direct deletion (more efficient for this table)
                            var mappingCount = await context.SteamDepotMappings.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {mappingCount:N0} depot mappings");
                            deletedRows += mappingCount;

                            // Also clear game information from Downloads table (since depot mappings are gone)
                            _logger.LogInformation("Clearing game information from Downloads table (GameName, GameImageUrl, GameAppId)");
                            await context.Downloads
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(d => d.GameName, (string?)null)
                                    .SetProperty(d => d.GameImageUrl, (string?)null)
                                    .SetProperty(d => d.GameAppId, (uint?)null), cancellationToken);
                            _logger.LogInformation("Cleared game information from all downloads");

                            // CRITICAL: Also delete the PICS JSON file to prevent re-import on next scan
                            // Without this, the JSON file would be imported back into the database
                            var picsJsonPath = Path.Combine(_pathResolver.GetPicsDirectory(), "pics_depot_mappings.json");
                            if (File.Exists(picsJsonPath))
                            {
                                try
                                {
                                    File.Delete(picsJsonPath);
                                    _logger.LogInformation("Deleted PICS JSON file: {Path}", picsJsonPath);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to delete PICS JSON file: {Path}", picsJsonPath);
                                }
                            }

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedDepotMappings",
                                new Dictionary<string, object?> { ["count"] = mappingCount },
                                $"Cleared depot mappings ({mappingCount:N0} rows)");
                            break;

                        case "CachedGameDetections":
                            // Direct ExecuteDeleteAsync is deliberate: database reset clears the whole table, not the load/upsert flow GameCacheDetectionDataService owns.
                            var gameDetectionCount = await context.CachedGameDetections.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {gameDetectionCount:N0} cached game detections");
                            deletedRows += gameDetectionCount;

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedGameDetections",
                                new Dictionary<string, object?> { ["count"] = gameDetectionCount },
                                $"Cleared cached game detections ({gameDetectionCount:N0} rows)");
                            break;

                        case "UserPreferences":
                            // Use ExecuteDeleteAsync for direct deletion
                            var userPreferencesCount = await context.UserPreferences.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {userPreferencesCount:N0} user preferences");
                            deletedRows += userPreferencesCount;

                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedUserPreferences",
                                new Dictionary<string, object?> { ["count"] = userPreferencesCount },
                                $"Cleared user preferences ({userPreferencesCount:N0} rows)");

                            // Mark that we need to broadcast preferences reset event after operation completes
                            shouldBroadcastPreferencesReset = true;
                            break;

                        case "UserSessions":
                            // CRITICAL: UserSessions is always processed FIRST (priority 0)
                            // This ensures all users are logged out immediately before any other tables are cleared

                            // SECURITY: Clear Steam auth FIRST, before clearing user sessions
                            // This ensures Steam is fully logged out before frontend receives the event
                            _logger.LogInformation("Clearing Steam authentication data first...");
                            try
                            {
                                await _steamKit2Service.ClearAllSteamAuthAsync();
                            }
                            catch (Exception steamEx)
                            {
                                _logger.LogWarning(steamEx, "Error clearing Steam auth during session reset");
                            }

                            _logger.LogInformation($"[PRIORITY] Clearing UserSessions table to invalidate all active sessions");
                            var userSessionsCount = await context.UserSessions.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {userSessionsCount:N0} user sessions");
                            deletedRows += userSessionsCount;

                            // CRITICAL: Clear in-memory caches for both guest sessions and device registrations
                            // Without this, the services would still serve cached sessions from memory
                            await ReportResetProgressAsync(operationId, true,
                                Math.Min(currentProgress + progressPerTable, 85.0), OperationStatus.Running,
                                "signalr.dbReset.clearedUserSessions",
                                new Dictionary<string, object?> { ["count"] = userSessionsCount },
                                $"Cleared user sessions ({userSessionsCount:N0} rows)");

                            // IMMEDIATELY broadcast UserSessionsCleared event to log out all connected users
                            // This happens RIGHT AFTER deletion, not at the end of the operation
                            // Note: This SignalR event will trigger the frontend to clear cookies and redirect to login
                            _logger.LogInformation("Broadcasting UserSessionsCleared event to all clients (immediate logout)");
                            await _notifications.NotifyAllAsync(SignalREvents.UserSessionsCleared, new
                            {
                                stageKey = "signalr.dbReset.sessionsCleared.logout",
                                context = new Dictionary<string, object?>(),
                                clearCookies = true, // Signal frontend to clear all auth cookies
                                timestamp = DateTime.UtcNow
                            });

                            _logger.LogInformation("All users have been logged out. Continuing with remaining table deletions in background...");
                            break;

                        case "Events":
                            // Use ExecuteDeleteAsync for direct deletion
                            // Note: EventDownloads will be cascade deleted due to FK constraint
                            var eventsCount = await context.Events.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {eventsCount:N0} events (and associated event downloads via cascade)");
                            deletedRows += eventsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "Events", eventsCount, $"Cleared events ({eventsCount:N0} rows)");

                            // Notify frontend to clear event cache
                            await _notifications.NotifyAllAsync(SignalREvents.EventsCleared, new
                            {
                                message = "All events have been cleared",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "EventDownloads":
                            // Use ExecuteDeleteAsync for direct deletion
                            var eventDownloadsCount = await context.EventDownloads.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {eventDownloadsCount:N0} event download associations");
                            deletedRows += eventDownloadsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "EventDownloads", eventDownloadsCount,
                                $"Cleared event download links ({eventDownloadsCount:N0} rows)");
                            break;

                        case CachedCorruptionDetectionsTable:
                        case CachedCorruptionScansTable:
                            if (corruptionEvidenceCleared)
                            {
                                break;
                            }

                            var (corruptionDetectionCount, corruptionScanCount) =
                                await DeleteCachedCorruptionEvidenceAsync(context, cancellationToken);
                            corruptionEvidenceCleared = true;
                            _logger.LogInformation(
                                "Cleared {Candidates:N0} cached corruption candidates and {Scans:N0} scan headers",
                                corruptionDetectionCount,
                                corruptionScanCount);
                            deletedRows += corruptionDetectionCount + corruptionScanCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "CorruptionEvidence", corruptionDetectionCount + corruptionScanCount,
                                $"Cleared corruption detection cache ({corruptionDetectionCount + corruptionScanCount:N0} rows)");
                            break;

                        case "ClientGroups":
                            // Use ExecuteDeleteAsync for direct deletion
                            // Note: ClientGroupMembers will be cascade deleted due to FK constraint
                            var clientGroupsCount = await context.ClientGroups.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {clientGroupsCount:N0} client groups (and associated members via cascade)");
                            deletedRows += clientGroupsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "ClientGroups", clientGroupsCount,
                                $"Cleared client groups ({clientGroupsCount:N0} rows)");

                            // Notify frontend to clear client groups cache
                            await _notifications.NotifyAllAsync(SignalREvents.ClientGroupsCleared, new
                            {
                                message = "All client groups have been cleared",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "PrefillSessions":
                            // Use ExecuteDeleteAsync for direct deletion
                            // Note: PrefillHistoryEntries will be cascade deleted due to FK constraint
                            var prefillSessionsCount = await context.PrefillSessions.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {prefillSessionsCount:N0} prefill sessions (and associated history via cascade)");
                            deletedRows += prefillSessionsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "PrefillSessions", prefillSessionsCount,
                                $"Cleared prefill sessions ({prefillSessionsCount:N0} rows)");

                            break;

                        case "PrefillHistoryEntries":
                            // Use ExecuteDeleteAsync for direct deletion
                            var prefillHistoryCount = await context.PrefillHistoryEntries.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {prefillHistoryCount:N0} prefill history entries");
                            deletedRows += prefillHistoryCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "PrefillHistoryEntries", prefillHistoryCount,
                                $"Cleared prefill history ({prefillHistoryCount:N0} rows)");
                            break;

                        case "BannedSteamUsers":
                            // Use ExecuteDeleteAsync for direct deletion
                            var bannedUsersCount = await context.BannedSteamUsers.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {bannedUsersCount:N0} banned Steam users");
                            deletedRows += bannedUsersCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "BannedSteamUsers", bannedUsersCount,
                                $"Cleared banned Steam users ({bannedUsersCount:N0} rows)");

                            break;

                        case "CachedServiceDetections":
                            // Direct ExecuteDeleteAsync is deliberate: database reset clears the whole table, not the load/upsert flow GameCacheDetectionDataService owns.
                            var serviceDetectionsCount = await context.CachedServiceDetections.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {serviceDetectionsCount:N0} cached service detections");
                            deletedRows += serviceDetectionsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "CachedServiceDetections", serviceDetectionsCount,
                                $"Cleared service detection cache ({serviceDetectionsCount:N0} rows)");
                            break;

                        case "PrefillCachedDepots":
                            // Use ExecuteDeleteAsync for direct deletion
                            var prefillCachedDepotsCount = await context.PrefillCachedDepots.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {prefillCachedDepotsCount:N0} prefill cached depots");
                            deletedRows += prefillCachedDepotsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "PrefillCachedDepots", prefillCachedDepotsCount,
                                $"Cleared prefill cache status ({prefillCachedDepotsCount:N0} rows)");
                            break;

                        case "CacheSnapshots":
                            // Use ExecuteDeleteAsync for direct deletion
                            var cacheSnapshotsCount = await context.CacheSnapshots.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {cacheSnapshotsCount:N0} cache snapshots");
                            deletedRows += cacheSnapshotsCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "CacheSnapshots", cacheSnapshotsCount,
                                $"Cleared cache size history ({cacheSnapshotsCount:N0} rows)");
                            break;

                        case "EpicGameMappings":
                            var epicMappingCount = await context.EpicGameMappings.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {epicMappingCount:N0} Epic game mappings");
                            deletedRows += epicMappingCount;

                            _logger.LogInformation("Clearing Epic app ID from Downloads table");
                            await context.Downloads
                                .Where(d => d.EpicAppId != null)
                                .ExecuteUpdateAsync(s => s
                                    .SetProperty(d => d.EpicAppId, (string?)null), cancellationToken);
                            _logger.LogInformation("Cleared Epic app ID from all downloads");

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "EpicGameMappings", epicMappingCount,
                                $"Cleared Epic game mappings ({epicMappingCount:N0} rows) and unmapped all Epic downloads");
                            break;

                        case "EpicCdnPatterns":
                            var epicCdnCount = await context.EpicCdnPatterns.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {epicCdnCount:N0} Epic CDN patterns");
                            deletedRows += epicCdnCount;

                            await ReportClearedTableAsync(operationId,
                                Math.Min(currentProgress + progressPerTable, 85.0),
                                "EpicCdnPatterns", epicCdnCount,
                                $"Cleared Epic CDN patterns ({epicCdnCount:N0} rows)");
                            break;
                    }

                    currentProgress += progressPerTable;
                }

                // Cleared log entries were the evidence behind the current Repeated-MISS scan:
                // revoke its currentness so it can no longer gate removals, but keep completed
                // snapshots as view-only history. An explicit corruption-table reset above
                // already deleted everything, so there is nothing left to demote in that case.
                if (tablesToClear.Contains("LogEntries") && !corruptionEvidenceCleared)
                {
                    var demotedScans = await DemoteCachedCorruptionEvidenceAsync(
                        context,
                        cancellationToken,
                        CorruptionDetectionMode.RepeatedMiss);
                    if (demotedScans > 0)
                    {
                        _logger.LogInformation(
                            "Demoted {Scans:N0} current Repeated-MISS corruption scan(s) to history after log clear",
                            demotedScans);
                    }
                }

                // Clean up files if LogEntries or Downloads were cleared
                if (tablesToClear.Contains("LogEntries") || tablesToClear.Contains("Downloads"))
                {
                    await ReportResetProgressAsync(
                        operationId,
                        true,
                        90.0,
                        OperationStatus.Running,
                        "signalr.dbReset.cleanup",
                        new Dictionary<string, object?>(),
                        "Cleaning up files...");

                    var dataDirectory = _pathResolver.GetDataDirectory();
                    if (Directory.Exists(dataDirectory))
                    {
                        var filesToDelete = new[] { "position.txt", "performance_data.json", "processing.marker" };
                        foreach (var file in filesToDelete)
                        {
                            var filePath = Path.Combine(dataDirectory, file);
                            if (File.Exists(filePath))
                            {
                                try
                                {
                                    File.Delete(filePath);
                                    _logger.LogInformation($"Deleted file: {file}");
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, $"Failed to delete file: {file}");
                                }
                            }
                        }
                    }
                }

                _logger.LogInformation($"Selective database reset completed successfully. Cleared {tablesToClear.Count} table(s)");

                // Final cancellation check before committing - don't commit if user cancelled
                cancellationToken.ThrowIfCancellationRequested();
                await deleteTransaction.CommitAsync(cancellationToken);

            }
            catch
            {
                // Rollback transaction on any error during deletion
                _logger.LogWarning("Error during deletion operations, rolling back transaction");
                await deleteTransaction.RollbackAsync();
                throw;
            }
            finally
            {
                // Re-enable foreign key triggers
                _logger.LogInformation("Re-enabling foreign key triggers");
                await context.Database.ExecuteSqlRawAsync("SET session_replication_role = DEFAULT;");
            }
            }); // end deleteStrategy.ExecuteAsync

            // Broadcast preference reset event AFTER all database operations complete
            // Note: UserSessionsCleared is now broadcast IMMEDIATELY after UserSessions deletion (not here)
            if (shouldBroadcastPreferencesReset)
            {
                _logger.LogInformation("Broadcasting UserPreferencesReset event to all clients");
                await _notifications.NotifyAllAsync(SignalREvents.UserPreferencesReset, new
                {
                    message = "User preferences have been reset to defaults",
                    timestamp = DateTime.UtcNow
                });
            }

            // Re-enabling foreign-key enforcement is part of the reset. Publish/commit the success
            // terminal only after the execution-strategy callback (including its finally) returned;
            // otherwise a cleanup failure could leave a failed reset reported as completed.
            terminalOutcome = OperationStatus.Completed;
            await ReportResetProgressAsync(
                operationId,
                false,
                100.0,
                OperationStatus.Completed,
                "signalr.dbReset.complete",
                new Dictionary<string, object?>(),
                $"Successfully cleared {tablesToClear.Count} table(s): {string.Join(", ", tablesToClear)}",
                tablesCleared: tablesToClear.Count,
                totalTables: tablesToClear.Count);

            _operationTracker.CompleteOperation(operationId, success: true);
        }
        catch (OperationCanceledException)
        {
            terminalOutcome = OperationStatus.Cancelled;
            _logger.LogInformation("Database reset operation {OperationId} was cancelled by user", operationId);

            var current = Volatile.Read(ref _currentResetProgress);
            await ReportResetProgressAsync(
                operationId,
                false,
                current?.Snapshot.PercentComplete ?? 0,
                OperationStatus.Cancelled,
                "signalr.dbReset.cancelled",
                new Dictionary<string, object?>(),
                "Database reset was cancelled by user");

            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            terminalOutcome = OperationStatus.Failed;
            _logger.LogError(ex, "Error during selective database reset");

            var current = Volatile.Read(ref _currentResetProgress);
            await ReportResetProgressAsync(
                operationId,
                false,
                current?.Snapshot.PercentComplete ?? 0,
                OperationStatus.Failed,
                "signalr.dbReset.failed",
                new Dictionary<string, object?> { ["errorDetail"] = ex.Message },
                $"Database reset failed: {ex.Message}");

            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
        finally
        {
            // Clean up operation tracking
            _activeResetOperations.TryRemove(operationId, out _);
            if (_currentResetOperationId == operationId)
            {
                _currentResetOperationId = null;
            }

            var progress = Volatile.Read(ref _currentResetProgress);
            if (progress?.OperationId == operationId)
            {
                Volatile.Write(ref _currentResetProgress, null);
            }

            _logger.LogInformation("Reset operation {OperationId} ended with {Outcome}", operationId, terminalOutcome);
        }
    }

    private Task ReportClearedTableAsync(
        Guid operationId,
        double percentComplete,
        string tableName,
        int count,
        string message) =>
        ReportResetProgressAsync(
            operationId,
            true,
            percentComplete,
            OperationStatus.Running,
            "signalr.dbReset.clearedTable",
            new Dictionary<string, object?>
            {
                ["tableName"] = tableName,
                ["count"] = count
            },
            message);

    private async Task ReportResetProgressAsync(
        Guid operationId,
        bool isProcessing,
        double percentComplete,
        OperationStatus status,
        string stageKey,
        IReadOnlyDictionary<string, object?> context,
        string message,
        int? tablesCleared = null,
        int? totalTables = null,
        int? filesDeleted = null)
    {
        var snapshot = OperationProgressSnapshot.Create(
            stageKey,
            percentComplete,
            context,
            Interlocked.Increment(ref _resetProgressRevision));
        var progress = new ResetProgressInfo(
            operationId,
            isProcessing,
            status,
            message,
            snapshot,
            tablesCleared,
            totalTables,
            filesDeleted);
        Volatile.Write(ref _currentResetProgress, progress);
        _operationTracker.UpdateProgress(operationId, snapshot.PercentComplete, snapshot.StageKey);

        if (isProcessing && !_resetProgressEmitGate.ShouldEmit(snapshot.StageKey, snapshot.Revision))
        {
            return;
        }

        await _notifications.NotifyAllAsync(
            SignalREvents.DatabaseResetProgress,
            new DatabaseResetProgress(
                OperationId: operationId,
                IsProcessing: isProcessing,
                PercentComplete: snapshot.PercentComplete,
                Status: status,
                StageKey: snapshot.StageKey,
                Message: message,
                TablesCleared: tablesCleared,
                TotalTables: totalTables,
                FilesDeleted: filesDeleted,
                Timestamp: DateTime.UtcNow,
                Context: snapshot.Context));
    }

    /// <summary>
    /// Validates reset-table input and expands an explicit corruption-table selection to the
    /// candidate/header pair (the rows are only meaningful together). Clearing log entries no
    /// longer deletes corruption snapshots: retained scans are view-only history, so a log
    /// clear merely demotes the current Repeated-MISS scan after the table loop.
    /// </summary>
    internal static List<string> ResolveResetTables(IEnumerable<string> tableNames)
    {
        var tables = tableNames
            .Where(_validResetTables.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tables.Contains(CachedCorruptionDetectionsTable, StringComparer.Ordinal) ||
            tables.Contains(CachedCorruptionScansTable, StringComparer.Ordinal))
        {
            if (!tables.Contains(CachedCorruptionDetectionsTable, StringComparer.Ordinal))
            {
                tables.Add(CachedCorruptionDetectionsTable);
            }

            if (!tables.Contains(CachedCorruptionScansTable, StringComparer.Ordinal))
            {
                tables.Add(CachedCorruptionScansTable);
            }
        }

        return tables;
    }

    /// <summary>
    /// Returns the row count used by selective-reset progress. Kept as an internal seam so every
    /// allowlisted table, including the corruption scan header, has direct relational-provider
    /// coverage.
    /// </summary>
    internal static async Task<int> CountResetTableRowsAsync(
        AppDbContext context,
        string tableName,
        CancellationToken cancellationToken)
    {
        return tableName switch
        {
            "LogEntries" => await context.LogEntries.CountAsync(cancellationToken),
            "Downloads" => await context.Downloads.CountAsync(cancellationToken),
            "ClientStats" => await context.ClientStats.CountAsync(cancellationToken),
            "ServiceStats" => await context.ServiceStats.CountAsync(cancellationToken),
            "SteamDepotMappings" => await context.SteamDepotMappings.CountAsync(cancellationToken),
            "CachedGameDetections" => await context.CachedGameDetections.CountAsync(cancellationToken),
            "CachedServiceDetections" => await context.CachedServiceDetections.CountAsync(cancellationToken),
            CachedCorruptionDetectionsTable => await context.CachedCorruptionDetections.CountAsync(cancellationToken),
            CachedCorruptionScansTable => await context.CachedCorruptionScans.CountAsync(cancellationToken),
            "ClientGroups" => await context.ClientGroups.CountAsync(cancellationToken),
            "UserSessions" => await context.UserSessions.CountAsync(cancellationToken),
            "UserPreferences" => await context.UserPreferences.CountAsync(cancellationToken),
            "Events" => await context.Events.CountAsync(cancellationToken),
            "EventDownloads" => await context.EventDownloads.CountAsync(cancellationToken),
            "PrefillSessions" => await context.PrefillSessions.CountAsync(cancellationToken),
            "PrefillHistoryEntries" => await context.PrefillHistoryEntries.CountAsync(cancellationToken),
            "PrefillCachedDepots" => await context.PrefillCachedDepots.CountAsync(cancellationToken),
            "BannedSteamUsers" => await context.BannedSteamUsers.CountAsync(cancellationToken),
            "CacheSnapshots" => await context.CacheSnapshots.CountAsync(cancellationToken),
            "EpicGameMappings" => await context.EpicGameMappings.CountAsync(cancellationToken),
            "EpicCdnPatterns" => await context.EpicCdnPatterns.CountAsync(cancellationToken),
            _ => 0
        };
    }

    /// <summary>
    /// Revokes currentness on stored corruption scans after cache or log evidence changed,
    /// optionally scoped to one detection method. Completed snapshots and their candidate rows
    /// are retained: history is view-only (it can never authorize a removal), so demotion keeps
    /// the reference trail while <c>GetDetectionAsync</c>/removal gates stop honoring the scan.
    /// The single UPDATE is atomic, so failure or cancellation retains the prior current scan.
    /// </summary>
    internal static async Task<int> DemoteCachedCorruptionEvidenceAsync(
        AppDbContext context,
        CancellationToken cancellationToken,
        CorruptionDetectionMode? detectionMode = null)
    {
        var currentScans = context.CachedCorruptionScans.Where(scan => scan.IsCurrent);
        if (detectionMode is { } mode)
        {
            currentScans = currentScans.Where(scan => scan.DetectionMode == mode);
        }

        return await currentScans.ExecuteUpdateAsync(
            setters => setters.SetProperty(scan => scan.IsCurrent, false),
            cancellationToken);
    }

    /// <summary>
    /// Deletes all current and historical corruption snapshots in FK-safe order. The caller owns the
    /// transaction so this composes with selective reset and cache-clearing invalidation.
    /// </summary>
    internal static async Task<(int Candidates, int Scans)> DeleteCachedCorruptionEvidenceAsync(
        AppDbContext context,
        CancellationToken cancellationToken)
    {
        var candidates = await context.CachedCorruptionDetections.ExecuteDeleteAsync(cancellationToken);
        var scans = await context.CachedCorruptionScans.ExecuteDeleteAsync(cancellationToken);
        return (candidates, scans);
    }

}
