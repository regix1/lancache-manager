using LancacheManager.Core.Services;
using LancacheManager.Models;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
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
    private static readonly ConcurrentDictionary<string, bool> _activeResetOperations = new();
    private static ResetProgressInfo _currentResetProgress = new();

    public class ResetProgressInfo
    {
        public bool IsProcessing { get; set; }
        public double PercentComplete { get; set; }
        public string Message { get; set; } = "";
        public string Status { get; set; } = "idle";
    }

    public static ResetProgressInfo CurrentResetProgress => _currentResetProgress;

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
        return GetLogEntriesCount();
    }

    public async Task<int> GetLogEntriesCount()
    {
        return await _context.LogEntries.CountAsync();
    }

    /// <summary>
    /// Starts a background task to reset selected tables and returns immediately
    /// </summary>
    public string StartResetSelectedTablesAsync(List<string> tableNames)
    {
        // Create cancellation support
        var cts = new CancellationTokenSource();

        // Register with unified operation tracker for cancel support
        var operationId = _operationTracker.RegisterOperation(
            OperationType.DatabaseReset,
            "Database Reset",
            cts);

        if (_activeResetOperations.TryAdd(operationId, true))
        {
            _logger.LogInformation("Starting background reset operation {OperationId} for tables: {Tables}", operationId, string.Join(", ", tableNames));

            // Initialize progress tracking
            _currentResetProgress = new ResetProgressInfo
            {
                IsProcessing = true,
                PercentComplete = 0,
                Message = $"Starting reset of {tableNames.Count} table(s)...",
                Status = "starting"
            };

            // Send started event via SignalR
            _notifications.NotifyAllFireAndForget(SignalREvents.DatabaseResetStarted, new
            {
                OperationId = operationId,
                Message = $"Starting reset of {tableNames.Count} table(s)..."
            });

            // Start background task with cancellation token
            _ = Task.Run(async () => await ResetSelectedTablesInternalAsync(operationId, tableNames, cts.Token));
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
    private async Task ResetSelectedTablesInternalAsync(string operationId, List<string> tableNames, CancellationToken cancellationToken)
    {
        // Use a new DbContext from factory for background operation (don't use injected context)
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        try
        {
            _logger.LogInformation($"Starting selective database reset for tables: {string.Join(", ", tableNames)}");

            // Send initial progress update
            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                operationId,
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = $"Starting reset of {tableNames.Count} table(s)...",
                timestamp = DateTime.UtcNow
            });

            // Validate table names to prevent SQL injection
            var validTables = new HashSet<string> { "LogEntries", "Downloads", "ClientStats", "ServiceStats", "SteamDepotMappings", "CachedGameDetections", "CachedServiceDetections", "CachedCorruptionDetections", "ClientGroups", "UserSessions", "UserPreferences", "Events", "EventDownloads", "PrefillSessions", "PrefillHistoryEntries", "PrefillCachedDepots", "BannedSteamUsers", "CacheSnapshots", "EpicGameMappings", "EpicCdnPatterns" };
            var tablesToClear = tableNames.Where(t => validTables.Contains(t)).ToList();

            if (tablesToClear.Count == 0)
            {
                _logger.LogWarning("No valid tables selected for reset");
                await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                {
                    operationId,
                    isProcessing = false,
                    percentComplete = 0.0,
                    status = "failed",
                    message = "No valid tables selected for reset",
                    timestamp = DateTime.UtcNow
                });
                return;
            }

            // Count total rows for progress calculation - wrap in transaction for consistent snapshot
            int totalRows = 0;
            using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
            {
                try
                {
                    foreach (var tableName in tablesToClear)
                    {
                        var count = tableName switch
                        {
                            "LogEntries" => await context.LogEntries.CountAsync(cancellationToken),
                            "Downloads" => await context.Downloads.CountAsync(cancellationToken),
                            "ClientStats" => await context.ClientStats.CountAsync(cancellationToken),
                            "ServiceStats" => await context.ServiceStats.CountAsync(cancellationToken),
                            "SteamDepotMappings" => await context.SteamDepotMappings.CountAsync(cancellationToken),
                            "CachedGameDetections" => await context.CachedGameDetections.CountAsync(cancellationToken),
                            "CachedServiceDetections" => await context.CachedServiceDetections.CountAsync(cancellationToken),
                            "CachedCorruptionDetections" => await context.CachedCorruptionDetections.CountAsync(cancellationToken),
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

            _logger.LogInformation($"Clearing {totalRows:N0} total rows from {tablesToClear.Count} table(s)");

            int deletedRows = 0;
            double progressPerTable = 85.0 / tablesToClear.Count;
            double currentProgress = 0;

            // Track which preference reset event needs to be broadcast AFTER operation completes
            bool shouldBroadcastPreferencesReset = false;

            // Temporarily disable foreign key constraints for bulk deletion (SQLite)
            // This prevents FK constraint errors during table deletions
            _logger.LogInformation("Disabling foreign key constraints for bulk deletion");
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");

            // Wrap all deletion operations in a transaction for atomicity
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
                                // SQLite doesn't support LIMIT in DELETE, so we use a subquery
                                var deleted = await context.Database.ExecuteSqlInterpolatedAsync(
                                    $"DELETE FROM LogEntries WHERE Id IN (SELECT Id FROM LogEntries LIMIT {batchSize})", cancellationToken);

                                // Check for cancellation immediately after batch completes
                                // SQLite completes the batch synchronously, so we check here for fastest response
                                cancellationToken.ThrowIfCancellationRequested();

                                if (deleted == 0)
                                    break;

                                logEntriesDeleted += deleted;
                                var percentDone = logEntriesTotal > 0 ? (double)logEntriesDeleted / logEntriesTotal * 100 : 100;

                                _logger.LogInformation($"Deleted {logEntriesDeleted:N0}/{logEntriesTotal:N0} log entries ({percentDone:F1}%)");

                                var progressPercent = Math.Min(currentProgress + progressPerTable * (logEntriesDeleted / (double)Math.Max(logEntriesTotal, 1)), 85.0);
                                var progressMessage = $"Clearing log entries... {logEntriesDeleted:N0}/{logEntriesTotal:N0} ({percentDone:F1}%)";

                                // Update static progress for API access
                                _currentResetProgress = new ResetProgressInfo
                                {
                                    IsProcessing = true,
                                    PercentComplete = progressPercent,
                                    Message = progressMessage,
                                    Status = "deleting"
                                };

                                await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                                {
                                    operationId,
                                    isProcessing = true,
                                    percentComplete = progressPercent,
                                    status = "deleting",
                                    message = progressMessage,
                                    timestamp = DateTime.UtcNow
                                });

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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared log entries ({logEntriesDeleted:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "Downloads":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var downloadsCount = await context.Downloads.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {downloadsCount:N0} downloads");
                            deletedRows += downloadsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared downloads ({downloadsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "ClientStats":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var clientStatsCount = await context.ClientStats.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {clientStatsCount:N0} client stats");
                            deletedRows += clientStatsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared client stats ({clientStatsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "ServiceStats":
                            // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                            var serviceStatsCount = await context.ServiceStats.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {serviceStatsCount:N0} service stats");
                            deletedRows += serviceStatsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared service stats ({serviceStatsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared depot mappings ({mappingCount:N0} rows) and unmapped all downloads",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "CachedGameDetections":
                            // Use ExecuteDeleteAsync for direct deletion (more efficient for this table)
                            var gameDetectionCount = await context.CachedGameDetections.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {gameDetectionCount:N0} cached game detections");
                            deletedRows += gameDetectionCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared cached game detections ({gameDetectionCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "UserPreferences":
                            // Use ExecuteDeleteAsync for direct deletion
                            var userPreferencesCount = await context.UserPreferences.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {userPreferencesCount:N0} user preferences");
                            deletedRows += userPreferencesCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared user preferences ({userPreferencesCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });

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
                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared user sessions ({userSessionsCount:N0} rows) and Steam auth data",
                                timestamp = DateTime.UtcNow
                            });

                            // IMMEDIATELY broadcast UserSessionsCleared event to log out all connected users
                            // This happens RIGHT AFTER deletion, not at the end of the operation
                            // Note: This SignalR event will trigger the frontend to clear cookies and redirect to login
                            _logger.LogInformation("Broadcasting UserSessionsCleared event to all clients (immediate logout)");
                            await _notifications.NotifyAllAsync(SignalREvents.UserSessionsCleared, new
                            {
                                message = "All user sessions have been cleared - logging out immediately",
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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared events ({eventsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });

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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared event download links ({eventDownloadsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "CachedCorruptionDetections":
                            // Use ExecuteDeleteAsync for direct deletion
                            var corruptionDetectionCount = await context.CachedCorruptionDetections.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {corruptionDetectionCount:N0} cached corruption detections");
                            deletedRows += corruptionDetectionCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared corruption detection cache ({corruptionDetectionCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "ClientGroups":
                            // Use ExecuteDeleteAsync for direct deletion
                            // Note: ClientGroupMembers will be cascade deleted due to FK constraint
                            var clientGroupsCount = await context.ClientGroups.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {clientGroupsCount:N0} client groups (and associated members via cascade)");
                            deletedRows += clientGroupsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared client groups ({clientGroupsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });

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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared prefill sessions ({prefillSessionsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });

                            // Notify frontend to clear prefill sessions cache
                            await _notifications.NotifyAllAsync(SignalREvents.PrefillSessionsCleared, new
                            {
                                message = "All prefill sessions have been cleared",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "PrefillHistoryEntries":
                            // Use ExecuteDeleteAsync for direct deletion
                            var prefillHistoryCount = await context.PrefillHistoryEntries.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {prefillHistoryCount:N0} prefill history entries");
                            deletedRows += prefillHistoryCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared prefill history ({prefillHistoryCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "BannedSteamUsers":
                            // Use ExecuteDeleteAsync for direct deletion
                            var bannedUsersCount = await context.BannedSteamUsers.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {bannedUsersCount:N0} banned Steam users");
                            deletedRows += bannedUsersCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared banned Steam users ({bannedUsersCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });

                            // Notify frontend to refresh banned users
                            await _notifications.NotifyAllAsync(SignalREvents.BannedSteamUsersCleared, new
                            {
                                message = "All banned Steam users have been cleared",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "CachedServiceDetections":
                            // Use ExecuteDeleteAsync for direct deletion
                            var serviceDetectionsCount = await context.CachedServiceDetections.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {serviceDetectionsCount:N0} cached service detections");
                            deletedRows += serviceDetectionsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared service detection cache ({serviceDetectionsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "PrefillCachedDepots":
                            // Use ExecuteDeleteAsync for direct deletion
                            var prefillCachedDepotsCount = await context.PrefillCachedDepots.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {prefillCachedDepotsCount:N0} prefill cached depots");
                            deletedRows += prefillCachedDepotsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared prefill cache status ({prefillCachedDepotsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "CacheSnapshots":
                            // Use ExecuteDeleteAsync for direct deletion
                            var cacheSnapshotsCount = await context.CacheSnapshots.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {cacheSnapshotsCount:N0} cache snapshots");
                            deletedRows += cacheSnapshotsCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared cache size history ({cacheSnapshotsCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
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

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared Epic game mappings ({epicMappingCount:N0} rows) and unmapped all Epic downloads",
                                timestamp = DateTime.UtcNow
                            });
                            break;

                        case "EpicCdnPatterns":
                            var epicCdnCount = await context.EpicCdnPatterns.ExecuteDeleteAsync(cancellationToken);
                            _logger.LogInformation($"Cleared {epicCdnCount:N0} Epic CDN patterns");
                            deletedRows += epicCdnCount;

                            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                            {
                                operationId,
                                isProcessing = true,
                                percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                                status = "deleting",
                                message = $"Cleared Epic CDN patterns ({epicCdnCount:N0} rows)",
                                timestamp = DateTime.UtcNow
                            });
                            break;
                    }

                    currentProgress += progressPerTable;
                }

                // Clean up files if LogEntries or Downloads were cleared
                if (tablesToClear.Contains("LogEntries") || tablesToClear.Contains("Downloads"))
                {
                    await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                    {
                        operationId,
                        isProcessing = true,
                        percentComplete = 90.0,
                        status = "cleanup",
                        message = "Cleaning up files...",
                        timestamp = DateTime.UtcNow
                    });

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

                // Send completion update
                await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
                {
                    operationId,
                    isProcessing = false,
                    percentComplete = 100.0,
                    status = "completed",
                    message = $"Successfully cleared {tablesToClear.Count} table(s): {string.Join(", ", tablesToClear)}",
                    timestamp = DateTime.UtcNow
                });

                _operationTracker.CompleteOperation(operationId, success: true);
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
                // Re-enable foreign key constraints
                _logger.LogInformation("Re-enabling foreign key constraints");
                await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
            }

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
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Database reset operation {OperationId} was cancelled by user", operationId);

            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                operationId,
                isProcessing = false,
                percentComplete = 0.0,
                status = "cancelled",
                message = "Database reset was cancelled by user",
                timestamp = DateTime.UtcNow
            });

            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during selective database reset");

            await _notifications.NotifyAllAsync(SignalREvents.DatabaseResetProgress, new
            {
                operationId,
                isProcessing = false,
                percentComplete = 0.0,
                status = "failed",
                message = $"Database reset failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
        finally
        {
            // Clean up operation tracking
            _activeResetOperations.TryRemove(operationId, out _);

            // Clear progress tracking - reflect actual outcome
            var wasCancelled = cancellationToken.IsCancellationRequested;
            _currentResetProgress = new ResetProgressInfo
            {
                IsProcessing = false,
                PercentComplete = wasCancelled ? 0 : 100,
                Message = wasCancelled ? "Database reset was cancelled" : "Database reset completed",
                Status = wasCancelled ? "cancelled" : "completed"
            };

            _logger.LogInformation("Reset operation {OperationId} {Outcome}", operationId, wasCancelled ? "cancelled" : "completed");
        }
    }

}
