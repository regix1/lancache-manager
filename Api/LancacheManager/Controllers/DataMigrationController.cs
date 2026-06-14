using LancacheManager.Models;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace LancacheManager.Controllers;

/// <summary>
/// Controller for importing historical download data from another LancacheManager database.
/// </summary>
[ApiController]
[Route("api/migration")]
[Authorize(Policy = "AdminOnly")]
public class DataMigrationController : ControllerBase
{
    private static readonly SemaphoreSlim _importStartLock = new(1, 1);

    /// <summary>
    /// Per-operation mutable result holder. The two import action methods share one
    /// <see cref="StartImportAsync"/> register factory, but the terminal metrics
    /// (counts + message) are only known at emit time. Each method fills this holder
    /// immediately BEFORE calling <c>CompleteOperation</c>; the <c>onTerminalEmit</c>
    /// closure (created in the factory) reads it to build the single DataImportComplete
    /// event. Records* stay null for success-no-progress and the early validation
    /// fast-fail paths.
    /// </summary>
    private sealed class DataImportResultHolder
    {
        public string? Message { get; set; }
        public ulong? RecordsImported { get; set; }
        public ulong? RecordsSkipped { get; set; }
        public ulong? RecordsErrors { get; set; }
        public ulong? TotalRecords { get; set; }
    }

    private readonly ILogger<DataMigrationController> _logger;
    private readonly ISignalRNotificationService _notifications;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IConfiguration _configuration;

    public DataMigrationController(
        ILogger<DataMigrationController> logger,
        ISignalRNotificationService notifications,
        IUnifiedOperationTracker operationTracker,
        IOperationConflictChecker conflictChecker,
        IConfiguration configuration)
    {
        _logger = logger;
        _notifications = notifications;
        _operationTracker = operationTracker;
        _conflictChecker = conflictChecker;
        _configuration = configuration;
    }

    /// <summary>
    /// POST /api/migration/import-lancache-manager - Import data from another LancacheManager database
    /// Request body: { "connectionString": "Host=localhost;Database=lancachemanager;Username=postgres;Password=...", "batchSize": 1000, "overwriteExisting": false }
    /// </summary>
    [HttpPost("import-lancache-manager")]
    public async Task<IActionResult> ImportLancacheManagerAsync([FromBody] DataMigrationImportRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new ErrorResponse { Error = "Connection string is required" });
        }

        var start = await StartImportAsync("LancacheManager Import", cancellationToken);
        if (start.Conflict != null)
        {
            return Conflict(start.Conflict);
        }

        _logger.LogInformation("Starting import from LancacheManager database");

        var cts = start.CancellationTokenSource!;
        var operationId = start.OperationId!.Value;
        var result = start.Result!;

        NpgsqlConnectionStringBuilder sourceConnBuilder;
        try
        {
            sourceConnBuilder = new NpgsqlConnectionStringBuilder(request.ConnectionString);
            if (string.IsNullOrWhiteSpace(sourceConnBuilder.Host) || string.IsNullOrWhiteSpace(sourceConnBuilder.Database))
            {
                throw new InvalidOperationException("Host and Database are required");
            }
        }
        catch (Exception ex)
        {
            _operationTracker.CompleteOperation(operationId, false, "Invalid source connection string");
            _logger.LogWarning(ex, "Invalid source connection string for LancacheManager import");
            return BadRequest(new ErrorResponse { Error = "Invalid source PostgreSQL connection string" });
        }

        var targetConnectionString = _configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(targetConnectionString))
        {
            _operationTracker.CompleteOperation(operationId, false, "Target connection string is missing");
            return StatusCode(500, new ErrorResponse { Error = "Server database connection is not configured" });
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
            // Wire value matches the old ImportType enum's JSON converter output exactly.
            ImportType = "lancache-manager"
        });

        try
        {
            var targetConnBuilder = new NpgsqlConnectionStringBuilder(targetConnectionString);
            if (string.Equals(sourceConnBuilder.Host, targetConnBuilder.Host, StringComparison.OrdinalIgnoreCase) &&
                sourceConnBuilder.Port == targetConnBuilder.Port &&
                string.Equals(sourceConnBuilder.Database, targetConnBuilder.Database, StringComparison.OrdinalIgnoreCase))
            {
                _operationTracker.CompleteOperation(operationId, false, "Source and target databases are identical");
                return BadRequest(new ErrorResponse { Error = "Cannot import from the same database that is currently in use" });
            }

            // Open source database (read-only)
            using var sourceConn = new NpgsqlConnection(request.ConnectionString);
            await sourceConn.OpenAsync();

            // Open target database
            using var targetConn = new NpgsqlConnection(targetConnectionString);
            await targetConn.OpenAsync();

            // Get total count from source
            using (var countCmd = sourceConn.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM \"Downloads\"";
                totalRecords = Convert.ToUInt64(await countCmd.ExecuteScalarAsync());
            }

            _logger.LogInformation("Found {TotalRecords} records in source LancacheManager database", totalRecords);

            // Send initial progress
            _operationTracker.UpdateProgress(operationId, 0, $"Found {totalRecords:N0} records to import");
            await _notifications.NotifyAllAsync(SignalREvents.DataImportProgress, new
            {
                OperationId = operationId,
                PercentComplete = 0.0,
                Status = OperationStatus.Running,
                Message = $"Found {totalRecords:N0} records to import",
                RecordsProcessed = 0UL,
                TotalRecords = totalRecords
            });

            // Read and insert in batches
            var offset = 0;
            while (true)
            {
                // Check for cancellation between batches
                cts.Token.ThrowIfCancellationRequested();

                // Read batch from source
                using var readCmd = sourceConn.CreateCommand();
                readCmd.CommandText = @"
                    SELECT ""Service"", ""ClientIp"", ""StartTimeUtc"", ""EndTimeUtc"", ""StartTimeLocal"", ""EndTimeLocal"",
                           ""CacheHitBytes"", ""CacheMissBytes"", ""IsActive"", ""DepotId"", ""GameAppId"", ""Datasource""
                    FROM ""Downloads""
                    ORDER BY ""StartTimeUtc""
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
                    var depotId = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
                    var gameAppId = reader.IsDBNull(10) ? (long?)null : reader.GetInt64(10);
                    var datasource = reader.IsDBNull(11) ? "default" : reader.GetString(11);

                    try
                    {
                        // Check if record exists
                        using var checkCmd = targetConn.CreateCommand();
                        checkCmd.Transaction = transaction;
                        checkCmd.CommandText = "SELECT COUNT(*) FROM \"Downloads\" WHERE \"ClientIp\" = @clientIp AND \"StartTimeUtc\" = @startTimeUtc";
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
                                    UPDATE ""Downloads"" SET
                                        ""Service"" = @service, ""EndTimeUtc"" = @endTimeUtc, ""EndTimeLocal"" = @endTimeLocal,
                                        ""CacheHitBytes"" = @cacheHitBytes, ""CacheMissBytes"" = @cacheMissBytes,
                                        ""IsActive"" = @isActive, ""DepotId"" = @depotId, ""GameAppId"" = @gameAppId, ""Datasource"" = @datasource
                                    WHERE ""ClientIp"" = @clientIp AND ""StartTimeUtc"" = @startTimeUtc";
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
                                INSERT INTO ""Downloads"" (""Service"", ""ClientIp"", ""StartTimeUtc"", ""EndTimeUtc"", ""StartTimeLocal"", ""EndTimeLocal"",
                                    ""CacheHitBytes"", ""CacheMissBytes"", ""IsActive"", ""DepotId"", ""GameAppId"", ""Datasource"")
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
                    Status = OperationStatus.Running,
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
            // Fill the per-op holder so the onTerminalEmit closure reproduces the old success wire shape.
            result.Message = $"Import completed: {recordsImported:N0} imported, {recordsSkipped:N0} skipped";
            result.RecordsImported = recordsImported;
            result.RecordsSkipped = recordsSkipped;
            result.RecordsErrors = recordsErrors;
            result.TotalRecords = totalRecords;
            _operationTracker.CompleteOperation(operationId, true);

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
        catch (OperationCanceledException)
        {
            _logger.LogInformation("LancacheManager import was cancelled by user");
            // Fill the per-op holder so the cancel-branch terminal event keeps the old wire message + counts.
            result.Message = "Import was cancelled";
            result.RecordsImported = recordsImported;
            result.RecordsSkipped = recordsSkipped;
            result.RecordsErrors = recordsErrors;
            result.TotalRecords = totalRecords;
            _operationTracker.CompleteOperation(operationId, false, "Cancelled by user");

            return Ok(new MigrationImportResponse
            {
                Message = $"Import cancelled: {recordsImported} imported before cancellation",
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
            // Fill the per-op holder so the fail-branch terminal event keeps "Import failed: ..." + counts.
            result.Message = $"Import failed: {ex.Message}";
            result.RecordsImported = recordsImported;
            result.RecordsSkipped = recordsSkipped;
            result.RecordsErrors = recordsErrors;
            result.TotalRecords = totalRecords;
            _operationTracker.CompleteOperation(operationId, false, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// GET /api/migration/import/status - Get current data import status
    /// Returns whether an import is running, progress percentage, status message, and operation ID
    /// </summary>
    [HttpGet("import/status")]
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
    /// GET /api/migration/validate-connection - Test connection to a source LancacheManager PostgreSQL database
    /// Query params: connectionString (PostgreSQL connection string)
    /// </summary>
    [HttpGet("validate-connection")]
    public async Task<IActionResult> ValidateConnectionAsync([FromQuery] string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return BadRequest(new ErrorResponse { Error = "Connection string is required" });
        }

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            // Check for the Downloads table
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT table_name FROM information_schema.tables WHERE table_schema='public' AND LOWER(table_name)=LOWER(@tableName)";
            cmd.Parameters.AddWithValue("@tableName", "Downloads");
            var tableExists = await cmd.ExecuteScalarAsync();

            if (tableExists == null)
            {
                return Ok(new ConnectionValidationResponse
                {
                    Valid = false,
                    Message = "Connection successful, but Downloads table not found"
                });
            }

            var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM \"Downloads\"";
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

    private async Task<(Guid? OperationId, CancellationTokenSource? CancellationTokenSource, DataImportResultHolder? Result, OperationConflictResponse? Conflict)> StartImportAsync(
        string operationName,
        CancellationToken cancellationToken)
    {
        await _importStartLock.WaitAsync(cancellationToken);
        try
        {
            var conflict = await _conflictChecker.CheckAsync(
                OperationType.DataImport,
                ConflictScope.Bulk(),
                cancellationToken);
            if (conflict != null)
            {
                return (null, null, null, conflict);
            }

            var cts = new CancellationTokenSource();
            var result = new DataImportResultHolder();
            // Single terminal emit for the whole op: every CompleteOperation (success, cancel,
            // exception, AND the early validation fast-fails) funnels here exactly once. The owning
            // method fills `result` before completing; cancel/error/validation fall back to info.Error.
            Guid registeredId = Guid.Empty;
            var operationId = _operationTracker.RegisterOperation(
                OperationType.DataImport,
                operationName,
                cts,
                onTerminalEmit: info => info.Cancelled
                    ? _notifications.NotifyAllAsync(SignalREvents.DataImportComplete,
                        new DataImportComplete(
                            OperationId: registeredId,
                            Success: false,
                            Message: result.Message ?? "Import was cancelled by user",
                            Cancelled: true,
                            RecordsImported: result.RecordsImported,
                            RecordsSkipped: result.RecordsSkipped,
                            RecordsErrors: result.RecordsErrors,
                            TotalRecords: result.TotalRecords))
                    : info.Success
                        ? _notifications.NotifyAllAsync(SignalREvents.DataImportComplete,
                            new DataImportComplete(
                                OperationId: registeredId,
                                Success: true,
                                Message: result.Message ?? "Import completed",
                                RecordsImported: result.RecordsImported,
                                RecordsSkipped: result.RecordsSkipped,
                                RecordsErrors: result.RecordsErrors,
                                TotalRecords: result.TotalRecords))
                        : _notifications.NotifyAllAsync(SignalREvents.DataImportComplete,
                            new DataImportComplete(
                                OperationId: registeredId,
                                Success: false,
                                Message: result.Message ?? info.Error ?? "Data import failed",
                                RecordsImported: result.RecordsImported,
                                RecordsSkipped: result.RecordsSkipped,
                                RecordsErrors: result.RecordsErrors,
                                TotalRecords: result.TotalRecords)));
            registeredId = operationId;
            return (operationId, cts, result, null);
        }
        finally
        {
            _importStartLock.Release();
        }
    }

}
