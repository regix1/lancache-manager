using LancacheManager.Infrastructure.Services.Interfaces;
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

    public DataMigrationController(
        ILogger<DataMigrationController> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
    }

    /// <summary>
    /// POST /api/migration/import-develancache - Import data from DeveLanCacheUI_Backend database
    /// Request body: { "connectionString": "Data Source=path/to/develancache.db", "batchSize": 1000, "overwriteExisting": false }
    /// </summary>
    [HttpPost("import-develancache")]
    [RequireAuth]
    public async Task<IActionResult> ImportFromDeveLanCache([FromBody] ImportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
        {
            return BadRequest(new { error = "Connection string is required" });
        }

        try
        {
            _logger.LogInformation("Starting import from DeveLanCacheUI_Backend database");

            // Extract the database path - supports both raw paths and connection strings
            var sourceDatabasePath = ExtractDatabasePath(request.ConnectionString);
            if (string.IsNullOrWhiteSpace(sourceDatabasePath) || !System.IO.File.Exists(sourceDatabasePath))
            {
                return BadRequest(new { error = "Source database file not found at specified path" });
            }

            // Get target database path
            var targetDatabasePath = _pathResolver.GetDatabasePath();

            // Get data migrator binary path
            var dataMigratorPath = _pathResolver.GetRustDataMigratorPath();
            _rustProcessHelper.ValidateRustBinaryExists(dataMigratorPath, "data_migrator");

            // Create temporary progress file
            var progressPath = Path.GetTempFileName();

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
                    return StatusCode(500, new { error = "Failed to start data migrator process" });
                }

                // Monitor stdout and stderr
                var (stdoutTask, stderrTask) = _rustProcessHelper.CreateOutputMonitoringTasks(process, "data_migrator");

                // Wait for process to complete
                await process.WaitForExitAsync();

                // Wait for output tasks
                await _rustProcessHelper.WaitForOutputTasksAsync(stdoutTask, stderrTask, TimeSpan.FromSeconds(5));

                if (process.ExitCode != 0)
                {
                    return StatusCode(500, new
                    {
                        error = "Data migration failed",
                        details = $"Process exited with code {process.ExitCode}"
                    });
                }

                // Read final progress to get statistics
                var progress = await _rustProcessHelper.ReadProgressFileAsync<MigrationProgress>(progressPath);

                if (progress == null)
                {
                    _logger.LogWarning("Progress file not found after migration");
                    return Ok(new { message = "Import completed but progress data unavailable" });
                }

                _logger.LogInformation(
                    "Import completed: {Imported} imported, {Skipped} skipped, {Errors} errors. Backup: {BackupPath}",
                    progress.RecordsImported, progress.RecordsSkipped, progress.RecordsErrors, progress.BackupPath ?? "none");

                return Ok(new
                {
                    message = progress.Message,
                    totalRecords = progress.RecordsProcessed,
                    imported = progress.RecordsImported,
                    skipped = progress.RecordsSkipped,
                    errors = progress.RecordsErrors,
                    backupPath = progress.BackupPath
                });
            }
            finally
            {
                // Clean up progress file
                await _rustProcessHelper.DeleteTemporaryFileAsync(progressPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importing data from DeveLanCacheUI_Backend");
            return StatusCode(500, new { error = "Import failed", details = ex.Message });
        }
    }

    /// <summary>
    /// GET /api/migration/validate-connection - Test connection to DeveLanCacheUI database
    /// Query params: connectionString (supports raw path or "Data Source=..." format)
    /// </summary>
    [HttpGet("validate-connection")]
    [RequireAuth]
    public async Task<IActionResult> ValidateConnection([FromQuery] string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return BadRequest(new { error = "Database path is required" });
        }

        try
        {
            // Extract path and convert to connection string format for SqliteConnection
            var dbPath = ExtractDatabasePath(connectionString);
            var connStr = connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase)
                ? connectionString
                : $"Data Source={dbPath}";

            using var connection = new SqliteConnection(connStr);
            await connection.OpenAsync();

            // Check for DownloadEvents table
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='DownloadEvents'";
            var tableExists = await cmd.ExecuteScalarAsync();

            if (tableExists == null)
            {
                return Ok(new
                {
                    valid = false,
                    message = "Connection successful, but DownloadEvents table not found"
                });
            }

            // Get record count
            var countCmd = connection.CreateCommand();
            countCmd.CommandText = "SELECT COUNT(*) FROM DownloadEvents";
            var recordCount = Convert.ToInt32(await countCmd.ExecuteScalarAsync());

            return Ok(new
            {
                valid = true,
                message = "Connection successful",
                recordCount
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate connection");
            return Ok(new
            {
                valid = false,
                message = ex.Message
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

    /// <summary>
    /// Request model for importing data
    /// </summary>
    public class ImportRequest
    {
        public string ConnectionString { get; set; } = string.Empty;
        public int? BatchSize { get; set; } = 1000;
        public bool OverwriteExisting { get; set; } = false;
    }

    /// <summary>
    /// Progress data from Rust data_migrator binary
    /// </summary>
    public class MigrationProgress
    {
        [JsonPropertyName("is_processing")]
        public bool IsProcessing { get; set; }

        [JsonPropertyName("percent_complete")]
        public double PercentComplete { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("records_processed")]
        public ulong RecordsProcessed { get; set; }

        [JsonPropertyName("records_imported")]
        public ulong RecordsImported { get; set; }

        [JsonPropertyName("records_skipped")]
        public ulong RecordsSkipped { get; set; }

        [JsonPropertyName("records_errors")]
        public ulong RecordsErrors { get; set; }

        [JsonPropertyName("backup_path")]
        public string? BackupPath { get; set; }

        [JsonPropertyName("timestamp")]
        public string Timestamp { get; set; } = string.Empty;
    }
}
