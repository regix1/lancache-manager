using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
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
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly OperationStateService _operationStateService;
    private readonly IUnifiedOperationTracker _operationTracker;

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _recentlyRemovedServices = new();
    private static readonly TimeSpan _removalGracePeriod = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Key prefix used when persisting corruption-detection operation state. Derived from
    /// <see cref="OperationType.CorruptionDetection"/> so the wire value stays in sync with
    /// the enum (N6: no raw literal).
    /// </summary>
    private static readonly string _operationStateKey = OperationType.CorruptionDetection.ToWireString();

    public CorruptionDetectionService(
        ILogger<CorruptionDetectionService> logger,
        IPathResolver pathResolver,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        OperationStateService operationStateService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
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
    /// <param name="detectionMode">Wire value of the detection mode ("miss_count" or "redownload").
    /// Parsed into <see cref="CorruptionDetectionMode"/>; unrecognized values fall back to
    /// <see cref="CorruptionDetectionMode.MissCount"/>.</param>
    public async Task<Guid> StartDetectionAsync(int threshold = 3, bool compareToCacheLogs = true, string detectionMode = "miss_count", CancellationToken cancellationToken = default)
    {
        // Parse wire value into the typed enum; unknown/null falls back to miss_count (legacy default).
        var mode = CorruptionDetectionModeExtensions.Parse(detectionMode);
        if (mode == CorruptionDetectionMode.Unknown)
        {
            mode = CorruptionDetectionMode.MissCount;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            // Check if there's already an active detection
            var activeOp = _operationTracker.GetActiveOperations(OperationType.CorruptionDetection).FirstOrDefault();
            if (activeOp != null)
            {
                _logger.LogWarning("[CorruptionDetection] Detection already in progress: {OperationId}", activeOp.Id);
                return activeOp.Id;
            }

            // Create a new cancellation token source
            // Note: Don't cancel/dispose old one here - it may have been disposed by CompleteOperation
            var cts = new CancellationTokenSource();

            // Create metadata and register with unified operation tracker.
            // The same metadata instance is mutated in-place by UpdateMetadata before CompleteOperation,
            // so the onTerminalEmit closure reads the final CorruptionCounts by value off this capture.
            // operationId is captured by the closure and is fully assigned before the closure can ever
            // run (it only fires at terminal time, inside CompleteOperation).
            var metadata = new CorruptionDetectionMetrics();
            Guid operationId = Guid.Empty;
            operationId = _operationTracker.RegisterOperation(
                OperationType.CorruptionDetection,
                "Corruption Detection",
                cts,
                metadata,
                onTerminalEmit: info => EmitTerminalAsync(info, operationId, metadata));

            // Save operation state for recovery
            _operationStateService.SaveState($"{_operationStateKey}_{operationId}", new OperationState
            {
                Key = $"{_operationStateKey}_{operationId}",
                Type = OperationType.CorruptionDetection.ToWireString(),
                Status = OperationStatus.Running.ToWireString(),
                Message = "Starting corruption detection..."
            });

            // Send start notification via SignalR
            await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionStarted, new
            {
                OperationId = operationId,
                StageKey = "signalr.corruptionDetect.starting"
            });

            // Run detection in background with the cancellation token
            var token = cts.Token;
            if (mode == CorruptionDetectionMode.Redownload)
            {
                _ = Task.Run(async () => await RunRedownloadDetectionAsync(operationId, threshold, token), token);
            }
            else
            {
                _ = Task.Run(async () => await RunDetectionAsync(operationId, threshold, compareToCacheLogs, token), token);
            }

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Single terminal SignalR emitter for a corruption-detection operation. Invoked EXACTLY ONCE
    /// from inside <c>CompleteOperation</c> (CompletedFlag-gated) via the registered
    /// <c>OnTerminalEmit</c> closure, covering the success, cancel, and error paths. Reads the final
    /// corruption totals by value off the captured <paramref name="metadata"/> (mutated in-place by
    /// <c>UpdateMetadata</c> before completion); on cancel/error the counts are unset → totals are 0.
    /// </summary>
    private Task EmitTerminalAsync(OperationTerminalInfo info, Guid operationId, CorruptionDetectionMetrics metadata)
    {
        var counts = metadata.CorruptionCounts;
        var totalServicesWithCorruption = counts?.Count ?? 0;
        var totalCorruptedChunks = counts != null ? (int)Math.Min(counts.Values.Sum(), int.MaxValue) : 0;

        if (info.Cancelled)
        {
            return _notifications.NotifyAllAsync(
                SignalREvents.CorruptionDetectionComplete,
                new SignalRNotifications.CorruptionDetectionComplete(
                    Success: false,
                    OperationId: operationId,
                    StageKey: "signalr.corruptionDetect.cancelled",
                    Status: OperationStatus.Cancelled,
                    Cancelled: true));
        }

        if (info.Success)
        {
            return _notifications.NotifyAllAsync(
                SignalREvents.CorruptionDetectionComplete,
                new SignalRNotifications.CorruptionDetectionComplete(
                    Success: true,
                    OperationId: operationId,
                    StageKey: "signalr.corruptionDetect.complete",
                    Status: OperationStatus.Completed,
                    Cancelled: false,
                    TotalServicesWithCorruption: totalServicesWithCorruption,
                    TotalCorruptedChunks: totalCorruptedChunks,
                    Context: new Dictionary<string, object?> { ["count"] = totalServicesWithCorruption }));
        }

        return _notifications.NotifyAllAsync(
            SignalREvents.CorruptionDetectionComplete,
            new SignalRNotifications.CorruptionDetectionComplete(
                Success: false,
                OperationId: operationId,
                StageKey: "signalr.corruptionDetect.failed",
                Status: OperationStatus.Failed,
                Cancelled: false,
                Error: info.Error,
                Context: new Dictionary<string, object?> { ["errorDetail"] = info.Error }));
    }

    /// <summary>
    /// Run the actual corruption detection scan.
    /// </summary>
    private async Task RunDetectionAsync(Guid operationId, int threshold, bool compareToCacheLogs, CancellationToken cancellationToken, bool detectRedownloads = false)
    {
        var operation = _operationTracker.GetOperation(operationId);
        if (operation == null)
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

            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Corruption manager");

            _logger.LogInformation("[CorruptionDetection] Starting detection for {Count} datasource(s)", datasources.Count);

            // Check for cancellation at start
            cancellationToken.ThrowIfCancellationRequested();

            // Process each datasource
            foreach (var datasource in datasources)
            {
                // Check for cancellation before each datasource
                cancellationToken.ThrowIfCancellationRequested();

                var dsCounts = await GetSummaryForDatasourceAsync(
                    datasource.LogPath, datasource.CachePath, timezone, rustBinaryPath,
                    operationId, datasource.Name, threshold, compareToCacheLogs, cancellationToken, detectRedownloads);

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

            // Filter out services within grace period after removal
            var expiredKeys = _recentlyRemovedServices.Where(kvp => DateTime.UtcNow - kvp.Value > _removalGracePeriod).Select(kvp => kvp.Key).ToList();
            foreach (var key in expiredKeys)
            {
                _recentlyRemovedServices.TryRemove(key, out _);
            }

            foreach (var service in _recentlyRemovedServices.Keys)
            {
                if (aggregatedCounts.Remove(service))
                {
                    _logger.LogInformation("[CorruptionDetection] Skipping recently removed service '{Service}' (grace period)", service);
                }
            }

            // Update operation via tracker
            _operationTracker.UpdateProgress(operationId, 100, "signalr.corruptionDetect.complete");
            _operationTracker.UpdateMetadata(operationId, metadata =>
            {
                var metrics = (CorruptionDetectionMetrics)metadata;
                metrics.CorruptionCounts = aggregatedCounts;
                metrics.LastDetectionTime = DateTime.UtcNow;
            });

            // Save results to database
            await SaveToDatabaseAsync(aggregatedCounts);

            // Clear operation state
            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");

            // Complete unified operation tracker. The terminal CorruptionDetectionComplete event is
            // emitted EXACTLY ONCE from inside CompleteOperation via the registered OnTerminalEmit
            // closure (success path); metrics are read by value off the metadata updated just above.
            _operationTracker.CompleteOperation(operationId, success: true);

            _logger.LogInformation("[CorruptionDetection] Detection complete: {Services}",
                string.Join(", ", aggregatedCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[CorruptionDetection] Operation {OperationId} was cancelled", operationId);

            // Clear operation state
            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");

            // Complete unified operation tracker. The terminal cancelled event is emitted from the
            // OnTerminalEmit closure (info.Cancelled branch) inside CompleteOperation.
            _operationTracker.CompleteOperation(operationId, success: false, error: "Cancelled by user");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CorruptionDetection] Detection failed for operation {OperationId}", operationId);

            // Clear operation state
            _operationStateService.RemoveState($"{_operationStateKey}_{operationId}");

            // Complete unified operation tracker. The terminal failed event is emitted from the
            // OnTerminalEmit closure (error branch) inside CompleteOperation.
            _operationTracker.CompleteOperation(operationId, success: false, error: ex.Message);
        }
    }

    /// <summary>
    /// Get corruption summary for a specific datasource with progress tracking.
    /// </summary>
    private async Task<Dictionary<string, long>> GetSummaryForDatasourceAsync(
        string logDir, string cacheDir, string timezone, string rustBinaryPath,
        Guid operationId, string datasourceName, int threshold, bool compareToCacheLogs, CancellationToken cancellationToken, bool detectRedownloads = false)
    {
        var operationsDir = _pathResolver.GetOperationsDirectory();
        Directory.CreateDirectory(operationsDir);
        var progressFile = Path.Combine(operationsDir, $"corruption_detection_{operationId}_{datasourceName}.json");

        try
        {
            var noCacheCheckFlag = !compareToCacheLogs ? " --no-cache-check" : "";
            var redownloadFlag = detectRedownloads ? " --detect-redownloads" : "";
            var startInfo = _rustProcessHelper.CreateProcessStartInfo(
                rustBinaryPath,
                $"summary \"{logDir}\" \"{cacheDir}\" \"{progressFile}\" \"{timezone}\" {threshold}{noCacheCheckFlag}{redownloadFlag}");

            var lastMessage = string.Empty;
            var lastPercent = 0.0;
            const double percentThreshold = 5.0;

            var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressAsync<CorruptionDetectionProgressData>(
                startInfo,
                operationId,
                cancellationToken,
                progressFile,
                async progressData =>
                {
                    var keyChanged = progressData.StageKey != lastMessage;
                    var percentChanged = Math.Abs(progressData.PercentComplete - lastPercent) >= percentThreshold;

                    if (!keyChanged && !percentChanged)
                    {
                        return;
                    }

                    lastMessage = progressData.StageKey ?? string.Empty;
                    lastPercent = progressData.PercentComplete;

                    await _notifications.NotifyAllAsync(SignalREvents.CorruptionDetectionProgress, new
                    {
                        OperationId = operationId,
                        PercentComplete = progressData.PercentComplete,
                        Status = OperationStatus.Running,
                        StageKey = progressData.StageKey,
                        Context = progressData.Context,
                        filesProcessed = progressData.FilesProcessed,
                        totalFiles = progressData.TotalFiles,
                        currentFile = progressData.CurrentFile,
                        datasourceName
                    });

                    _logger.LogDebug("[CorruptionDetection] Progress: {Percent:F1}% - {StageKey}",
                        progressData.PercentComplete, progressData.StageKey);
                },
                "corruption_manager");

            _logger.LogDebug("[CorruptionDetection] Rust process exit code: {Code}", result.ExitCode);

            if (result.ExitCode != 0)
            {
                _logger.LogError("[CorruptionDetection] Failed with exit code {Code}: {Error}", result.ExitCode, result.Error);
                throw new Exception($"corruption_manager failed with exit code {result.ExitCode}: {result.Error}");
            }

            var summaryData = JsonSerializer.Deserialize<CorruptionSummaryData>(result.Output,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (summaryData?.ServiceCounts == null)
            {
                return new Dictionary<string, long>();
            }

            return summaryData.ServiceCounts.ToDictionary(kvp => kvp.Key, kvp => (long)kvp.Value);
        }
        finally
        {
            await _rustProcessHelper.DeleteTempFileAsync(progressFile);
        }
    }

    /// <summary>
    /// Save corruption detection results to database for caching.
    /// </summary>
    private async Task SaveToDatabaseAsync(Dictionary<string, long> corruptionCounts)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Use a transaction for atomicity - delete and insert as a single unit
        // Wrap in execution strategy so EF Core can replay the transaction on transient failures
        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
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
        });
    }

    /// <summary>
    /// Run re-download detection via the Rust binary - finds URLs with multiple HIT responses
    /// from the same client by scanning access logs directly.
    /// </summary>
    private async Task RunRedownloadDetectionAsync(Guid operationId, int threshold, CancellationToken cancellationToken)
    {
        // Reuse the same flow as RunDetectionAsync but with detectRedownloads=true
        await RunDetectionAsync(operationId, threshold, compareToCacheLogs: true, cancellationToken, detectRedownloads: true);
    }

    /// <summary>
    /// Get cached corruption detection results from database.
    /// </summary>
    public async Task<CachedCorruptionResult?> GetDetectionAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cachedCorruption = await dbContext.CachedCorruptionDetections.AsNoTracking().ToListAsync();

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
    public async Task ClearServiceCacheAsync(string serviceName)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var deleted = await dbContext.CachedCorruptionDetections
            .Where(c => c.ServiceName == serviceName)
            .ExecuteDeleteAsync();

        // Track service removal to prevent immediate reappearance
        _recentlyRemovedServices[serviceName.ToLowerInvariant()] = DateTime.UtcNow;

        _logger.LogInformation("[CorruptionDetection] Removed cached corruption entry for service: {Service} ({Deleted} rows). Grace period active for {Minutes} minutes.",
            serviceName, deleted, _removalGracePeriod.TotalMinutes);
    }

    /// <summary>
    /// Get the status of the active detection operation.
    /// </summary>
    public OperationInfo? GetOperationStatus(Guid operationId)
    {
        return _operationTracker.GetOperation(operationId);
    }

    /// <summary>
    /// Get the currently active detection operation (if any).
    /// </summary>
    public OperationInfo? GetActiveOperation()
    {
        return _operationTracker.GetActiveOperations(OperationType.CorruptionDetection).FirstOrDefault();
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
            if (activeOp.Status == OperationStatus.Cancelled || activeOp.Status == OperationStatus.Cancelling)
            {
                _logger.LogDebug("[CorruptionDetection] Cancellation already in progress for operation {OperationId}", activeOp.Id);
                return true;
            }

            _logger.LogInformation("[CorruptionDetection] Cancelling detection operation {OperationId}", activeOp.Id);
            _operationTracker.CancelOperation(activeOp.Id);
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

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    public int FilesProcessed { get; set; }
    public int TotalFiles { get; set; }
    public double PercentComplete { get; set; }
    public string? CurrentFile { get; set; }
    public string? Timestamp { get; set; }
}

/// <summary>
/// JSON model for Rust corruption removal progress.
/// </summary>
internal class CorruptionRemovalProgressData
{
    [System.Text.Json.Serialization.JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
    public string? StageKey { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("context")]
    public Dictionary<string, object?>? Context { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("filesProcessed")]
    public int FilesProcessed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalFiles")]
    public int TotalFiles { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }
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
