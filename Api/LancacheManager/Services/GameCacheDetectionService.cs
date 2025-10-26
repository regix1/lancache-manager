using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Data;

namespace LancacheManager.Services;

/// <summary>
/// Service for running game cache detection as a background operation
/// </summary>
public class GameCacheDetectionService
{
    private readonly ILogger<GameCacheDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly OperationStateService _operationStateService;
    private readonly ConcurrentDictionary<string, DetectionOperation> _operations = new();

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

    public string StartDetectionAsync()
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
        _ = Task.Run(async () => await RunDetectionAsync(operationId));

        return operationId;
    }

    private async Task RunDetectionAsync(string operationId)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            return;
        }

        try
        {
            _logger.LogInformation("[GameDetection] Starting detection for operation {OperationId}", operationId);

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

            var startInfo = new ProcessStartInfo
            {
                FileName = rustBinaryPath,
                Arguments = $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\"",
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

                operation.Status = "complete";
                operation.Message = $"Detected {result.TotalGamesDetected} games with cache files";
                operation.Games = result.Games;
                operation.TotalGamesDetected = result.TotalGamesDetected;

                // Update persisted state
                _operationStateService.UpdateState($"gameDetection_{operationId}", new Dictionary<string, object>
                {
                    ["Status"] = "complete",
                    ["Message"] = operation.Message,
                    ["TotalGamesDetected"] = result.TotalGamesDetected
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
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Operation {OperationId} failed", operationId);
            operation.Status = "failed";
            operation.Error = ex.Message;
            operation.Message = $"Detection failed: {ex.Message}";

            // Update persisted state
            _operationStateService.UpdateState($"gameDetection_{operationId}", new Dictionary<string, object>
            {
                ["Status"] = "failed",
                ["Message"] = operation.Message,
                ["Error"] = ex.Message
            });
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

    private void RestoreInterruptedOperations()
    {
        try
        {
            var allStates = _operationStateService.GetAllStates();
            var gameDetectionStates = allStates.Where(s => s.Type == "gameDetection" && s.Status == "running");

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

                        // Restart the detection task
                        _ = Task.Run(async () => await RunDetectionAsync(operationId));
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
