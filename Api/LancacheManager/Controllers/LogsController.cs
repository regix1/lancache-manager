using System.Text.Json;
using LancacheManager.Models;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LancacheManager.Controllers;

/// <summary>
/// RESTful controller for log processing and management
/// Handles log position updates, processing operations, and service log removal
/// </summary>
[ApiController]
[Route("api/logs")]
[Authorize(Policy = "AdminOnly")]
public class LogsController : ControllerBase
{
    private static readonly SemaphoreSlim _logProcessingStartLock = new(1, 1);

    private readonly RustLogProcessorService _rustLogProcessorService;
    private readonly RustLogRemovalService _rustLogRemovalService;
    private readonly ILogger<LogsController> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly DatasourceService _datasourceService;
    private readonly StateService _stateRepository;
    private readonly NginxLogRotationService _nginxLogRotationService;
    private readonly IOperationConflictChecker _conflictChecker;
    private readonly IOperationQueue _operationQueue;

    public LogsController(
        RustLogProcessorService rustLogProcessorService,
        RustLogRemovalService rustLogRemovalService,
        ILogger<LogsController> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        DatasourceService datasourceService,
        StateService stateRepository,
        NginxLogRotationService nginxLogRotationService,
        IOperationConflictChecker conflictChecker,
        IOperationQueue operationQueue)
    {
        _rustLogProcessorService = rustLogProcessorService;
        _rustLogRemovalService = rustLogRemovalService;
        _logger = logger;
        _pathResolver = pathResolver;
        _rustProcessHelper = rustProcessHelper;
        _datasourceService = datasourceService;
        _stateRepository = stateRepository;
        _nginxLogRotationService = nginxLogRotationService;
        _conflictChecker = conflictChecker;
        _operationQueue = operationQueue;
    }

    /// <summary>
    /// GET /api/logs - Get log information
    /// </summary>
    [HttpGet]
    public IActionResult GetLogInfo()
    {
        var logsPath = _pathResolver.GetLogsDirectory();
        return Ok(new LogInfoResponse
        {
            Path = logsPath,
            Exists = Directory.Exists(logsPath)
        });
    }

    /// <summary>
    /// GET /api/logs/service-counts - Get log entry counts by service (aggregated from all datasources)
    /// </summary>
    [HttpGet("service-counts")]
    public async Task<IActionResult> GetServiceCountsAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        var aggregatedCounts = new Dictionary<string, ulong>();

        foreach (var ds in datasources)
        {
            var counts = await GetServiceCountsForDatasourceAsync(ds);
            if (counts == null) continue;

            foreach (var kvp in counts)
            {
                if (aggregatedCounts.ContainsKey(kvp.Key))
                    aggregatedCounts[kvp.Key] += kvp.Value;
                else
                    aggregatedCounts[kvp.Key] = kvp.Value;
            }
        }

        return Ok(aggregatedCounts);
    }

    /// <summary>
    /// GET /api/logs/service-counts/by-datasource - Get log entry counts by service, grouped by datasource
    /// </summary>
    [HttpGet("service-counts/by-datasource")]
    public async Task<IActionResult> GetServiceCountsByDatasourceAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        var result = new List<object>();

        foreach (var ds in datasources)
        {
            var counts = await GetServiceCountsForDatasourceAsync(ds);
            result.Add(new
            {
                datasource = ds.Name,
                logsPath = ds.LogPath,
                logsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath),
                enabled = ds.Enabled,
                serviceCounts = counts ?? new Dictionary<string, ulong>()
            });
        }

        return Ok(result);
    }

    /// <summary>
    /// Runs the log-manager count command for a single datasource and returns the deserialized
    /// service_counts dictionary, or null if the directory is missing or the command fails.
    /// </summary>
    private async Task<Dictionary<string, ulong>?> GetServiceCountsForDatasourceAsync(ResolvedDatasource datasource)
    {
        if (!Directory.Exists(datasource.LogPath))
        {
            _logger.LogWarning("Log directory not found for datasource '{Name}': {Path}", datasource.Name, datasource.LogPath);
            return null;
        }

        var result = await _rustProcessHelper.RunLogManagerAsync(
            "count",
            datasource.LogPath,
            progressFile: null
        );

        if (!result.Success)
        {
            _logger.LogWarning("Failed to count logs for datasource '{Name}': {Error}", datasource.Name, result.Error);
            return null;
        }

        if (result.Data is JsonElement jsonElement &&
            jsonElement.TryGetProperty("service_counts", out var serviceCountsElement))
        {
            return JsonSerializer.Deserialize<Dictionary<string, ulong>>(serviceCountsElement.GetRawText());
        }

        return null;
    }

    /// <summary>
    /// GET /api/logs/entries-count - Get total count of log entries in database
    /// </summary>
    [HttpGet("entries-count")]
    public IActionResult GetEntriesCount()
    {
        // Delegate to DatabaseController's endpoint for consistency
        return Ok(new MessageResponse { Message = "Use GET /api/database/log-entries-count instead" });
    }

    /// <summary>
    /// PATCH /api/logs/position - Update log position (reset to beginning or end)
    /// RESTful: PATCH is proper method for partial updates
    /// Request body: { "position": 0 } to reset to beginning, { "position": null } to reset to end
    /// </summary>
    [HttpPatch("position")]
    public async Task<IActionResult> ResetLogPositionAsync(
        [FromBody] UpdateLogPositionRequest? request,
        CancellationToken cancellationToken = default)
    {
        return await ResetPositionCoreAsync(
            datasourceName: null,
            requestedPosition: request?.Position,
            cancellationToken);
    }

    /// <summary>
    /// GET /api/logs/positions - Get log positions for all datasources
    /// </summary>
    [HttpGet("positions")]
    public async Task<IActionResult> GetLogPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var datasources = _datasourceService.GetDatasources();
        var positions = new List<object>();

        foreach (var ds in datasources)
        {
            var position = _stateRepository.GetLogPosition(ds.Name);

            // Use saved totalLines from Rust processor (avoids recounting). On the first run,
            // use the same focused Rust line-count command as reset-to-end; failures propagate
            // instead of masquerading as a required zero value.
            var totalLines = _stateRepository.GetLogTotalLines(ds.Name);
            // True when the first-run count above stopped at an unreadable source member, so the
            // reported total is only a clean prefix. This is distinct from the ingestion-side
            // filesWithErrors list below (which reflects the last processing run, not this count).
            var totalLinesPartial = false;
            if (totalLines == 0 && position == 0)
            {
                var countResult = await _rustProcessHelper.CountLogLinesAsync(
                    ds.LogPath,
                    cancellationToken);
                totalLines = countResult.LinesProcessed;
                totalLinesPartial = countResult.FilesWithErrors > 0;
            }

            ds.RefreshLogSources();
            var diagnostics = _stateRepository.GetLogIngestDiagnostics(ds.Name);
            var sourcePositions = _stateRepository.GetLogSourcePositions(ds.Name);

            positions.Add(new
            {
                datasource = ds.Name,
                position,
                totalLines,
                totalLinesPartial,
                logPath = ds.LogPath,
                enabled = ds.Enabled,
                layout = ds.Layout,
                sourceCount = ds.LogSourceStems.Count,
                sourcePositions,
                unparsedLines = diagnostics?.UnparsedLines ?? 0,
                hintlessHttpDetailedLines = diagnostics?.HintlessHttpDetailedLines ?? 0,
                invalidEncodingLines = diagnostics?.InvalidEncodingLines ?? 0,
                skippedFallbackLines = diagnostics?.SkippedFallbackLines ?? 0,
                incompleteFinalRecords = diagnostics?.IncompleteFinalRecords ?? 0,
                filesWithErrors = diagnostics?.FilesWithErrors ?? new List<string>(),
                lastRunTerminalStatus = diagnostics?.TerminalStatus ?? string.Empty,
                missingSourcesMessage = diagnostics?.MissingSourcesMessage
            });
        }

        return Ok(positions);
    }

    /// <summary>
    /// PATCH /api/logs/position/{datasourceName} - Reset position for a specific datasource
    /// </summary>
    [HttpPatch("position/{datasourceName}")]
    public async Task<IActionResult> ResetDatasourceLogPositionAsync(
        string datasourceName,
        [FromBody] UpdateLogPositionRequest? request,
        CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        return await ResetPositionCoreAsync(
            datasourceName,
            request?.Position,
            cancellationToken);
    }

    /// <summary>
    /// POST /api/logs/process - Start processing logs from current position (all datasources)
    /// Note: POST is acceptable here as this starts an asynchronous operation
    /// Uses the position set by PUT /api/logs/position endpoint (top or bottom)
    /// </summary>
    [HttpPost("process")]
    public async Task<IActionResult> ProcessAllLogsAsync(CancellationToken cancellationToken = default)
    {
        await _logProcessingStartLock.WaitAsync(cancellationToken);
        try
        {
            // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
            Task<Guid?> StartAllProcessingAsync() => _rustLogProcessorService.StartAllInBackgroundAsync();

            var conflict = await _conflictChecker.CheckAsync(
                OperationType.LogProcessing,
                ConflictScope.Bulk(),
                cancellationToken);
            if (conflict != null)
            {
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.LogProcessing, ConflictScope.Bulk(), "Log Processing",
                    StartAllProcessingAsync, cancellationToken));
            }

            var operationId = await StartAllProcessingAsync();
            if (!operationId.HasValue)
            {
                var raceConflict = await _conflictChecker.CheckAsync(
                    OperationType.LogProcessing,
                    ConflictScope.Bulk(),
                    cancellationToken);
                if (raceConflict != null)
                {
                    // Race: processing began between our check and the start - park it.
                    return Accepted(await _operationQueue.EnqueueAsync(
                        OperationType.LogProcessing, ConflictScope.Bulk(), "Log Processing",
                        StartAllProcessingAsync, cancellationToken));
                }

                _logger.LogWarning("Failed to start log processing for all datasources");
                return StatusCode(500, new ErrorResponse { Error = "Failed to start log processing" });
            }

            _logger.LogInformation("Started log processing for all datasources (Operation: {OperationId})", operationId.Value);

            return Accepted(new OperationResponse
            {
                OperationId = operationId.Value,
                Message = "Log processing started for all datasources",
                Status = OperationStatus.Running
            });
        }
        finally
        {
            _logProcessingStartLock.Release();
        }
    }

    /// <summary>
    /// POST /api/logs/process/{datasourceName} - Start processing logs for a specific datasource
    /// </summary>
    [HttpPost("process/{datasourceName}")]
    public async Task<IActionResult> ProcessDatasourceLogsAsync(string datasourceName, CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        await _logProcessingStartLock.WaitAsync(cancellationToken);
        try
        {
            // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
            async Task<Guid?> StartDatasourceProcessingAsync()
            {
                var position = _stateRepository.GetLogPosition(datasourceName);
                return await _rustLogProcessorService.StartInBackgroundAsync(
                    datasource.LogPath,
                    position,
                    silentMode: false,
                    datasourceName: datasourceName);
            }

            var conflict = await _conflictChecker.CheckAsync(
                OperationType.LogProcessing,
                ConflictScope.Bulk(),
                cancellationToken);
            if (conflict != null)
            {
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.LogProcessing, ConflictScope.Bulk(), $"Log Processing ({datasourceName})",
                    StartDatasourceProcessingAsync, cancellationToken));
            }

            var operationId = await StartDatasourceProcessingAsync();
            if (!operationId.HasValue)
            {
                var raceConflict = await _conflictChecker.CheckAsync(
                    OperationType.LogProcessing,
                    ConflictScope.Bulk(),
                    cancellationToken);
                if (raceConflict != null)
                {
                    // Race: processing began between our check and the start - park it.
                    return Accepted(await _operationQueue.EnqueueAsync(
                        OperationType.LogProcessing, ConflictScope.Bulk(), $"Log Processing ({datasourceName})",
                        StartDatasourceProcessingAsync, cancellationToken));
                }

                _logger.LogWarning("Failed to start log processing for datasource '{Name}'", datasourceName);
                return StatusCode(500, new ErrorResponse { Error = $"Failed to start log processing for '{datasourceName}'" });
            }

            _logger.LogInformation("Started log processing for datasource '{Name}' (Operation: {OperationId})", datasourceName, operationId.Value);
            return Accepted(new OperationResponse
            {
                OperationId = operationId.Value,
                Message = $"Log processing started for '{datasourceName}'",
                Status = OperationStatus.Running
            });
        }
        finally
        {
            _logProcessingStartLock.Release();
        }
    }

    /// <summary>
    /// GET /api/logs/process/status - Get log processing status
    /// </summary>
    [HttpGet("process/status")]
    public IActionResult GetProcessingStatus()
    {
        var status = _rustLogProcessorService.GetStatus();
        return Ok(status);
    }

    /// <summary>
    /// POST /api/logs/process/kill - Force kill log processing operation
    /// </summary>
    [HttpPost("process/kill")]
    public async Task<IActionResult> ForceKillAsync()
    {
        var killed = await _rustLogProcessorService.ForceKillProcessingAsync();
        if (!killed)
        {
            return NotFound(new NotFoundResponse { Error = "No log processing operation to kill" });
        }
        return Ok(new { message = "Log processing was force killed" });
    }

    /// <summary>
    /// Shared position-reset logic for both the all-datasources and single-datasource PATCH endpoints.
    /// When <paramref name="datasourceName"/> is null, operates across all datasources.
    /// If <paramref name="requestedPosition"/> is 0, resets to beginning; otherwise resets to end of file.
    /// </summary>
    private async Task<IActionResult> ResetPositionCoreAsync(
        string? datasourceName,
        long? requestedPosition,
        CancellationToken cancellationToken)
    {
        var isSingleDatasource = datasourceName != null;

        // A reset while a processor is running would be silently undone when that run's
        // terminal checkpoint persists its snapshotted positions; make the user stop (or
        // wait out) processing first instead of returning a success that does not stick.
        if (_rustLogProcessorService.IsProcessing)
        {
            return Conflict(new ErrorResponse
            {
                Error = "Log processing is currently running. Stop it or let it finish, then reset the position."
            });
        }

        // Position == 0 -> reset to beginning. This remains state-only and deliberately does not
        // launch the Rust line counter.
        if (requestedPosition == 0)
        {
            if (isSingleDatasource)
            {
                _rustLogProcessorService.ResetLogPosition(datasourceName!);
                _logger.LogInformation("Datasource '{Name}': Log position reset to beginning", datasourceName);
            }
            else
            {
                _rustLogProcessorService.ResetLogPosition();
                _logger.LogInformation("Log position reset to beginning for all datasources");
            }

            return Ok(new LogPositionResponse
            {
                Message = isSingleDatasource
                    ? $"Log position reset to beginning for '{datasourceName}'"
                    : "Log position reset to beginning",
                Position = 0
            });
        }

        IEnumerable<ResolvedDatasource> datasources = isSingleDatasource
            ? new[] { _datasourceService.GetDatasource(datasourceName!)! }
            : _datasourceService.GetDatasources();

        long totalLines = 0;
        foreach (var ds in datasources)
        {
            // Persist only after this datasource's Rust process has completed successfully. A
            // failure or cancellation therefore cannot overwrite its state with a fallback zero.
            // The count inventories EVERY source series (access.log AND per-service files) and
            // reports complete-record counts per stem, which seed the per-stem checkpoints.
            var countResult = await _rustProcessHelper.CountLogLinesAsync(
                ds.LogPath,
                cancellationToken);
            var lineCount = countResult.LinesProcessed;

            _stateRepository.SetLogSourcePositions(ds.Name, countResult.SourceLineCounts);
            _stateRepository.SetLogPosition(ds.Name, lineCount);
            _stateRepository.SetLogTotalLines(ds.Name, lineCount);
            totalLines += lineCount;

            if (lineCount > 0)
                _logger.LogInformation("Datasource '{Name}': Log position set to end (line {LineCount})", ds.Name, lineCount);
            else
                _logger.LogInformation("Datasource '{Name}': No log files found, position set to 0", ds.Name);
        }

        if (!isSingleDatasource)
            _logger.LogInformation("Log position reset to end for all datasources (total lines: {TotalLines})", totalLines);

        return Ok(new LogPositionResponse
        {
            Message = isSingleDatasource
                ? $"Log position reset to end of file for '{datasourceName}'"
                : "Log position reset to end of file",
            Position = totalLines
        });
    }

    /// <summary>
    /// Checks that at least one logs directory across all datasources is writable.
    /// Returns a BadRequest IActionResult with a PUID/PGID error message if none are writable, or null if at least one is writable.
    /// Also emits a warning if some (but not all) datasources are read-only.
    /// </summary>
    private BadRequestObjectResult? EnsureWritable(string operationDescription)
    {
        var datasources = _datasourceService.GetDatasources();
        var writableDatasources = datasources
            .Where(ds => _pathResolver.IsDirectoryWritable(ds.LogPath))
            .ToList();

        if (writableDatasources.Count == 0)
        {
            var errorMessage = $"Cannot {operationDescription}: all logs directories are read-only. " +
                "This is typically caused by incorrect PUID/PGID settings in your docker-compose.yml. " +
                $"The lancache container is configured to run as UID/GID {ContainerEnvironment.UidGid} (configured via PUID/PGID environment variables).";

            _logger.LogWarning("[{Operation}] Permission check failed: {Error}", operationDescription, errorMessage);
            return BadRequest(new ErrorResponse { Error = errorMessage });
        }

        var readOnlyCount = datasources.Count - writableDatasources.Count;
        if (readOnlyCount > 0)
        {
            _logger.LogWarning(
                "[{Operation}] {ReadOnlyCount} of {TotalCount} datasources are read-only and will be skipped",
                operationDescription, readOnlyCount, datasources.Count);
        }

        return null;
    }

    /// <summary>
    /// DELETE /api/logs/services/{service} - Remove logs for specific service (from all datasources)
    /// RESTful: DELETE is proper method for removing resources, service name in path
    /// </summary>
    [HttpDelete("services/{service}")]
    public async Task<IActionResult> RemoveServiceLogsAsync(string service, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(ApiResponse.Invalid("Service name is required"));
        }

        // CRITICAL: Check write permissions BEFORE starting the operation
        // This removes logs from all datasources, so we need at least one writable datasource
        var permissionError = EnsureWritable($"remove service logs for '{service}'");
        if (permissionError != null)
            return permissionError;

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        Task<Guid?> StartLogRemovalAsync() => _rustLogRemovalService.StartRemovalInBackgroundAsync(service);

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service(service),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.LogRemoval, ConflictScope.Service(service), $"Log Removal ({service})",
                StartLogRemovalAsync, cancellationToken));
        }

        var operationId = await StartLogRemovalAsync();

        if (!operationId.HasValue)
        {
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.LogRemoval,
                ConflictScope.Service(service),
                cancellationToken);
            if (raceConflict != null)
            {
                // Race: removal began between our check and the start - park it.
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.LogRemoval, ConflictScope.Service(service), $"Log Removal ({service})",
                    StartLogRemovalAsync, cancellationToken));
            }

            return StatusCode(500, new ErrorResponse { Error = $"Failed to remove logs for service '{service}'" });
        }

        _logger.LogInformation("Started log removal for service: {Service} (Operation: {OperationId})", service, operationId.Value);

        return Accepted(new LogRemovalStartResponse
        {
            Message = $"Started log removal for service: {service}",
            Service = service,
            OperationId = operationId.Value,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// DELETE /api/logs/datasources/{datasourceName}/services/{service} - Remove logs for specific service from a specific datasource
    /// </summary>
    [HttpDelete("datasources/{datasourceName}/services/{service}")]
    public async Task<IActionResult> RemoveServiceLogsFromDatasourceAsync(string datasourceName, string service, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(ApiResponse.Invalid("Service name is required"));
        }

        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        if (!datasource.LogsWritable)
        {
            return BadRequest(ApiResponse.Invalid($"Logs directory is read-only for datasource '{datasourceName}'"));
        }

        // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
        Task<Guid?> StartDatasourceLogRemovalAsync() =>
            _rustLogRemovalService.StartRemovalForDatasourceInBackgroundAsync(service, datasourceName);

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.LogRemoval,
            ConflictScope.Service(service),
            cancellationToken);
        if (conflict != null)
        {
            return Accepted(await _operationQueue.EnqueueAsync(
                OperationType.LogRemoval, ConflictScope.Service(service),
                $"Log Removal ({service} @ {datasourceName})", StartDatasourceLogRemovalAsync, cancellationToken));
        }

        var operationId = await StartDatasourceLogRemovalAsync();

        if (!operationId.HasValue)
        {
            var raceConflict = await _conflictChecker.CheckAsync(
                OperationType.LogRemoval,
                ConflictScope.Service(service),
                cancellationToken);
            if (raceConflict != null)
            {
                // Race: removal began between our check and the start - park it.
                return Accepted(await _operationQueue.EnqueueAsync(
                    OperationType.LogRemoval, ConflictScope.Service(service),
                    $"Log Removal ({service} @ {datasourceName})", StartDatasourceLogRemovalAsync, cancellationToken));
            }

            return StatusCode(500, new ErrorResponse { Error = $"Failed to remove logs for service '{service}' from datasource '{datasourceName}'" });
        }

        _logger.LogInformation(
            "Started log removal for service: {Service} in datasource: {Datasource} (Operation: {OperationId})",
            service, datasourceName, operationId.Value);

        return Accepted(new LogRemovalStartResponse
        {
            Message = $"Started log removal for service: {service} from datasource: {datasourceName}",
            Service = service,
            OperationId = operationId.Value,
            Status = OperationStatus.Running
        });
    }

    /// <summary>
    /// DELETE /api/logs/datasources/{datasourceName}/file - Delete the entire access.log file for a datasource
    /// This is a destructive operation that removes all log history for the datasource
    /// </summary>
    [HttpDelete("datasources/{datasourceName}/file")]
    public async Task<IActionResult> DeleteLogFileAsync(
        string datasourceName,
        CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(new NotFoundResponse { Error = $"Datasource '{datasourceName}' not found" });
        }

        if (!datasource.LogsWritable)
        {
            return BadRequest(ApiResponse.Invalid($"Logs directory is read-only for datasource '{datasourceName}'"));
        }

        datasource.RefreshLogSources();

        // A monolithic-only datasource keeps the original single-file delete. When
        // per-service sources exist (bare-metal / mixed layouts), the whole source SET is
        // the log file: Rust deletes every source series (rotations included) and every
        // stem checkpoint clears below.
        var hasPerServiceSources = datasource.LogSourceStems.Any(LogSourceLayout.IsPerServiceStem);
        var accessLogPath = Path.Combine(datasource.LogPath, "access.log");
        var deleteTarget = hasPerServiceSources ? datasource.LogPath : accessLogPath;
        if (!hasPerServiceSources && !System.IO.File.Exists(accessLogPath))
        {
            return NotFound(new NotFoundResponse { Error = $"Log file not found: {accessLogPath}" });
        }

        // C# retains authorization, datasource/read-only validation, state, and nginx
        // orchestration. Rust performs only the validated leaf mutation.
        var deletion = await _rustProcessHelper.DeleteLogFileAsync(
            deleteTarget,
            cancellationToken);

        _stateRepository.SetLogSourcePositions(datasourceName, new Dictionary<string, long>());
        _stateRepository.SetLogPosition(datasourceName, 0);
        _stateRepository.SetLogTotalLines(datasourceName, 0);

        _logger.LogInformation(
            "Deleted log file(s) for datasource '{Datasource}': {Path} ({Size} bytes)",
            datasourceName,
            deleteTarget,
            deletion.BytesDeleted);

        // Reopening nginx remains best-effort after a successful deletion.
        var rotationResult = await _nginxLogRotationService.ReopenNginxLogsAsync();
        if (!rotationResult.Success)
        {
            _logger.LogWarning("Failed to signal nginx to reopen logs: {Error}", rotationResult.ErrorMessage);
        }

        return Ok(new MessageResponse
        {
            Message = $"Log file deleted successfully for datasource '{datasourceName}'"
        });
    }

    /// <summary>
    /// GET /api/logs/remove/status - Get status of log removal operation
    /// </summary>
    [HttpGet("remove/status")]
    public IActionResult GetRemovalStatus()
    {
        var status = _rustLogRemovalService.GetRemovalStatus();
        return Ok(status);
    }

}
