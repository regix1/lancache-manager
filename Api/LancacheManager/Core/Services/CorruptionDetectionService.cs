using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service for detecting corrupted cache chunks with caching and background processing.
/// Similar pattern to GameCacheDetectionService.
/// </summary>
public class CorruptionDetectionService
{
    private readonly ILogger<CorruptionDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly OperationStateService _operationStateService;
    private readonly IUnifiedOperationTracker _operationTracker;

    // Track active detection operations
    private readonly ConcurrentDictionary<string, DetectionOperation> _operations = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;

    private const string OperationStateKey = "corruptionDetection";

    public class DetectionOperation
    {
        public string OperationId { get; set; } = string.Empty;
        public string? TrackerOperationId { get; set; }
        public DateTime StartTime { get; set; }
        public string Status { get; set; } = "running";
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, long>? CorruptionCounts { get; set; }
        public DateTime? LastDetectionTime { get; set; }
    }

    public CorruptionDetectionService(
        ILogger<CorruptionDetectionService> logger,
        IPathResolver pathResolver,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        OperationStateService operationStateService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _operationStateService = operationStateService;
        _operationTracker = operationTracker;
    }

    /// <summary>
    /// Start a background corruption detection scan.
    /// Returns immediately with an operation ID.
    /// </summary>
    public async Task<string> StartDetectionAsync(CancellationToken cancellationToken = default)
    {
        await _startLock.WaitAsync(cancellationToken);
        try
        {
            // Check if there's already an active detection
            var activeOp = _operations.Values.FirstOrDefault(o => o.Status == "running");
            if (activeOp != null)
            {
                _logger.LogWarning("[CorruptionDetection] Detection already in progress: {OperationId}", activeOp.OperationId);
                return activeOp.OperationId;
            }

            var operationId = Guid.NewGuid().ToString("N")[..8];
            var operation = new DetectionOperation
            {
                OperationId = operationId,
                StartTime = DateTime.UtcNow,
                Status = "running",
                Message = "Starting corruption detection..."
            };

            _operations[operationId] = operation;

            // Create a new cancellation token source
            // Note: Don't cancel/dispose old one here - it may have been disposed by CompleteOperation
            _cancellationTokenSource = new CancellationTokenSource();

            // Register with unified operation tracker
            var trackerOperationId = _operationTracker.RegisterOperation(
                OperationType.CorruptionDetection,
                "Corruption Detection",
                _cancellationTokenSource);
            operation.TrackerOperationId = trackerOperationId;

            // Save operation state for recovery
            _operationStateService.SaveState($"{OperationStateKey}_{operationId}", new OperationState
            {
                Key = $"{OperationStateKey}_{operationId}",
                Type = "corruptionDetection",
                Status = "running",
                Message = "Starting corruption detection..."
            });

            // Send start notification via SignalR
            // Use trackerOperationId so frontend can cancel via /api/operations/{id}/cancel
            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionStarted, new
            {
                OperationId = trackerOperationId,
                Message = "Starting corruption detection scan..."
            });

            // Run detection in background with the cancellation token
            var token = _cancellationTokenSource.Token;
            _ = Task.Run(async () => await RunDetectionAsync(operationId, token), token);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Run the actual corruption detection scan.
    /// </summary>
    private async Task RunDetectionAsync(string operationId, CancellationToken cancellationToken)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            _logger.LogWarning("[CorruptionDetection] Operation not found: {OperationId}", operationId);
            return;
        }

        try
        {
            var aggregatedCounts = new Dictionary<string, long>();
            var datasources = _datasourceService.GetDatasources();
            var timezone = Environment.GetEnvironmentVariable("TZ") ?? "UTC";
            var rustBinaryPath = _pathResolver.GetRustCorruptionManagerPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Corruption manager");

            _logger.LogInformation("[CorruptionDetection] Starting detection for {Count} datasource(s)", datasources.Count);

            // Check for cancellation at start
            cancellationToken.ThrowIfCancellationRequested();

            // Process each datasource
            foreach (var datasource in datasources)
            {
                // Check for cancellation before each datasource
                cancellationToken.ThrowIfCancellationRequested();

                var dsCounts = await GetCorruptionSummaryForDatasource(
                    datasource.LogPath, datasource.CachePath, timezone, rustBinaryPath,
                    operationId, operation.TrackerOperationId ?? operationId, datasource.Name, cancellationToken);

                // Aggregate counts
                foreach (var kvp in dsCounts)
                {
                    if (aggregatedCounts.ContainsKey(kvp.Key))
                    {
                        aggregatedCounts[kvp.Key] += kvp.Value;
                    }
                    else
                    {
                        aggregatedCounts[kvp.Key] = kvp.Value;
                    }
                }
            }

            // Update operation
            operation.Status = OperationStatus.Completed;
            operation.Message = $"Detection complete. Found {aggregatedCounts.Count} services with corruption.";
            operation.CorruptionCounts = aggregatedCounts;
            operation.LastDetectionTime = DateTime.UtcNow;

            // Save results to database
            await SaveCorruptionToDatabaseAsync(aggregatedCounts);

            // Clear operation state
            _operationStateService.RemoveState($"{OperationStateKey}_{operationId}");

            // Complete unified operation tracker
            if (operation.TrackerOperationId != null)
            {
                _operationTracker.CompleteOperation(operation.TrackerOperationId, success: true);
            }

            // Send completion notification via SignalR
            // Use TrackerOperationId so frontend can cancel via /api/operations/{id}/cancel
            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionComplete, new
            {
                OperationId = operation.TrackerOperationId,
                Success = true,
                Status = OperationStatus.Completed,
                Message = operation.Message,
                Cancelled = false,
                totalServicesWithCorruption = aggregatedCounts.Count,
                totalCorruptedChunks = aggregatedCounts.Values.Sum()
            });

            _logger.LogInformation("[CorruptionDetection] Detection complete: {Services}",
                string.Join(", ", aggregatedCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CorruptionDetection] Operation {OperationId} was cancelled", operationId);
            operation.Status = "cancelled";
            operation.Message = "Detection cancelled by user";

            // Clear operation state
            _operationStateService.RemoveState($"{OperationStateKey}_{operationId}");

            // Complete unified operation tracker
            if (operation.TrackerOperationId != null)
            {
                _operationTracker.CompleteOperation(operation.TrackerOperationId, success: false, error: "Cancelled by user");
            }

            // Send cancellation notification via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionComplete, new
            {
                OperationId = operation.TrackerOperationId,
                Success = false,
                Status = OperationStatus.Cancelled,
                Message = "Detection cancelled by user",
                Cancelled = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CorruptionDetection] Detection failed for operation {OperationId}", operationId);

            operation.Status = "failed";
            operation.Message = ex.Message;

            // Clear operation state
            _operationStateService.RemoveState($"{OperationStateKey}_{operationId}");

            // Complete unified operation tracker
            if (operation.TrackerOperationId != null)
            {
                _operationTracker.CompleteOperation(operation.TrackerOperationId, success: false, error: ex.Message);
            }

            // Send failure notification via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionComplete, new
            {
                OperationId = operation.TrackerOperationId,
                Success = false,
                Status = OperationStatus.Failed,
                Message = ex.Message,
                Cancelled = false
            });
        }
    }

    /// <summary>
    /// Get corruption summary for a specific datasource with progress tracking.
    /// </summary>
    private async Task<Dictionary<string, long>> GetCorruptionSummaryForDatasource(
        string logDir, string cacheDir, string timezone, string rustBinaryPath,
        string operationId, string trackerOperationId, string datasourceName, CancellationToken cancellationToken)
    {
        // Create progress file for this datasource
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{datasourceName}.json");

        try
        {
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"summary \"{logDir}\" \"{cacheDir}\" \"{progressFile}\" \"{timezone}\"");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                throw new Exception("Failed to start corruption_manager process");
            }

            // Poll the progress file while the process runs
            var pollTask = Task.Run(async () =>
            {
                var lastMessage = string.Empty;
                var lastPercent = 0.0;
                const double percentThreshold = 5.0; // Send update if percent changes by 5% or more
                
                while (!process.HasExited)
                {
                    await Task.Delay(500, cancellationToken);

                    var progressData = await _rustProcessHelper.ReadProgressFileAsync<CorruptionDetectionProgressData>(progressFile);

                    if (progressData != null)
                    {
                        var messageChanged = progressData.Message != lastMessage;
                        var percentChanged = Math.Abs(progressData.PercentComplete - lastPercent) >= percentThreshold;
                        
                        // Send update if either message OR percentComplete changes significantly
                        if (messageChanged || percentChanged)
                        {
                            lastMessage = progressData.Message;
                            lastPercent = progressData.PercentComplete;

                            // Send progress notification via SignalR
                            // Use trackerOperationId so frontend can cancel via /api/operations/{id}/cancel
                            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionProgress, new
                            {
                                OperationId = trackerOperationId,
                                PercentComplete = progressData.PercentComplete,
                                Status = OperationStatus.Running,
                                Message = progressData.Message,
                                filesProcessed = progressData.FilesProcessed,
                                totalFiles = progressData.TotalFiles,
                                currentFile = progressData.CurrentFile,
                                datasourceName
                            });

                            _logger.LogDebug("[CorruptionDetection] Progress: {Percent:F1}% - {Message}",
                                progressData.PercentComplete, progressData.Message);
                        }
                    }
                }
            }, cancellationToken);

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await _processManager.WaitForProcessAsync(process, cancellationToken);
            await pollTask;

            var output = await outputTask;
            var error = await errorTask;

            _logger.LogDebug("[CorruptionDetection] Rust process exit code: {Code}", process.ExitCode);

            if (process.ExitCode != 0)
            {
                _logger.LogError("[CorruptionDetection] Failed with exit code {Code}: {Error}", process.ExitCode, error);
                throw new Exception($"corruption_manager failed with exit code {process.ExitCode}: {error}");
            }

            // Parse JSON output from Rust binary
            var summaryData = JsonSerializer.Deserialize<CorruptionSummaryData>(output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (summaryData?.ServiceCounts == null)
            {
                return new Dictionary<string, long>();
            }

            return summaryData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
        }
        finally
        {
            // Clean up progress file
            await _rustProcessHelper.DeleteTemporaryFileAsync(progressFile);
        }
    }

    /// <summary>
    /// Save corruption detection results to database for caching.
    /// </summary>
    private async Task SaveCorruptionToDatabaseAsync(Dictionary<string, long> corruptionCounts)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Use a transaction for atomicity - delete and insert as a single unit
        await using var transaction = await dbContext.Database.BeginTransactionAsync();
        try
        {
            // Use ExecuteDeleteAsync for bulk delete - avoids tracking and concurrency issues
            // This is immune to DbUpdateConcurrencyException since it doesn't track entities
            await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync();

            // Add new corruption data
            var now = DateTime.UtcNow;
            foreach (var kvp in corruptionCounts)
            {
                dbContext.CachedCorruptionDetections.Add(new CachedCorruptionDetection
                {
                    ServiceName = kvp.Key,
                    CorruptedChunkCount = kvp.Value,
                    LastDetectedUtc = now,
                    CreatedAtUtc = now
                });
            }

            await dbContext.SaveChangesAsync();
            await transaction.CommitAsync();
            _logger.LogInformation("[CorruptionDetection] Saved {Count} corruption records to database", corruptionCounts.Count);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "[CorruptionDetection] Failed to save corruption records to database, rolling back");
            throw;
        }
    }

    /// <summary>
    /// Get cached corruption detection results from database.
    /// </summary>
    public async Task<CachedCorruptionResult?> GetCachedDetectionAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cachedCorruption = await dbContext.CachedCorruptionDetections.ToListAsync();

        if (cachedCorruption.Count == 0)
        {
            return null;
        }

        var corruptionCounts = cachedCorruption.ToDictionary(
            c => c.ServiceName,
            c => c.CorruptedChunkCount);

        var lastDetectedTime = cachedCorruption.Max(c => c.LastDetectedUtc);

        return new CachedCorruptionResult
        {
            HasCachedResults = true,
            CorruptionCounts = corruptionCounts,
            LastDetectionTime = lastDetectedTime,
            TotalServicesWithCorruption = corruptionCounts.Count,
            TotalCorruptedChunks = corruptionCounts.Values.Sum()
        };
    }

    /// <summary>
    /// Remove a service's cached corruption detection entry after successful removal.
    /// </summary>
    public async Task RemoveCachedServiceAsync(string serviceName)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var deleted = await dbContext.CachedCorruptionDetections
            .Where(c => c.ServiceName == serviceName)
            .ExecuteDeleteAsync();
        _logger.LogInformation("[CorruptionDetection] Removed cached corruption entry for service: {Service} ({Deleted} rows)", serviceName, deleted);
    }

    /// <summary>
    /// Get the status of the active detection operation.
    /// </summary>
    public DetectionOperation? GetOperationStatus(string operationId)
    {
        return _operations.TryGetValue(operationId, out var op) ? op : null;
    }

    /// <summary>
    /// Get the currently active detection operation (if any).
    /// </summary>
    public DetectionOperation? GetActiveOperation()
    {
        return _operations.Values.FirstOrDefault(o => o.Status == "running");
    }

    /// <summary>
    /// Cancel the currently running detection operation.
    /// </summary>
    public bool CancelDetection()
    {
        var activeOp = GetActiveOperation();
        if (activeOp != null)
        {
            // If already cancelled or cancelling, return true (idempotent)
            // This prevents 404 errors when user clicks cancel button multiple times
            if (activeOp.Status == "cancelled" || activeOp.Status == "cancelling")
            {
                _logger.LogDebug("[CorruptionDetection] Cancellation already in progress for operation {OperationId}", activeOp.OperationId);
                return true;
            }
            
            _logger.LogInformation("[CorruptionDetection] Cancelling detection operation {OperationId}", activeOp.OperationId);
            _cancellationTokenSource?.Cancel();
            activeOp.Status = "cancelled";
            activeOp.Message = "Detection cancelled by user";
            return true;
        }
        return false;
    }

    /// <summary>
    /// Invalidate the cached corruption detection results.
    /// </summary>
    public async Task InvalidateCacheAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        // Use ExecuteDeleteAsync for bulk delete - avoids tracking and concurrency issues
        await dbContext.CachedCorruptionDetections.ExecuteDeleteAsync();
        _logger.LogInformation("[CorruptionDetection] Cache invalidated");
    }

    /// <summary>
    /// Restore interrupted operations on startup.
    /// </summary>
    public void RestoreInterruptedOperations()
    {
        try
        {
            var states = _operationStateService.GetStatesByType("corruptionDetection");
            foreach (var state in states)
            {
                _logger.LogInformation("[CorruptionDetection] Found interrupted operation {Key}, clearing state", state.Key);
                _operationStateService.RemoveState(state.Key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[CorruptionDetection] Failed to restore interrupted operations");
        }
    }
}

/// <summary>
/// JSON model for Rust corruption manager output.
/// </summary>
public class CorruptionSummaryData
{
    [System.Text.Json.Serialization.JsonPropertyName("service_counts")]
    public Dictionary<string, long>? ServiceCounts { get; set; }
}

/// <summary>
/// JSON model for Rust corruption detection progress.
/// </summary>
public class CorruptionDetectionProgressData
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public double PercentComplete { get; set; }
    public string? CurrentFile { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>
/// JSON model for Rust corruption removal progress.
/// </summary>
public class CorruptionRemovalProgressData
{
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Timestamp { get; set; }
    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public double PercentComplete { get; set; }
}

/// <summary>
/// Result model for cached corruption detection.
/// </summary>
public class CachedCorruptionResult
{
    public bool HasCachedResults { get; set; }
    public Dictionary<string, long>? CorruptionCounts { get; set; }
    public DateTime LastDetectionTime { get; set; }
    public int TotalServicesWithCorruption { get; set; }
    public long TotalCorruptedChunks { get; set; }
}
