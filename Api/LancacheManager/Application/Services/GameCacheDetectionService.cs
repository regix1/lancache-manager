using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Services.Interfaces;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for running game cache detection as a background operation
/// </summary>
public class GameCacheDetectionService
{
    private readonly ILogger<GameCacheDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly OperationStateService _operationStateService;
    private readonly ConcurrentDictionary<string, DetectionOperation> _operations = new();

    // Cache for detection results - persists until backend restart
    private DetectionOperation? _cachedDetectionResult = null;
    private readonly object _cacheLock = new object();

    public class DetectionOperation
    {
        public string OperationId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string Status { get; set; } = "running"; // running, complete, failed
        public string? Message { get; set; }
        public List<GameCacheInfo>? Games { get; set; }
        public int TotalGamesDetected { get; set; }
        public string? Error { get; set; }
    }

    public GameCacheDetectionService(
        ILogger<GameCacheDetectionService> logger,
        IPathResolver pathResolver,
        OperationStateService operationStateService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _operationStateService = operationStateService;

        // Restore any interrupted operations on startup
        RestoreInterruptedOperations();
    }

    public string StartDetectionAsync(bool incremental = true)
    {
        var operationId = Guid.NewGuid().ToString();
        var operation = new DetectionOperation
        {
            OperationId = operationId,
            StartTime = DateTime.UtcNow,
            Status = "running",
            Message = "Starting game cache detection..."
        };

        _operations[operationId] = operation;

        // Save to OperationStateService for persistence
        _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
        {
            Key = $"gameDetection_{operationId}",
            Type = "gameDetection",
            Status = "running",
            Message = operation.Message,
            Data = JsonSerializer.SerializeToElement(new { operationId })
        });

        // Start detection in background
        _ = Task.Run(async () => await RunDetectionAsync(operationId, incremental));

        return operationId;
    }

    private async Task RunDetectionAsync(string operationId, bool incremental)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            return;
        }

        string? excludedIdsPath = null;
        List<GameCacheInfo>? existingGames = null;

        try
        {
            _logger.LogInformation("[GameDetection] Starting detection for operation {OperationId} (incremental={Incremental})", operationId, incremental);

            var dataDir = _pathResolver.GetDataDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var outputJson = Path.Combine(dataDir, $"game_detection_{operationId}.json");

            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();

            if (!File.Exists(rustBinaryPath))
            {
                throw new FileNotFoundException($"Game cache detector binary not found at {rustBinaryPath}");
            }

            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"Database not found at {dbPath}");
            }

            var cachePath = _pathResolver.GetCacheDirectory();

            _logger.LogInformation("[GameDetection] Using cache path: {CachePath}", cachePath);

            // Build arguments - add excluded game IDs if incremental and cache exists
            string arguments;
            if (incremental)
            {
                lock (_cacheLock)
                {
                    if (_cachedDetectionResult != null && _cachedDetectionResult.Games != null && _cachedDetectionResult.Games.Count > 0)
                    {
                        existingGames = _cachedDetectionResult.Games;
                        var excludedGameIds = existingGames.Select(g => g.GameAppId).ToList();

                        excludedIdsPath = Path.Combine(dataDir, $"excluded_game_ids_{operationId}.json");
                        var excludedIdsJson = JsonSerializer.Serialize(excludedGameIds);
                        File.WriteAllText(excludedIdsPath, excludedIdsJson);

                        _logger.LogInformation("[GameDetection] Incremental scan: excluding {ExcludedCount} already-detected games", excludedGameIds.Count);
                        operation.Message = $"Scanning for new games (skipping {excludedGameIds.Count} already detected)...";

                        arguments = $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\" \"{excludedIdsPath}\"";
                    }
                    else
                    {
                        _logger.LogInformation("[GameDetection] No cached results found, performing full scan");
                        arguments = $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\"";
                    }
                }
            }
            else
            {
                _logger.LogInformation("[GameDetection] Force refresh: performing full scan");
                arguments = $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = rustBinaryPath,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            operation.Message = "Scanning database and cache directory...";

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start game_cache_detector process");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                await process.WaitForExitAsync();

                var output = await outputTask;
                var error = await errorTask;

                // Log diagnostic output from stderr (contains scan progress and stats)
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogInformation("[GameDetection] Diagnostic output:\n{DiagnosticOutput}", error);
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogError("[GameDetection] Process failed with exit code {ExitCode}. Error: {Error}",
                        process.ExitCode, error);
                    throw new Exception($"Game detection failed: {error}");
                }

                // Read results from JSON
                if (!File.Exists(outputJson))
                {
                    throw new FileNotFoundException($"Output file not found: {outputJson}");
                }

                var json = await File.ReadAllTextAsync(outputJson);
                var result = JsonSerializer.Deserialize<GameDetectionResult>(json);

                if (result == null)
                {
                    throw new Exception("Failed to parse detection results");
                }

                // Merge with existing games if this was an incremental scan
                List<GameCacheInfo> finalGames;
                int totalGamesDetected;

                if (incremental && existingGames != null && existingGames.Count > 0)
                {
                    // Merge existing games with newly detected games
                    finalGames = existingGames.ToList();
                    finalGames.AddRange(result.Games);
                    totalGamesDetected = finalGames.Count;

                    var newGamesCount = result.TotalGamesDetected;
                    _logger.LogInformation("[GameDetection] Incremental scan complete: {NewCount} new games, {TotalCount} total",
                        newGamesCount, totalGamesDetected);

                    operation.Message = newGamesCount > 0
                        ? $"Found {newGamesCount} new game{(newGamesCount != 1 ? "s" : "")} ({totalGamesDetected} total with cache files)"
                        : $"No new games detected ({totalGamesDetected} total with cache files)";
                }
                else
                {
                    finalGames = result.Games;
                    totalGamesDetected = result.TotalGamesDetected;
                    operation.Message = $"Detected {totalGamesDetected} games with cache files";
                }

                operation.Status = "complete";
                operation.Games = finalGames;
                operation.TotalGamesDetected = totalGamesDetected;

                // Cache the completed detection result (merged or full)
                lock (_cacheLock)
                {
                    _cachedDetectionResult = operation;
                    _logger.LogInformation("[GameDetection] Results cached - {Count} games total", totalGamesDetected);
                }

                // Update persisted state with complete status
                _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
                {
                    Key = $"gameDetection_{operationId}",
                    Type = "gameDetection",
                    Status = "complete",
                    Message = operation.Message,
                    Data = JsonSerializer.SerializeToElement(new { operationId, totalGamesDetected = result.TotalGamesDetected })
                });

                _logger.LogInformation("[GameDetection] Completed: {Count} games detected", result.TotalGamesDetected);

                // Clean up output file
                try
                {
                    File.Delete(outputJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete output file: {File}", outputJson);
                }

                // Clean up excluded IDs file if it was created
                if (!string.IsNullOrEmpty(excludedIdsPath) && File.Exists(excludedIdsPath))
                {
                    try
                    {
                        File.Delete(excludedIdsPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete excluded IDs file: {File}", excludedIdsPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Operation {OperationId} failed", operationId);
            operation.Status = "failed";
            operation.Error = ex.Message;
            operation.Message = $"Detection failed: {ex.Message}";

            // Update persisted state with failed status
            _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
            {
                Key = $"gameDetection_{operationId}",
                Type = "gameDetection",
                Status = "failed",
                Message = operation.Message,
                Data = JsonSerializer.SerializeToElement(new { operationId, error = ex.Message })
            });

            // Clean up excluded IDs file in error path too
            if (!string.IsNullOrEmpty(excludedIdsPath) && File.Exists(excludedIdsPath))
            {
                try
                {
                    File.Delete(excludedIdsPath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to delete excluded IDs file: {File}", excludedIdsPath);
                }
            }
        }
    }

    public DetectionOperation? GetOperationStatus(string operationId)
    {
        return _operations.TryGetValue(operationId, out var operation) ? operation : null;
    }

    public void CleanupOldOperations(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var oldOperations = _operations
            .Where(kvp => kvp.Value.StartTime < cutoff && kvp.Value.Status != "running")
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var operationId in oldOperations)
        {
            _operations.TryRemove(operationId, out _);
        }
    }

    public DetectionOperation? GetActiveOperation()
    {
        return _operations.Values.FirstOrDefault(op => op.Status == "running");
    }

    public DetectionOperation? GetCachedDetection()
    {
        lock (_cacheLock)
        {
            return _cachedDetectionResult;
        }
    }

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _logger.LogInformation("[GameDetection] Cache invalidated");
            _cachedDetectionResult = null;
        }
    }

    private void RestoreInterruptedOperations()
    {
        try
        {
            var allStates = _operationStateService.GetAllStates();

            // Only restore operations that are recent (within last 5 minutes) to avoid re-running old completed operations
            var recentCutoff = DateTime.UtcNow.AddMinutes(-5);
            var gameDetectionStates = allStates.Where(s =>
                s.Type == "gameDetection" &&
                s.Status == "running" &&
                s.CreatedAt > recentCutoff);

            foreach (var state in gameDetectionStates)
            {
                if (state.Data.HasValue && state.Data.Value.TryGetProperty("operationId", out var opIdElement))
                {
                    var operationId = opIdElement.GetString();
                    if (!string.IsNullOrEmpty(operationId))
                    {
                        var operation = new DetectionOperation
                        {
                            OperationId = operationId,
                            StartTime = state.CreatedAt,
                            Status = "running",
                            Message = state.Message ?? "Resuming game cache detection..."
                        };

                        _operations[operationId] = operation;
                        _logger.LogInformation("[GameDetection] Restored interrupted operation {OperationId}", operationId);

                        // Restart the detection task with incremental scanning (default)
                        _ = Task.Run(async () => await RunDetectionAsync(operationId, incremental: true));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Error restoring interrupted operations");
        }
    }

    private class GameDetectionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("total_games_detected")]
        public int TotalGamesDetected { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("games")]
        public List<GameCacheInfo> Games { get; set; } = new();
    }
}
