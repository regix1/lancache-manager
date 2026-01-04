using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

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
    private readonly IHubContext<DownloadHub> _hubContext;
    private readonly DatasourceService _datasourceService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly OperationStateService _operationStateService;

    // Track active detection operations
    private readonly ConcurrentDictionary<string, DetectionOperation> _operations = new();

    private const string OperationStateKey = "corruptionDetection";

    public class DetectionOperation
    {
        public string OperationId { get; set; } = string.Empty;
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
        IHubContext<DownloadHub> hubContext,
        DatasourceService datasourceService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        OperationStateService operationStateService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _hubContext = hubContext;
        _datasourceService = datasourceService;
        _dbContextFactory = dbContextFactory;
        _operationStateService = operationStateService;
    }

    /// <summary>
    /// Start a background corruption detection scan.
    /// Returns immediately with an operation ID.
    /// </summary>
    public async Task<string> StartDetectionAsync(CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid().ToString("N")[..8];

        // Check if there's already an active detection
        var activeOp = _operations.Values.FirstOrDefault(o => o.Status == "running");
        if (activeOp != null)
        {
            _logger.LogWarning("[CorruptionDetection] Detection already in progress: {OperationId}", activeOp.OperationId);
            return activeOp.OperationId;
        }

        var operation = new DetectionOperation
        {
            OperationId = operationId,
            StartTime = DateTime.UtcNow,
            Status = "running",
            Message = "Starting corruption detection..."
        };

        _operations[operationId] = operation;

        // Save operation state for recovery
        _operationStateService.SaveState($"{OperationStateKey}_{operationId}", new OperationState
        {
            Key = $"{OperationStateKey}_{operationId}",
            Type = "corruptionDetection",
            Status = "running",
            Message = "Starting corruption detection..."
        });

        // Send start notification via SignalR
        await _hubContext.Clients.All.SendAsync("CorruptionDetectionStarted", new
        {
            operationId,
            message = "Starting corruption detection scan..."
        }, cancellationToken);

        // Run detection in background
        _ = Task.Run(async () => await RunDetectionAsync(operationId, cancellationToken), cancellationToken);

        return operationId;
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

            // Process each datasource
            foreach (var datasource in datasources)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var dsCounts = await GetCorruptionSummaryForDatasource(
                    datasource.LogPath, datasource.CachePath, timezone, rustBinaryPath, cancellationToken);

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
            operation.Status = "complete";
            operation.Message = $"Detection complete. Found {aggregatedCounts.Count} services with corruption.";
            operation.CorruptionCounts = aggregatedCounts;
            operation.LastDetectionTime = DateTime.UtcNow;

            // Save results to database
            await SaveCorruptionToDatabaseAsync(aggregatedCounts);

            // Clear operation state
            _operationStateService.RemoveState($"{OperationStateKey}_{operationId}");

            // Send completion notification via SignalR
            await _hubContext.Clients.All.SendAsync("CorruptionDetectionComplete", new
            {
                operationId,
                success = true,
                message = operation.Message,
                totalServicesWithCorruption = aggregatedCounts.Count,
                totalCorruptedChunks = aggregatedCounts.Values.Sum()
            });

            _logger.LogInformation("[CorruptionDetection] Detection complete: {Services}",
                string.Join(", ", aggregatedCounts.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[CorruptionDetection] Detection failed for operation {OperationId}", operationId);

            operation.Status = "failed";
            operation.Message = ex.Message;

            // Clear operation state
            _operationStateService.RemoveState($"{OperationStateKey}_{operationId}");

            // Send failure notification via SignalR
            await _hubContext.Clients.All.SendAsync("CorruptionDetectionComplete", new
            {
                operationId,
                success = false,
                error = ex.Message
            });
        }
    }

    /// <summary>
    /// Get corruption summary for a specific datasource.
    /// </summary>
    private async Task<Dictionary<string, long>> GetCorruptionSummaryForDatasource(
        string logDir, string cacheDir, string timezone, string rustBinaryPath, CancellationToken cancellationToken)
    {
        var startInfo = _rustProcessHelper.CreateProcessStartInfo(
            rustBinaryPath,
            $"summary \"{logDir}\" \"{cacheDir}\" \"{timezone}\"");

        using var process = Process.Start(startInfo);
        if (process == null)
        {
            throw new Exception("Failed to start corruption_manager process");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await _processManager.WaitForProcessAsync(process, cancellationToken);

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

    /// <summary>
    /// Save corruption detection results to database for caching.
    /// </summary>
    private async Task SaveCorruptionToDatabaseAsync(Dictionary<string, long> corruptionCounts)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Clear existing cached corruption data
        dbContext.CachedCorruptionDetections.RemoveRange(dbContext.CachedCorruptionDetections);
        await dbContext.SaveChangesAsync();

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
        _logger.LogInformation("[CorruptionDetection] Saved {Count} corruption records to database", corruptionCounts.Count);
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
    /// Invalidate the cached corruption detection results.
    /// </summary>
    public async Task InvalidateCacheAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        dbContext.CachedCorruptionDetections.RemoveRange(dbContext.CachedCorruptionDetections);
        await dbContext.SaveChangesAsync();
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
    public Dictionary<string, long>? ServiceCounts { get; set; }
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
