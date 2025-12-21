using LancacheManager.Application.Services;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Repositories.Interfaces;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace LancacheManager.Infrastructure.Repositories;

public class DatabaseRepository : IDatabaseRepository
{
    private readonly AppDbContext _context;
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly ILogger<DatabaseRepository> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly StateRepository _stateRepository;
    private readonly DatasourceService _datasourceService;
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

    public DatabaseRepository(
        AppDbContext context,
        IHubContext<DownloadHub> hubContext,
        ILogger<DatabaseRepository> logger,
        IPathResolver pathResolver,
        IDbContextFactory<AppDbContext> dbContextFactory,
        SteamKit2Service steamKit2Service,
        StateRepository stateRepository,
        DatasourceService datasourceService)
    {
        _context = context;
        _hubContext = hubContext;
        _logger = logger;
        _pathResolver = pathResolver;
        _dbContextFactory = dbContextFactory;
        _steamKit2Service = steamKit2Service;
        _stateRepository = stateRepository;
        _datasourceService = datasourceService;
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

    public async Task ResetDatabase()
    {
        try
        {
            _logger.LogInformation("Starting database reset with batched deletion");

            // Send initial progress update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = "Starting database reset...",
                timestamp = DateTime.UtcNow
            });

            // Count total rows for progress calculation - run all counts in parallel for speed
            var countTasks = new[]
            {
                _context.LogEntries.CountAsync(),
                _context.Downloads.CountAsync(),
                _context.ClientStats.CountAsync(),
                _context.ServiceStats.CountAsync()
            };
            var counts = await Task.WhenAll(countTasks);
            var logEntriesCount = counts[0];
            var downloadsCount = counts[1];
            var clientStatsCount = counts[2];
            var serviceStatsCount = counts[3];
            var totalRows = logEntriesCount + downloadsCount + clientStatsCount + serviceStatsCount;

            _logger.LogInformation($"Deleting {totalRows:N0} total rows: LogEntries={logEntriesCount:N0}, Downloads={downloadsCount:N0}, ClientStats={clientStatsCount:N0}, ServiceStats={serviceStatsCount:N0}");

            // Log what is being preserved
            var cachedGameDetectionsCount = await _context.CachedGameDetections.CountAsync();
            var depotMappingsCount = await _context.SteamDepotMappings.CountAsync();
            _logger.LogInformation($"Preserving {cachedGameDetectionsCount:N0} cached game detections and {depotMappingsCount:N0} depot mappings");

            // Delete LogEntries first (foreign key constraint - must be deleted before Downloads)
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 5.0,
                status = "deleting",
                message = "Clearing log entries...",
                timestamp = DateTime.UtcNow
            });
            var deletedLogEntries = await _context.LogEntries.ExecuteDeleteAsync();
            _logger.LogInformation($"Cleared {deletedLogEntries:N0} log entries");

            // Reset log positions for all datasources so the log processor knows to start from beginning
            _logger.LogInformation("Resetting log positions for all datasources");
            foreach (var ds in _datasourceService.GetDatasources())
            {
                _stateRepository.SetLogPosition(ds.Name, 0);
                _stateRepository.SetLogTotalLines(ds.Name, 0);
            }
            // Also reset legacy position
            _stateRepository.SetLogPosition(0);

            // Delete Downloads
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 25.0,
                status = "deleting",
                message = "Clearing downloads...",
                timestamp = DateTime.UtcNow
            });
            var deletedDownloads = await _context.Downloads.ExecuteDeleteAsync();
            _logger.LogInformation($"Cleared {deletedDownloads:N0} downloads");

            // Delete ClientStats
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 60.0,
                status = "deleting",
                message = "Clearing client stats...",
                timestamp = DateTime.UtcNow
            });
            var deletedClientStats = await _context.ClientStats.ExecuteDeleteAsync();
            _logger.LogInformation($"Cleared {deletedClientStats:N0} client stats");

            // Delete ServiceStats
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 75.0,
                status = "deleting",
                message = "Clearing service stats...",
                timestamp = DateTime.UtcNow
            });
            var deletedServiceStats = await _context.ServiceStats.ExecuteDeleteAsync();
            _logger.LogInformation($"Cleared {deletedServiceStats:N0} service stats");


            // Get data directory for file cleanup
            var dataDirectory = _pathResolver.GetDataDirectory();

            // Ensure data directory exists before working with files
            if (!Directory.Exists(dataDirectory))
            {
                Directory.CreateDirectory(dataDirectory);
                _logger.LogInformation($"Created data directory: {dataDirectory}");
            }

            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 85.0,
                status = "cleanup",
                message = "Cleaning up files...",
                timestamp = DateTime.UtcNow
            });

            // Clear position file
            var positionFile = Path.Combine(dataDirectory, "position.txt");
            if (File.Exists(positionFile))
            {
                File.Delete(positionFile);
            }

            // Clear performance data file
            var performanceFile = Path.Combine(dataDirectory, "performance_data.json");
            if (File.Exists(performanceFile))
            {
                File.Delete(performanceFile);
            }

            // Clear processing marker
            var processingMarker = Path.Combine(dataDirectory, "processing.marker");
            if (File.Exists(processingMarker))
            {
                File.Delete(processingMarker);
            }

            _logger.LogInformation($"Database reset completed successfully. Data directory: {dataDirectory}");
            _logger.LogInformation($"Preserved data: {cachedGameDetectionsCount:N0} game detections, {depotMappingsCount:N0} depot mappings");

            // Send completion update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "complete",
                message = $"Database reset completed successfully (preserved {cachedGameDetectionsCount:N0} game detections)",
                timestamp = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting database");

            // Send error update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "error",
                message = $"Database reset failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            throw;
        }
    }

    /// <summary>
    /// Starts a background task to reset selected tables and returns immediately
    /// </summary>
    public string StartResetSelectedTablesAsync(List<string> tableNames)
    {
        var operationId = Guid.NewGuid().ToString();

        if (_activeResetOperations.TryAdd(operationId, true))
        {
            _logger.LogInformation($"Starting background reset operation {operationId} for tables: {string.Join(", ", tableNames)}");

            // Initialize progress tracking
            _currentResetProgress = new ResetProgressInfo
            {
                IsProcessing = true,
                PercentComplete = 0,
                Message = $"Starting reset of {tableNames.Count} table(s)...",
                Status = "starting"
            };

            // Start background task
            _ = Task.Run(async () => await ResetSelectedTablesInternal(operationId, tableNames));
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
    private async Task ResetSelectedTablesInternal(string operationId, List<string> tableNames)
    {
        // Use a new DbContext from factory for background operation (don't use injected context)
        await using var context = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            _logger.LogInformation($"Starting selective database reset for tables: {string.Join(", ", tableNames)}");

            // Send initial progress update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = true,
                percentComplete = 0.0,
                status = "starting",
                message = $"Starting reset of {tableNames.Count} table(s)...",
                timestamp = DateTime.UtcNow
            });

            // Validate table names to prevent SQL injection
            var validTables = new HashSet<string> { "LogEntries", "Downloads", "ClientStats", "ServiceStats", "SteamDepotMappings", "CachedGameDetections", "UserSessions", "UserPreferences", "Events", "EventDownloads" };
            var tablesToClear = tableNames.Where(t => validTables.Contains(t)).ToList();

            if (tablesToClear.Count == 0)
            {
                _logger.LogWarning("No valid tables selected for reset");
                await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                {
                    isProcessing = false,
                    percentComplete = 0.0,
                    status = "error",
                    message = "No valid tables selected for reset",
                    timestamp = DateTime.UtcNow
                });
                return;
            }

            // Count total rows for progress calculation
            var totalRows = 0;
            foreach (var tableName in tablesToClear)
            {
                var count = tableName switch
                {
                    "LogEntries" => await context.LogEntries.CountAsync(),
                    "Downloads" => await context.Downloads.CountAsync(),
                    "ClientStats" => await context.ClientStats.CountAsync(),
                    "ServiceStats" => await context.ServiceStats.CountAsync(),
                    "SteamDepotMappings" => await context.SteamDepotMappings.CountAsync(),
                    "CachedGameDetections" => await context.CachedGameDetections.CountAsync(),
                    "UserSessions" => await context.UserSessions.CountAsync(),
                    "UserPreferences" => await context.UserPreferences.CountAsync(),
                    "Events" => await context.Events.CountAsync(),
                    "EventDownloads" => await context.EventDownloads.CountAsync(),
                    _ => 0
                };
                totalRows += count;
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

            try
            {
                // Delete tables based on selection
                // PRIORITY ORDER:
                // 1. UserSessions - ALWAYS FIRST to immediately invalidate all sessions
                // 2. UserPreferences - Must be deleted before UserSessions FK cascade
                // 3. LogEntries - Must be deleted before Downloads FK
                // 4. Downloads - Has FK dependency on LogEntries
                // 5. All others - No dependencies
                var orderedTables = tablesToClear.OrderBy(t =>
                    t == "UserSessions" ? 0 :      // HIGHEST PRIORITY - invalidate sessions first
                    t == "UserPreferences" ? 1 :   // Second - FK dependency on UserSessions
                    t == "EventDownloads" ? 2 :    // Third - FK dependency from Events and Downloads
                    t == "LogEntries" ? 3 :        // Fourth - FK dependency from Downloads
                    t == "Downloads" ? 4 :         // Fifth - depends on LogEntries
                    t == "Events" ? 5 :            // Sixth - EventDownloads depend on this
                    6).ToList();

                // Special case: If deleting Downloads but NOT LogEntries, we must null out the foreign keys first
                if (tablesToClear.Contains("Downloads") && !tablesToClear.Contains("LogEntries"))
                {
                    _logger.LogInformation("Nullifying LogEntry.DownloadId foreign keys before deleting Downloads table");
                    await context.LogEntries.Where(le => le.DownloadId != null)
                        .ExecuteUpdateAsync(s => s.SetProperty(le => le.DownloadId, (int?)null));
                }

                foreach (var tableName in orderedTables)
            {
                _logger.LogInformation($"Clearing table: {tableName}");

                switch (tableName)
                {
                    case "LogEntries":
                        // Use batched deletion to avoid locking the database for too long
                        // This allows other operations to proceed between batches
                        var logEntriesTotal = await context.LogEntries.CountAsync();
                        var logEntriesDeleted = 0;
                        const int batchSize = 100000;

                        _logger.LogInformation($"Starting batched deletion of {logEntriesTotal:N0} log entries (batch size: {batchSize:N0})");

                        while (true)
                        {
                            // Delete a batch using raw SQL for efficiency
                            // SQLite doesn't support LIMIT in DELETE, so we use a subquery
                            var deleted = await context.Database.ExecuteSqlRawAsync(
                                $"DELETE FROM LogEntries WHERE Id IN (SELECT Id FROM LogEntries LIMIT {batchSize})");

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

                            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                            {
                                isProcessing = true,
                                percentComplete = progressPercent,
                                status = "deleting",
                                message = progressMessage,
                                timestamp = DateTime.UtcNow
                            });

                            // Small delay to allow other operations to acquire the lock
                            await Task.Delay(50);
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

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared log entries ({logEntriesDeleted:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "Downloads":
                        // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                        var downloadsCount = await context.Downloads.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {downloadsCount:N0} downloads");
                        deletedRows += downloadsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared downloads ({downloadsCount:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "ClientStats":
                        // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                        var clientStatsCount = await context.ClientStats.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {clientStatsCount:N0} client stats");
                        deletedRows += clientStatsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared client stats ({clientStatsCount:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "ServiceStats":
                        // Use ExecuteDeleteAsync for direct deletion (much faster than batched deletion)
                        var serviceStatsCount = await context.ServiceStats.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {serviceStatsCount:N0} service stats");
                        deletedRows += serviceStatsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared service stats ({serviceStatsCount:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "SteamDepotMappings":
                        // Use ExecuteDeleteAsync for direct deletion (more efficient for this table)
                        var mappingCount = await context.SteamDepotMappings.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {mappingCount:N0} depot mappings");
                        deletedRows += mappingCount;

                        // Also clear game information from Downloads table (since depot mappings are gone)
                        _logger.LogInformation("Clearing game information from Downloads table (GameName, GameImageUrl, GameAppId)");
                        await context.Downloads
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(d => d.GameName, (string?)null)
                                .SetProperty(d => d.GameImageUrl, (string?)null)
                                .SetProperty(d => d.GameAppId, (uint?)null));
                        _logger.LogInformation("Cleared game information from all downloads");

                        // CRITICAL: Also delete the PICS JSON file to prevent re-import on next scan
                        // Without this, the JSON file would be imported back into the database
                        var picsJsonPath = Path.Combine(_pathResolver.GetDataDirectory(), "pics_depot_mappings.json");
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

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared depot mappings ({mappingCount:N0} rows) and unmapped all downloads",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "CachedGameDetections":
                        // Use ExecuteDeleteAsync for direct deletion (more efficient for this table)
                        var gameDetectionCount = await context.CachedGameDetections.ExecuteDeleteAsync();
                        var serviceDetectionCount = await context.CachedServiceDetections.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {gameDetectionCount:N0} cached game detections and {serviceDetectionCount:N0} cached service detections");
                        deletedRows += gameDetectionCount + serviceDetectionCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared cached game detections ({gameDetectionCount:N0} games, {serviceDetectionCount:N0} services)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "UserPreferences":
                        // Use ExecuteDeleteAsync for direct deletion
                        var userPreferencesCount = await context.UserPreferences.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {userPreferencesCount:N0} user preferences");
                        deletedRows += userPreferencesCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
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
                        var userSessionsCount = await context.UserSessions.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {userSessionsCount:N0} user sessions");
                        deletedRows += userSessionsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
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
                        await _hubContext.Clients.All.SendAsync("UserSessionsCleared", new
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
                        var eventsCount = await context.Events.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {eventsCount:N0} events (and associated event downloads via cascade)");
                        deletedRows += eventsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared events ({eventsCount:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;

                    case "EventDownloads":
                        // Use ExecuteDeleteAsync for direct deletion
                        var eventDownloadsCount = await context.EventDownloads.ExecuteDeleteAsync();
                        _logger.LogInformation($"Cleared {eventDownloadsCount:N0} event download associations");
                        deletedRows += eventDownloadsCount;

                        await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                        {
                            isProcessing = true,
                            percentComplete = Math.Min(currentProgress + progressPerTable, 85.0),
                            status = "deleting",
                            message = $"Cleared event download links ({eventDownloadsCount:N0} rows)",
                            timestamp = DateTime.UtcNow
                        });
                        break;
                }

                currentProgress += progressPerTable;
            }

            // Clean up files if LogEntries or Downloads were cleared
            if (tablesToClear.Contains("LogEntries") || tablesToClear.Contains("Downloads"))
            {
                await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
                {
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

            // Send completion update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 100.0,
                status = "complete",
                message = $"Successfully cleared {tablesToClear.Count} table(s): {string.Join(", ", tablesToClear)}",
                timestamp = DateTime.UtcNow
            });

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
                await _hubContext.Clients.All.SendAsync("UserPreferencesReset", new
                {
                    message = "User preferences have been reset to defaults",
                    timestamp = DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during selective database reset");

            // Send error update
            await _hubContext.Clients.All.SendAsync("DatabaseResetProgress", new
            {
                isProcessing = false,
                percentComplete = 0.0,
                status = "error",
                message = $"Database reset failed: {ex.Message}",
                timestamp = DateTime.UtcNow
            });

            throw;
        }
        finally
        {
            // Clean up operation tracking
            _activeResetOperations.TryRemove(operationId, out _);

            // Clear progress tracking
            _currentResetProgress = new ResetProgressInfo
            {
                IsProcessing = false,
                PercentComplete = 100,
                Message = "Database reset completed",
                Status = "completed"
            };

            _logger.LogInformation($"Reset operation {operationId} completed");
        }
    }

    public async Task<List<Download>> GetDownloadsWithApp0()
    {
        return await _context.Downloads
            .Where(d => d.GameAppId == 0)
            .ToListAsync();
    }

    public async Task MarkApp0DownloadsInactive()
    {
        var app0Downloads = await GetDownloadsWithApp0();
        foreach (var download in app0Downloads)
        {
            download.IsActive = false;
        }
        await _context.SaveChangesAsync();
    }

    public async Task<List<Download>> GetDownloadsWithBadImageUrls()
    {
        return await _context.Downloads
            .Where(d => d.GameImageUrl != null && d.GameImageUrl.Contains("cdn.akamai.steamstatic.com"))
            .ToListAsync();
    }

    public async Task<int> FixBadImageUrls()
    {
        var badImageUrls = await GetDownloadsWithBadImageUrls();

        if (badImageUrls.Any())
        {
            // Clear bad image URLs - they will be backfilled from Steam API
            foreach (var download in badImageUrls)
            {
                download.GameImageUrl = null;
            }

            await _context.SaveChangesAsync();
            return badImageUrls.Count;
        }

        return 0;
    }

    public async Task<int> ClearDepotMappings()
    {
        try
        {
            _logger.LogInformation("Clearing all depot mappings from database and downloads table");

            // ExecuteDeleteAsync returns the number of rows deleted
            // This avoids a slow COUNT query before deletion
            var count = await _context.SteamDepotMappings.ExecuteDeleteAsync();

            // Also clear game info from downloads table (set to null, keep download records)
            await _context.Downloads
                .Where(d => d.GameAppId != null || d.GameName != null || d.GameImageUrl != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(d => d.GameAppId, (uint?)null)
                    .SetProperty(d => d.GameName, (string?)null)
                    .SetProperty(d => d.GameImageUrl, (string?)null));

            await _context.SaveChangesAsync();

            // CRITICAL: Also delete the PICS JSON file to prevent re-import on next scan
            var picsJsonPath = Path.Combine(_pathResolver.GetDataDirectory(), "pics_depot_mappings.json");
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

            _logger.LogInformation("Cleared {Count} depot mappings from database and cleared game info from downloads table", count);
            return count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing depot mappings");
            throw;
        }
    }
}
