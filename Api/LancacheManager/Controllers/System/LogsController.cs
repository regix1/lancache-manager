using System.Runtime.ExceptionServices;
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
[Authorize(Policy = "AccountHolder")]
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
    private readonly OperationStateService _operationStateService;
    private readonly CacheManagementService _cacheManagementService;

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
        IOperationQueue operationQueue,
        OperationStateService operationStateService,
        CacheManagementService cacheManagementService)
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
        _operationStateService = operationStateService;
        _cacheManagementService = cacheManagementService;
    }

    /// <summary>
    /// Returns the resolved logs directory path and whether it currently exists on disk.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(LogInfoResponse), StatusCodes.Status200OK)]
    public ActionResult<LogInfoResponse> GetLogInfo()
    {
        var logsPath = _pathResolver.GetLogsDirectory();
        return Ok(new LogInfoResponse
        {
            Path = logsPath,
            Exists = Directory.Exists(logsPath)
        });
    }

    /// <summary>
    /// Gets log entry counts by service, aggregated from all datasources.
    /// </summary>
    [HttpGet("service-counts")]
    [ProducesResponseType(typeof(Dictionary<string, ulong>), StatusCodes.Status200OK)]
    public async Task<ActionResult<Dictionary<string, ulong>>> GetServiceCountsAsync()
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
    /// Gets log entry counts by service, grouped by datasource.
    /// </summary>
    [HttpGet("service-counts/by-datasource")]
    [ProducesResponseType(typeof(List<DatasourceServiceCountsResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<DatasourceServiceCountsResponse>>> GetServiceCountsByDatasourceAsync()
    {
        var datasources = _datasourceService.GetDatasources();
        var result = new List<DatasourceServiceCountsResponse>();

        foreach (var ds in datasources)
        {
            var counts = await GetServiceCountsForDatasourceAsync(ds);
            result.Add(new DatasourceServiceCountsResponse
            {
                Datasource = ds.Name,
                LogsPath = ds.LogPath,
                LogsWritable = _pathResolver.IsDirectoryWritable(ds.LogPath),
                Enabled = ds.Enabled,
                ServiceCounts = counts ?? new Dictionary<string, ulong>()
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
    /// Updates the log position, resetting to the beginning or end.
    /// </summary>
    /// <remarks>
    /// PATCH is the proper method for partial updates. Request body: { "position": 0 } to reset
    /// to beginning, { "position": null } to reset to end.
    /// </remarks>
    [HttpPatch("position")]
    [ProducesResponseType(typeof(LogPositionResponse), StatusCodes.Status200OK)]
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
    /// Gets log positions for all datasources.
    /// </summary>
    /// <remarks>
    /// Returns, per datasource, the current read position, total line count, and the ingest
    /// diagnostic counters (unparsed lines, encoding errors, missing sources) from the last run.
    /// </remarks>
    [HttpGet("positions")]
    [ProducesResponseType(typeof(List<DatasourceLogPositionResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLogPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        var datasources = _datasourceService.GetDatasources();
        var positions = new List<DatasourceLogPositionResponse>();

        foreach (var ds in datasources)
        {
            var position = _stateRepository.GetLogPosition(ds.Name);

            // Use saved totalLines from Rust processor (avoids recounting). On the first run,
            // use the same focused Rust line-count command as reset-to-end; failures propagate
            // instead of masquerading as a required zero value.
            var totalLines = _stateRepository.GetLogTotalLines(ds.Name);
            if (totalLines == 0 && position == 0)
            {
                var countResult = await _rustProcessHelper.CountLogLinesAsync(
                    ds.LogPath,
                    cancellationToken);
                totalLines = countResult.LinesProcessed;
            }

            ds.RefreshLogSources();
            var diagnostics = _stateRepository.GetLogIngestDiagnostics(ds.Name);
            var sourcePositions = _stateRepository.GetLogSourcePositions(ds.Name);

            positions.Add(new DatasourceLogPositionResponse
            {
                Datasource = ds.Name,
                Position = position,
                TotalLines = totalLines,
                LogPath = ds.LogPath,
                Enabled = ds.Enabled,
                Layout = ds.Layout,
                SourceCount = ds.LogSourceStems.Count,
                SourcePositions = sourcePositions,
                UnparsedLines = diagnostics?.UnparsedLines ?? 0,
                HintlessHttpDetailedLines = diagnostics?.HintlessHttpDetailedLines ?? 0,
                InvalidEncodingLines = diagnostics?.InvalidEncodingLines ?? 0,
                SkippedFallbackLines = diagnostics?.SkippedFallbackLines ?? 0,
                IncompleteFinalRecords = diagnostics?.IncompleteFinalRecords ?? 0,
                FilesWithErrors = diagnostics?.FilesWithErrors ?? new List<string>(),
                LastRunTerminalStatus = diagnostics?.TerminalStatus ?? string.Empty,
                MissingSourcesMessage = diagnostics?.MissingSourcesMessage
            });
        }

        return Ok(positions);
    }

    /// <summary>
    /// Resets the log position for a specific datasource.
    /// </summary>
    /// <remarks>
    /// Same beginning/end semantics as the all-datasources variant, scoped to one datasource.
    /// Returns 404 if the datasource name is not configured.
    /// </remarks>
    [HttpPatch("position/{datasourceName}")]
    [ProducesResponseType(typeof(LogPositionResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetDatasourceLogPositionAsync(
        string datasourceName,
        [FromBody] UpdateLogPositionRequest? request,
        CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(ApiResponse.NotFound($"Datasource '{datasourceName}'"));
        }

        datasourceName = datasource.Name;

        return await ResetPositionCoreAsync(
            datasourceName,
            request?.Position,
            cancellationToken);
    }

    /// <summary>
    /// Starts processing logs from the current position for all datasources.
    /// </summary>
    /// <remarks>
    /// POST is acceptable here since this starts an asynchronous operation. Uses the position
    /// set by the PUT /api/logs/position endpoint (top or bottom).
    /// </remarks>
    [HttpPost("process")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status202Accepted)]
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
                return StatusCode(500, new ErrorResponse
                {
                    Error = "Failed to start log processing",
                    StageKey = "errors.logs.startFailed"
                });
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
    /// Starts processing logs for a specific datasource.
    /// </summary>
    /// <remarks>
    /// Uses the position saved for this datasource. Returns 404 if the datasource name is not
    /// configured; conflicting requests are parked in the wait queue rather than rejected.
    /// </remarks>
    [HttpPost("process/{datasourceName}")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(OperationResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ProcessDatasourceLogsAsync(string datasourceName, CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(ApiResponse.NotFound($"Datasource '{datasourceName}'"));
        }

        datasourceName = datasource.Name;

        await _logProcessingStartLock.WaitAsync(cancellationToken);
        try
        {
            // Wait-queue model: conflicting requests are parked (visible waiting card), never 409'd.
            async Task<Guid?> StartDatasourceProcessingAsync()
            {
                return await _rustLogProcessorService.StartInBackgroundAsync(
                    datasource.LogPath,
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
                return StatusCode(500, new ErrorResponse
                {
                    Error = $"Failed to start log processing for '{datasourceName}'",
                    StageKey = "errors.logs.startFailedForDatasource",
                    Context = new Dictionary<string, object?> { ["datasource"] = datasourceName }
                });
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
    /// Gets the log processing status.
    /// </summary>
    /// <remarks>
    /// A snapshot of the current or most recent run's progress, for clients that poll instead
    /// of relying on the SignalR processing-progress events.
    /// </remarks>
    [HttpGet("process/status")]
    [ProducesResponseType(typeof(LogProcessingStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<LogProcessingStatusResponse> GetProcessingStatus()
    {
        var status = _rustLogProcessorService.GetStatus();
        return Ok(status);
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

        // A pass waiting at the log lock during a step already holds IsProcessing, so wait for
        // the step first; the refusal below then answers only for a pass that is really reading.
        await _operationStateService.WaitForLogStepAsync(active: false, cancellationToken);

        // A reset while a processor is running would be silently undone when that run's
        // terminal checkpoint persists its snapshotted positions; make the user stop (or
        // wait out) processing first instead of returning a success that does not stick.
        if (_rustLogProcessorService.IsProcessing)
        {
            var refusal = ApiResponse.Error(
                "Log processing is currently running. Stop it or let it finish, then reset the position.");
            refusal.StageKey = "errors.logs.processingActive";
            return Conflict(refusal);
        }

        // Position == 0 -> reset to beginning. This remains state-only and deliberately does not
        // launch the Rust line counter.
        if (requestedPosition == 0)
        {
            // Lowering stored positions must not interleave with an import or another job's step.
            await using (await _operationStateService.LockLogFilesAsync(
                null,
                OperationType.LogProcessing,
                LogFileLockKind.Rows,
                cancellationToken))
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
            }

            return Ok(new LogPositionResponse
            {
                Message = isSingleDatasource
                    ? $"Log position reset to beginning for '{datasourceName}'"
                    : "Log position reset to beginning",
                Position = 0
            });
        }

        var conflict = await _conflictChecker.CheckAsync(
            OperationType.LogProcessing,
            ConflictScope.Bulk(),
            cancellationToken);
        if (conflict != null)
        {
            // Only another log processing run conflicts here, and this request is not queued.
            var busy = ApiResponse.Error(
                "Log processing is currently running. Stop it or let it finish, then reset the position.");
            busy.StageKey = "errors.logs.processingActive";
            return Conflict(busy);
        }

        var reservation = await _rustLogProcessorService.TryReserveProcessingGateAsync();
        if (reservation is null)
        {
            var reserved = ApiResponse.Error(
                "Log processing is currently running. Stop it or let it finish, then reset the position.");
            reserved.StageKey = "errors.logs.processingActive";
            return Conflict(reserved);
        }

        try
        {
            // Inside the try, so a request abandoned while it waits still frees the reservation.
            // The reservation keeps the live import out; the lock keeps another job's position
            // reduction from landing between this count and its write.
            await using var logLock = await _operationStateService.LockLogFilesAsync(
                null,
                OperationType.LogProcessing,
                LogFileLockKind.Rows,
                cancellationToken);

            IEnumerable<ResolvedDatasource> datasources = isSingleDatasource
                ? new[] { _datasourceService.GetDatasource(datasourceName!)! }
                : _datasourceService.GetDatasources();

            long totalLines = 0;
            foreach (var ds in datasources)
            {
                // Counting can take seconds on large or compressed logs. The reservation prevents a
                // live pass from ingesting the lines being skipped and a purge from lowering positions
                // before this older count writes them.
                var countResult = await _rustProcessHelper.CountLogLinesAsync(
                    ds.LogPath,
                    cancellationToken);
                var lineCount = countResult.LinesProcessed;

                _rustLogProcessorService.ClearResume(ds.Name);
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
        finally
        {
            _rustLogProcessorService.ReleaseProcessingGate(reservation.Value);
        }
    }

    /// <summary>
    /// Removes logs for a specific service from a specific datasource.
    /// </summary>
    /// <remarks>
    /// Deletes the matching log entries for the service via the Rust removal worker.
    /// Conflicting requests for the same service are parked in the wait queue rather than
    /// rejected.
    /// </remarks>
    [HttpDelete("datasources/{datasourceName}/services/{service}")]
    [ProducesResponseType(typeof(QueuedOperationResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(LogRemovalStartResponse), StatusCodes.Status202Accepted)]
    public async Task<IActionResult> RemoveServiceLogsFromDatasourceAsync(string datasourceName, string service, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return BadRequest(ApiResponse.Invalid("Service name is required"));
        }

        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(ApiResponse.NotFound($"Datasource '{datasourceName}'"));
        }

        datasourceName = datasource.Name;

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

            return StatusCode(500, new ErrorResponse
            {
                Error = $"Failed to remove logs for service '{service}' from datasource '{datasourceName}'",
                StageKey = "errors.logs.removeFailed",
                Context = new Dictionary<string, object?>
                {
                    ["service"] = service,
                    ["datasource"] = datasourceName
                }
            });
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
    /// Deletes the entire access.log file for a datasource.
    /// </summary>
    /// <remarks>
    /// Deletes the datasource's access.log, or every per-service log series with its rotations,
    /// and keeps the read position of any older log file left on disk.
    /// </remarks>
    [HttpDelete("datasources/{datasourceName}/file")]
    [ProducesResponseType(typeof(MessageResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteLogFileAsync(
        string datasourceName,
        CancellationToken cancellationToken = default)
    {
        var datasource = _datasourceService.GetDatasource(datasourceName);
        if (datasource == null)
        {
            return NotFound(ApiResponse.NotFound($"Datasource '{datasourceName}'"));
        }

        datasourceName = datasource.Name;

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
            return NotFound(new NotFoundResponse
            {
                Error = $"Log file not found: {accessLogPath}",
                StageKey = "errors.logs.fileNotFound",
                Context = new Dictionary<string, object?> { ["path"] = accessLogPath }
            });
        }
        // Held through the position update, so no import reads the files this deletes and no other
        // step rewrites them; the reopen check is prepared after this, against the files as they are now.
        await using var logLock = await _operationStateService.LockLogFilesAsync(
            null,
            OperationType.LogProcessing,
            LogFileLockKind.Rewrite,
            cancellationToken);
        // Listed after the lock: a logrotate run during the wait renames and removes files.
        var affectedPaths = hasPerServiceSources
            ? NginxLogRotationService.GetAffectedLogPaths(datasource)
            : new[] { accessLogPath };
        await using var reopenCheck = await _nginxLogRotationService.PrepareReopenCheckAsync(
            new[] { datasource },
            affectedPaths,
            expectsPublication: false,
            cancellationToken);

        _nginxLogRotationService.ValidateReopenCheck(reopenCheck);
        var positions = _stateRepository.GetLogSourcePositions(datasourceName);
        if (positions.Count == 0 && _stateRepository.GetLogPosition(datasourceName) is > 0 and var legacyPosition)
        {
            // The importer reads a datasource that has only a legacy total as that many lines of access.log.
            positions[LogSourceLayout.MonolithicStem] = legacyPosition;
        }

        LogFileDeletionResult? deletion = null;
        ExceptionDispatchInfo? deleteFailure = null;
        try
        {
            // The delete cannot be undone, so once the logs are held it runs to the end.
            deletion = await _rustProcessHelper.DeleteLogFileAsync(deleteTarget, CancellationToken.None);
        }
        catch (Exception error)
        {
            deleteFailure = ExceptionDispatchInfo.Capture(error);
        }

        LogRotationResult reopen;
        try
        {
            // A position counts the lines of a series, oldest file first, that were imported or skipped
            // on purpose (the fresh-install seed, "Reset position to end"); skipped lines have no rows.
            // The delete removes each series newest file first, so a series whose current file is still
            // there lost nothing and keeps its position, and a series that lost it keeps the read lines
            // still in its older files. Counted before the reopen, which lets nginx create a new current
            // file. Written after the delete: a kill before the first unlink leaves every position right;
            // a kill between the first unlink and this write leaves the old position on a series that
            // lost its current file, so the next import skips that many lines, less its older files'
            // lines, of the new file.
            var emptiedStems = positions.Keys
                .Where(stem => !System.IO.File.Exists(Path.Combine(datasource.LogPath, stem)))
                .ToList();
            if (emptiedStems.Count > 0)
            {
                var remaining = await _rustProcessHelper.CountLogLinesAsync(
                    datasource.LogPath,
                    CancellationToken.None);
                foreach (var stem in emptiedStems)
                {
                    positions[stem] = Math.Min(positions[stem], remaining.SourceLineCounts.GetValueOrDefault(stem));
                }
                _stateRepository.SetLogSourcePositions(datasourceName, positions);
                _stateRepository.SetLogTotalLines(datasourceName, remaining.LinesProcessed);
            }
        }
        finally
        {
            if (deleteFailure != null)
            {
                await _nginxLogRotationService.InvalidateReopenCheckAsync(
                    reopenCheck,
                    CancellationToken.None);
            }
            // Files may already be gone, so an aborted request or a failed position write must still
            // reopen nginx.
            reopen = await _nginxLogRotationService.CompleteReopenCheckAsync(
                reopenCheck,
                physicalChange: true,
                CancellationToken.None);
            // Every open Log Removal panel and the cached counts still list the deleted files.
            await _cacheManagementService.InvalidateServiceCountsAsync();
        }

        if (deleteFailure != null)
        {
            if (!reopen.Success)
            {
                throw new AggregateException(
                    deleteFailure.SourceException,
                    new IOException(reopen.ErrorMessage!));
            }
            deleteFailure.Throw();
        }
        if (!reopen.Success)
        {
            throw new IOException(reopen.ErrorMessage!);
        }

        _logger.LogInformation(
            "Deleted log file(s) for datasource '{Datasource}': {Path} ({Size} bytes)",
            datasourceName,
            deleteTarget,
            deletion!.BytesDeleted);

        return Ok(new MessageResponse
        {
            Message = $"Log file deleted successfully for datasource '{datasourceName}'"
        });
    }

    /// <summary>
    /// Gets the status of a log removal operation.
    /// </summary>
    /// <remarks>
    /// A recovery endpoint for the active (or most recent) service log removal, used to rebuild
    /// the progress card on page reload instead of relying only on the SignalR events.
    /// </remarks>
    [HttpGet("remove/status")]
    [ProducesResponseType(typeof(LogRemovalStatusResponse), StatusCodes.Status200OK)]
    public ActionResult<LogRemovalStatusResponse> GetRemovalStatus()
    {
        var status = _rustLogRemovalService.GetRemovalStatus();
        return Ok(status);
    }

}
