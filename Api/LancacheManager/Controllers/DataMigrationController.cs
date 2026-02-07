using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Security;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Text.Json.Serialization;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for importing data from DeveLanCacheUI_Backend
/// Allows migration of historical download data using Rust data_migrator binary
/// </summary>
[ApiController]
[Route("api/migration")]
public class DataMigrationController : ControllerBase
{
    private readonly ILogger<DataMigrationController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;

    public DataMigrationController(
        ILogger<DataMigrationController> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// POST /api/migration/import-develancache - Import data from DeveLanCacheUI_Backend database
    /// Request body: { "connectionString": "Data Source=path/to/develancache.db", "batchSize": 1000, "overwriteExisting": false }
    /// </summary>
    [HttpPost("import-develancache")]
    [RequireAuth]
    public async Task<IActionResult> ImportFromDeveLanCache([FromBody] DataMigrationImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new ErrorResponse { Error = "Connection string is required" });
        }

        // Check if an import is already running
        var activeImports = _operationTracker.GetActiveOperations(OperationType.DataImport);
        if (activeImports.Any())
        {
            return Conflict(new ErrorResponse { Error = "A data import operation is already running" });
        }

        _logger.LogInformation("Starting import from DeveLanCacheUI_Backend database");

        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(OperationType.DataImport, "DeveLanCacheUI Import", cts);

        // Extract the database path - supports both raw paths and connection strings
        var sourceDatabasePath = ExtractDatabasePath(request.ConnectionString);
        if (string.IsNullOrWhiteSpace(sourceDatabasePath) || !System.IO.File.Exists(sourceDatabasePath))
        {
            _operationTracker.CompleteOperation(operationId, false, "Source database file not found");
            return BadRequest(new ErrorResponse { Error = "Source database file not found at specified path" });
        }

        // Get target database path
        var targetDatabasePath = _pathResolver.GetDatabasePath();

        // Get data migrator binary path
        var dataMigratorPath = _pathResolver.GetRustDataMigratorPath();
        _rustProcessHelper.ValidateRustBinaryExists(dataMigratorPath, "data_migrator");

        // Create temporary progress file
        var progressPath = Path.GetTempFileName();

        // Send started notification
        _operationTracker.UpdateProgress(operationId, 0, "Starting DeveLanCacheUI_Backend import...");
        await _notifications.NotifyAllAsync(SignalREvents.DataImportStarted, new
        {
            OperationId = operationId,
            Message = "Starting DeveLanCacheUI_Backend import...",
            ImportType = "develancache"
        });

        try
        {
            // Build arguments for data_migrator
            var batchSize = request.BatchSize ?? 1000;
            var overwriteFlag = request.OverwriteExisting ? "1" : "0";
            var arguments = $"\"{sourceDatabasePath}\" \"{targetDatabasePath}\" \"{progressPath}\" {overwriteFlag} {batchSize}";

            _logger.LogInformation("[data_migrator] Executing: {Binary} {Args}", dataMigratorPath, arguments);

            // Start the process
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(dataMigratorPath, arguments);
            using var process = Process.Start(startInfo);

            if (process == null)
            {
                _operationTracker.CompleteOperation(operationId, false, "Failed to start data migrator process");
                await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
                {
                    OperationId = operationId,
                    Success = false,
                    Message = "Failed to start data migrator process"
                });
                return StatusCode(500, new ErrorResponse { Error = "Failed to start data migrator process" });
            }

            // Monitor stdout and stderr
            var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(process, "data_migrator");

            // Wait for process to complete
            await process.WaitForExitAsync();

            // Wait for output tasks
            await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

            if (process.ExitCode != 0)
            {
                var errorMessage = $"Data migration failed with exit code {process.ExitCode}";
                _operationTracker.CompleteOperation(operationId, false, errorMessage);
                await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
                {
                    OperationId = operationId,
                    Success = false,
                    Message = errorMessage
                });
                return StatusCode(500, new ErrorResponse
                {
                    Error = "Data migration failed",
                    Details = $"Process exited with code {process.ExitCode}"
                });
            }

            // Read final progress to get statistics
            var progress = await _rustProcessHelper.ReadProgressFileAsync<MigrationProgress>(progressPath);

            if (progress == null)
            {
                _logger.LogWarning("Progress file not found after migration");
                _operationTracker.CompleteOperation(operationId, true);
                await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
                {
                    OperationId = operationId,
                    Success = true,
                    Message = "Import completed but progress data unavailable"
                });
                return Ok(new MessageResponse { Message = "Import completed but progress data unavailable" });
            }

            _logger.LogInformation(
                "Import completed: {Imported} imported, {Skipped} skipped, {Errors} errors. Backup: {BackupPath}",
                progress.RecordsImported, progress.RecordsSkipped, progress.RecordsErrors, progress.BackupPath ?? "none");

            // Send completion notification
            _operationTracker.UpdateProgress(operationId, 100, $"Import completed: {progress.RecordsImported} imported, {progress.RecordsSkipped} skipped");
            _operationTracker.CompleteOperation(operationId, true);
            await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
            {
                OperationId = operationId,
                Success = true,
                Message = $"Import completed: {progress.RecordsImported} imported, {progress.RecordsSkipped} skipped",
                RecordsImported = progress.RecordsImported,
                RecordsSkipped = progress.RecordsSkipped,
                RecordsErrors = progress.RecordsErrors,
                TotalRecords = progress.RecordsProcessed
            });

            return Ok(new MigrationImportResponse
            {
                Message = progress.Message,
                TotalRecords = progress.RecordsProcessed,
                Imported = progress.RecordsImported,
                Skipped = progress.RecordsSkipped,
                Errors = progress.RecordsErrors,
                BackupPath = progress.BackupPath
            });
        }
        catch (Exception ex)
        {
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
            {
                OperationId = operationId,
                Success = false,
                Message = ex.Message
            });
            throw;
        }
        finally
        {
            // Clean up progress file
            await _rustProcessHelper.DeleteTemporaryFileAsync(progressPath);
        }
    }

    /// <summary>
    /// POST /api/migration/import-lancache-manager - Import data from another LancacheManager database
    /// Request body: { "connectionString": "Data Source=path/to/lancachemanager.db", "batchSize": 1000, "overwriteExisting": false }
    /// </summary>
    [HttpPost("import-lancache-manager")]
    [RequireAuth]
    public async Task<IActionResult> ImportFromLancacheManager([FromBody] DataMigrationImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new ErrorResponse { Error = "Connection string is required" });
        }

        // Check if an import is already running
        var activeImports = _operationTracker.GetActiveOperations(OperationType.DataImport);
        if (activeImports.Any())
        {
            return Conflict(new ErrorResponse { Error = "A data import operation is already running" });
        }

        _logger.LogInformation("Starting import from LancacheManager database");

        var cts = new CancellationTokenSource();
        var operationId = _operationTracker.RegisterOperation(OperationType.DataImport, "LancacheManager Import", cts);

        // Extract the database path
        var sourceDatabasePath = ExtractDatabasePath(request.ConnectionString);
        if (string.IsNullOrWhiteSpace(sourceDatabasePath) || !System.IO.File.Exists(sourceDatabasePath))
        {
            _operationTracker.CompleteOperation(operationId, false, "Source database file not found");
            return BadRequest(new ErrorResponse { Error = "Source database file not found at specified path" });
        }

        // Get target database path
        var targetDatabasePath = _pathResolver.GetDatabasePath();

        // Ensure we're not importing from the same database
        if (Path.GetFullPath(sourceDatabasePath).Equals(Path.GetFullPath(targetDatabasePath), StringComparison.OrdinalIgnoreCase))
        {
            _operationTracker.CompleteOperation(operationId, false, "Cannot import from the same database");
            return BadRequest(new ErrorResponse { Error = "Cannot import from the same database that is currently in use" });
        }

        var batchSize = request.BatchSize ?? 1000;
        var overwriteExisting = request.OverwriteExisting;

        ulong totalRecords = 0;
        ulong recordsImported = 0;
        ulong recordsSkipped = 0;
        ulong recordsErrors = 0;
        string? backupPath = null;

        // Send started notification
        _operationTracker.UpdateProgress(operationId, 0, "Starting LancacheManager import...");
        await _notifications.NotifyAllAsync(SignalREvents.DataImportStarted, new
        {
            OperationId = operationId,
            Message = "Starting LancacheManager import...",
            ImportType = "lancache-manager"
        });

        try
        {
            // Create backup of target database
            if (System.IO.File.Exists(targetDatabasePath))
            {
                var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                backupPath = Path.Combine(
                    Path.GetDirectoryName(targetDatabasePath) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(targetDatabasePath)}.backup.{timestamp}{Path.GetExtension(targetDatabasePath)}"
                );
                System.IO.File.Copy(targetDatabasePath, backupPath);
                _logger.LogInformation("Created backup at {BackupPath}", backupPath);
            }

            // Open source database (read-only)
            var sourceConnStr = $"Data Source={sourceDatabasePath};Mode=ReadOnly;Default Timeout=5";
            using var sourceConn = new SqliteConnection(sourceConnStr);
            await sourceConn.OpenAsync();

            // Open target database (with busy timeout to handle concurrent access)
            var targetConnStr = $"Data Source={targetDatabasePath};Default Timeout=5";
            using var targetConn = new SqliteConnection(targetConnStr);
            await targetConn.OpenAsync();

            // Get total count from source
            using (var countCmd = sourceConn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM Downloads";
                totalRecords = Convert.ToUInt64(await countCmd.ExecuteScalarAsync());
            }

            _logger.LogInformation("Found {TotalRecords} records in source LancacheManager database", totalRecords);

            // Send initial progress
            _operationTracker.UpdateProgress(operationId, 0, $"Found {totalRecords:N0} records to import");
            await _notifications.NotifyAllAsync(SignalREvents.DataImportProgress, new
            {
                OperationId = operationId,
                PercentComplete = 0.0,
                Status = "running",
                Message = $"Found {totalRecords:N0} records to import",
                RecordsProcessed = 0UL,
                TotalRecords = totalRecords
            });

            // Read and insert in batches
            var offset = 0;
            while (true)
            {
                // Read batch from source
                using var readCmd = sourceConn.CreateCommand();
                readCmd.CommandText = @"
                    SELECT Service, ClientIp, StartTimeUtc, EndTimeUtc, StartTimeLocal, EndTimeLocal,
                           CacheHitBytes, CacheMissBytes, IsActive, DepotId, GameAppId, Datasource
                    FROM Downloads
                    ORDER BY StartTimeUtc
                    LIMIT @limit OFFSET @offset";
                readCmd.Parameters.AddWithValue("@limit", batchSize);
                readCmd.Parameters.AddWithValue("@offset", offset);

                using var reader = await readCmd.ExecuteReaderAsync();
                var hasRecords = false;

                using var transaction = targetConn.BeginTransaction();

                while (await reader.ReadAsync())
                {
                    hasRecords = true;

                    var service = reader.GetString(0);
                    var clientIp = reader.GetString(1);
                    var startTimeUtc = reader.GetString(2);
                    var endTimeUtc = reader.GetString(3);
                    var startTimeLocal = reader.IsDBNull(4) ? startTimeUtc : reader.GetString(4);
                    var endTimeLocal = reader.IsDBNull(5) ? endTimeUtc : reader.GetString(5);
                    var cacheHitBytes = reader.GetInt64(6);
                    var cacheMissBytes = reader.GetInt64(7);
                    var isActive = reader.IsDBNull(8) ? 0 : reader.GetInt32(8);
                    var depotId = reader.IsDBNull(9) ? (int?)null : reader.GetInt32(9);
                    var gameAppId = reader.IsDBNull(10) ? (int?)null : reader.GetInt32(10);
                    var datasource = reader.IsDBNull(11) ? "default" : reader.GetString(11);

                    try
                    {
                        // Check if record exists
                        using var checkCmd = targetConn.CreateCommand();
                        checkCmd.Transaction = transaction;
                        checkCmd.CommandText = "SELECT COUNT(*) FROM Downloads WHERE ClientIp = @clientIp AND StartTimeUtc = @startTimeUtc";
                        checkCmd.Parameters.AddWithValue("@clientIp", clientIp);
                        checkCmd.Parameters.AddWithValue("@startTimeUtc", startTimeUtc);
                        var exists = Convert.ToInt32(await checkCmd.ExecuteScalarAsync()) > 0;

                        if (exists)
                        {
                            if (overwriteExisting)
                            {
                                using var updateCmd = targetConn.CreateCommand();
                                updateCmd.Transaction = transaction;
                                updateCmd.CommandText = @"
                                    UPDATE Downloads SET
                                        Service = @service, EndTimeUtc = @endTimeUtc, EndTimeLocal = @endTimeLocal,
                                        CacheHitBytes = @cacheHitBytes, CacheMissBytes = @cacheMissBytes,
                                        IsActive = @isActive, DepotId = @depotId, GameAppId = @gameAppId, Datasource = @datasource
                                    WHERE ClientIp = @clientIp AND StartTimeUtc = @startTimeUtc";
                                updateCmd.Parameters.AddWithValue("@service", service);
                                updateCmd.Parameters.AddWithValue("@endTimeUtc", endTimeUtc);
                                updateCmd.Parameters.AddWithValue("@endTimeLocal", endTimeLocal);
                                updateCmd.Parameters.AddWithValue("@cacheHitBytes", cacheHitBytes);
                                updateCmd.Parameters.AddWithValue("@cacheMissBytes", cacheMissBytes);
                                updateCmd.Parameters.AddWithValue("@isActive", isActive);
                                updateCmd.Parameters.AddWithValue("@depotId", (object?)depotId ?? DBNull.Value);
                                updateCmd.Parameters.AddWithValue("@gameAppId", (object?)gameAppId ?? DBNull.Value);
                                updateCmd.Parameters.AddWithValue("@datasource", datasource);
                                updateCmd.Parameters.AddWithValue("@clientIp", clientIp);
                                updateCmd.Parameters.AddWithValue("@startTimeUtc", startTimeUtc);
                                await updateCmd.ExecuteNonQueryAsync();
                                recordsImported++;
                            }
                            else
                            {
                                recordsSkipped++;
                            }
                        }
                        else
                        {
                            using var insertCmd = targetConn.CreateCommand();
                            insertCmd.Transaction = transaction;
                            insertCmd.CommandText = @"
                                INSERT INTO Downloads (Service, ClientIp, StartTimeUtc, EndTimeUtc, StartTimeLocal, EndTimeLocal,
                                    CacheHitBytes, CacheMissBytes, IsActive, DepotId, GameAppId, Datasource)
                                VALUES (@service, @clientIp, @startTimeUtc, @endTimeUtc, @startTimeLocal, @endTimeLocal,
                                    @cacheHitBytes, @cacheMissBytes, @isActive, @depotId, @gameAppId, @datasource)";
                            insertCmd.Parameters.AddWithValue("@service", service);
                            insertCmd.Parameters.AddWithValue("@clientIp", clientIp);
                            insertCmd.Parameters.AddWithValue("@startTimeUtc", startTimeUtc);
                            insertCmd.Parameters.AddWithValue("@endTimeUtc", endTimeUtc);
                            insertCmd.Parameters.AddWithValue("@startTimeLocal", startTimeLocal);
                            insertCmd.Parameters.AddWithValue("@endTimeLocal", endTimeLocal);
                            insertCmd.Parameters.AddWithValue("@cacheHitBytes", cacheHitBytes);
                            insertCmd.Parameters.AddWithValue("@cacheMissBytes", cacheMissBytes);
                            insertCmd.Parameters.AddWithValue("@isActive", isActive);
                            insertCmd.Parameters.AddWithValue("@depotId", (object?)depotId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@gameAppId", (object?)gameAppId ?? DBNull.Value);
                            insertCmd.Parameters.AddWithValue("@datasource", datasource);
                            await insertCmd.ExecuteNonQueryAsync();
                            recordsImported++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error processing record");
                        recordsErrors++;
                    }
                }

                transaction.Commit();

                // Send progress notification after each batch
                var recordsProcessed = (ulong)offset + (ulong)batchSize;
                if (recordsProcessed > totalRecords) recordsProcessed = totalRecords;
                var percentComplete = totalRecords > 0 ? (double)recordsProcessed / totalRecords * 100.0 : 0;

                var progressMessage = $"Importing records... {recordsProcessed:N0} of {totalRecords:N0}";
                _operationTracker.UpdateProgress(operationId, percentComplete, progressMessage);
                await _notifications.NotifyAllAsync(SignalREvents.DataImportProgress, new
                {
                    OperationId = operationId,
                    PercentComplete = percentComplete,
                    Status = "running",
                    Message = progressMessage,
                    RecordsProcessed = recordsProcessed,
                    TotalRecords = totalRecords,
                    RecordsImported = recordsImported,
                    RecordsSkipped = recordsSkipped
                });

                if (!hasRecords)
                    break;

                offset += batchSize;

                _logger.LogDebug("Processed {Offset} of {Total} records", offset, totalRecords);
            }

            _logger.LogInformation(
                "Import completed: {Imported} imported, {Skipped} skipped, {Errors} errors. Backup: {BackupPath}",
                recordsImported, recordsSkipped, recordsErrors, backupPath ?? "none");

            // Send completion notification
            _operationTracker.UpdateProgress(operationId, 100, $"Import completed: {recordsImported:N0} imported, {recordsSkipped:N0} skipped");
            _operationTracker.CompleteOperation(operationId, true);
            await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
            {
                OperationId = operationId,
                Success = true,
                Message = $"Import completed: {recordsImported:N0} imported, {recordsSkipped:N0} skipped",
                RecordsImported = recordsImported,
                RecordsSkipped = recordsSkipped,
                RecordsErrors = recordsErrors,
                TotalRecords = totalRecords
            });

            return Ok(new MigrationImportResponse
            {
                Message = $"Import completed: {recordsImported} imported, {recordsSkipped} skipped, {recordsErrors} errors",
                TotalRecords = totalRecords,
                Imported = recordsImported,
                Skipped = recordsSkipped,
                Errors = recordsErrors,
                BackupPath = backupPath
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during LancacheManager import");
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            await _notifications.NotifyAllAsync(SignalREvents.DataImportComplete, new
            {
                OperationId = operationId,
                Success = false,
                Message = $"Import failed: {ex.Message}",
                RecordsImported = recordsImported,
                RecordsSkipped = recordsSkipped,
                RecordsErrors = recordsErrors,
                TotalRecords = totalRecords
            });
            throw;
        }
    }

    /// <summary>
    /// GET /api/migration/import/status - Get current data import status
    /// Returns whether an import is running, progress percentage, status message, and operation ID
    /// </summary>
    [HttpGet("import/status")]
    [RequireGuestSession]
    public IActionResult GetImportStatus()
    {
        var activeImports = _operationTracker.GetActiveOperations(OperationType.DataImport).ToList();

        if (activeImports.Count == 0)
        {
            return Ok(new DataImportStatusResponse
            {
                IsProcessing = false
            });
        }

        var operation = activeImports.First();
        return Ok(new DataImportStatusResponse
        {
            IsProcessing = true,
            Status = operation.Status,
            Message = operation.Message,
            PercentComplete = operation.PercentComplete,
            OperationId = operation.Id
        });
    }

    /// <summary>
    /// GET /api/migration/validate-connection - Test connection to external database
    /// Query params: connectionString (supports raw path or "Data Source=..." format), importType (develancache or lancache-manager)
    /// </summary>
    [HttpGet("validate-connection")]
    [RequireAuth]
    public async Task<IActionResult> ValidateConnection([FromQuery] string connectionString, [FromQuery] string importType = "develancache")
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return BadRequest(new ErrorResponse { Error = "Database path is required" });
        }

        try
        {
            // Extract path and convert to connection string format for SqliteConnection
            var dbPath = ExtractDatabasePath(connectionString);
            var connStr = connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase)
                ? connectionString + ";Default Timeout=5"
                : $"Data Source={dbPath};Default Timeout=5";

            using var connection = new SqliteConnection(connStr);
            await connection.OpenAsync();

            // Determine which table to check based on import type
            var tableName = importType == "lancache-manager" ? "Downloads" : "DownloadEvents";

            // Check for the appropriate table
            var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT name FROM sqlite_master WHERE type='table' AND name='{tableName}'";
            var tableExists = await cmd.ExecuteScalarAsync();

            if (tableExists == null)
            {
                return Ok(new ConnectionValidationResponse
                {
                    Valid = false,
                    Message = $"Connection successful, but {tableName} table not found"
                });
            }

            // Get record count
            var countCmd = connection.CreateCommand();
            countCmd.CommandText = $"SELECT COUNT(*) FROM {tableName}";
            var recordCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            return Ok(new ConnectionValidationResponse
            {
                Valid = true,
                Message = "Connection successful",
                RecordCount = recordCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate connection");
            return Ok(new ConnectionValidationResponse
            {
                Valid = false,
                Message = ex.Message
            });
        }
    }

    /// <summary>
    /// Extracts the database path from either a raw path or SQLite connection string
    /// Supports: "/path/to/database.db" or "Data Source=/path/to/database.db"
    /// </summary>
    private string ExtractDatabasePath(string input)
    {
        var trimmed = input.Trim();

        // Check if it's a connection string format
        if (trimmed.Contains("Data Source", StringComparison.OrdinalIgnoreCase))
        {
            // Parse connection string to extract Data Source value
            var parts = trimmed.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var keyValue = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (keyValue.Length == 2 &&
                    keyValue[0].Equals("Data Source", StringComparison.OrdinalIgnoreCase))
                {
                    return keyValue[1].Trim();
                }
            }
            return string.Empty;
        }

        // Treat as raw file path
        return trimmed;
    }

}
