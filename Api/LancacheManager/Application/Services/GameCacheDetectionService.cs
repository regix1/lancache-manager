using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Data;
using LancacheManager.Infrastructure.Services.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Application.Services;

/// <summary>
/// Service for running game cache detection as a background operation
/// </summary>
public class GameCacheDetectionService
{
    private readonly ILogger<GameCacheDetectionService> _logger;
    private readonly IPathResolver _pathResolver;
    private readonly OperationStateService _operationStateService;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly SteamKit2Service _steamKit2Service;
    private readonly ProcessManager _processManager;
    private readonly RustProcessHelper _rustProcessHelper;
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
        OperationStateService operationStateService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        SteamKit2Service steamKit2Service,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _operationStateService = operationStateService;
        _dbContextFactory = dbContextFactory;
        _steamKit2Service = steamKit2Service;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;

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

    /// <summary>
    /// Count unknown games (games with names starting with "Unknown Game") in cached results
    /// </summary>
    private async Task<int> CountUnknownGamesAsync()
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
            var unknownCount = await dbContext.CachedGameDetections
                .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
                .CountAsync();
            return unknownCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Failed to count unknown games");
            return 0;
        }
    }

    /// <summary>
    /// Check if unknown depot IDs now have mappings available in SteamDepotMappings
    /// Returns the count of unknown depot IDs that now have mappings
    /// </summary>
    private async Task<(int totalUnknowns, int nowMapped)> CheckUnknownDepotsForMappingsAsync()
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            // Get unknown games from cache (depot IDs stored as game_app_id for unknowns)
            var unknownGames = await dbContext.CachedGameDetections
                .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
                .ToListAsync();

            if (unknownGames.Count == 0)
            {
                return (0, 0);
            }

            // Extract depot IDs from unknown game names
            // Game names like "Unknown Game (Depot 123456)" -> extract 123456
            var unknownDepotIds = unknownGames
                .Select(g => g.GameAppId) // For unknown games, depot ID is stored as game_app_id
                .ToHashSet();

            // Check how many of these depot IDs now have mappings in SteamDepotMappings
            var mappedCount = await dbContext.SteamDepotMappings
                .Where(m => unknownDepotIds.Contains(m.DepotId))
                .Select(m => m.DepotId)
                .Distinct()
                .CountAsync();

            return (unknownGames.Count, mappedCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Failed to check unknown depots for mappings");
            return (0, 0);
        }
    }

    /// <summary>
    /// Smart pre-check: If 3+ unknown games detected in cache, check if depot mappings are now available
    /// If mappings exist, invalidate cache to trigger fresh scan with new mappings
    /// </summary>
    private async Task<bool> ApplyMappingsPreCheckAsync(DetectionOperation operation)
    {
        try
        {
            var (totalUnknowns, nowMapped) = await CheckUnknownDepotsForMappingsAsync();

            if (totalUnknowns >= 3 && nowMapped > 0)
            {
                _logger.LogInformation("[GameDetection] Found {UnknownCount} unknown games in cache, {MappedCount} now have depot mappings available",
                    totalUnknowns, nowMapped);
                operation.Message = $"Found {nowMapped} new depot mapping(s) for unknown games - invalidating cache...";

                // Invalidate cache so the scan will pick up the new mappings
                await InvalidateCacheAsync();

                _logger.LogInformation("[GameDetection] Cache invalidated - fresh scan will use new depot mappings");
                operation.Message = $"Cache invalidated - scanning with {nowMapped} new mapping(s)...";
                return true;
            }
            else if (totalUnknowns >= 3)
            {
                _logger.LogInformation("[GameDetection] Found {UnknownCount} unknown games but no new depot mappings available - proceeding with scan",
                    totalUnknowns);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Pre-check mapping failed, continuing with scan anyway");
            return false;
        }
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

            // Smart pre-check: If incremental scan and we have 3+ unknown games, try applying mappings first
            if (incremental)
            {
                await ApplyMappingsPreCheckAsync(operation);
            }

            var dataDir = _pathResolver.GetDataDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var outputJson = Path.Combine(dataDir, $"game_detection_{operationId}.json");

            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Game cache detector");

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
                // Load existing games from database
                await using (var dbContext = await _dbContextFactory.CreateDbContextAsync())
                {
                    var cachedGames = await dbContext.CachedGameDetections.ToListAsync();
                    if (cachedGames.Count > 0)
                    {
                        // Convert database records to GameCacheInfo
                        existingGames = cachedGames.Select(ConvertToGameCacheInfo).ToList();
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

            var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

            operation.Message = "Scanning database and cache directory...";

            var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, CancellationToken.None);

            // Log diagnostic output from stderr (contains scan progress and stats)
            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                _logger.LogInformation("[GameDetection] Diagnostic output:\n{DiagnosticOutput}", result.Error);
            }

            if (result.ExitCode != 0)
            {
                _logger.LogError("[GameDetection] Process failed with exit code {ExitCode}. Error: {Error}",
                    result.ExitCode, result.Error);
                throw new Exception($"Game detection failed: {result.Error}");
            }

            // Read results from JSON
            if (!File.Exists(outputJson))
            {
                throw new FileNotFoundException($"Output file not found: {outputJson}");
            }

            var json = await File.ReadAllTextAsync(outputJson);
            var detectionResult = JsonSerializer.Deserialize<GameDetectionResult>(json);

            if (detectionResult == null)
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
                finalGames.AddRange(detectionResult.Games);
                totalGamesDetected = finalGames.Count;

                var newGamesCount = detectionResult.TotalGamesDetected;
                _logger.LogInformation("[GameDetection] Incremental scan complete: {NewCount} new games, {TotalCount} total",
                    newGamesCount, totalGamesDetected);

                operation.Message = newGamesCount > 0
                    ? $"Found {newGamesCount} new game{(newGamesCount != 1 ? "s" : "")} ({totalGamesDetected} total with cache files)"
                    : $"No new games detected ({totalGamesDetected} total with cache files)";
            }
            else
            {
                finalGames = detectionResult.Games;
                totalGamesDetected = detectionResult.TotalGamesDetected;
                operation.Message = $"Detected {totalGamesDetected} games with cache files";
            }

            operation.Status = "complete";
            operation.Games = finalGames;
            operation.TotalGamesDetected = totalGamesDetected;

            // Save results to database (replaces in-memory cache)
            await SaveGamesToDatabaseAsync(finalGames, incremental);
            _logger.LogInformation("[GameDetection] Results saved to database - {Count} games total", totalGamesDetected);

            // Update persisted state with complete status
            _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
            {
                Key = $"gameDetection_{operationId}",
                Type = "gameDetection",
                Status = "complete",
                Message = operation.Message,
                Data = JsonSerializer.SerializeToElement(new { operationId, totalGamesDetected = detectionResult.TotalGamesDetected })
            });

            _logger.LogInformation("[GameDetection] Completed: {Count} games detected", detectionResult.TotalGamesDetected);

            // Clean up temporary files
            await _rustProcessHelper.DeleteTemporaryFileAsync(outputJson);
            if (!string.IsNullOrEmpty(excludedIdsPath))
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(excludedIdsPath);
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
            if (!string.IsNullOrEmpty(excludedIdsPath))
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(excludedIdsPath);
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

    public async Task<DetectionOperation?> GetCachedDetectionAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cachedGames = await dbContext.CachedGameDetections.ToListAsync();

        if (cachedGames.Count == 0)
        {
            return null;
        }

        var games = cachedGames.Select(ConvertToGameCacheInfo).ToList();

        return new DetectionOperation
        {
            OperationId = "cached",
            StartTime = cachedGames.Max(g => g.LastDetectedUtc),
            Status = "complete",
            Message = $"Loaded {games.Count} games from cache",
            Games = games,
            TotalGamesDetected = games.Count
        };
    }

    public async Task InvalidateCacheAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        await dbContext.CachedGameDetections.ExecuteDeleteAsync();
        await dbContext.SaveChangesAsync();
        _logger.LogInformation("[GameDetection] Cache invalidated - all cached games deleted from database");
    }

    public async Task RemoveGameFromCacheAsync(uint gameAppId)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var game = await dbContext.CachedGameDetections.FirstOrDefaultAsync(g => g.GameAppId == gameAppId);

        if (game != null)
        {
            dbContext.CachedGameDetections.Remove(game);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("[GameDetection] Removed game {AppId} ({GameName}) from cache",
                gameAppId, game.GameName);
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

    private async Task SaveGamesToDatabaseAsync(List<GameCacheInfo> games, bool incremental)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        if (!incremental)
        {
            // Full scan - clear existing cache first
            await dbContext.CachedGameDetections.ExecuteDeleteAsync();
        }

        var now = DateTime.UtcNow;

        foreach (var game in games)
        {
            var cachedGame = new CachedGameDetection
            {
                GameAppId = game.GameAppId,
                GameName = game.GameName,
                CacheFilesFound = game.CacheFilesFound,
                TotalSizeBytes = game.TotalSizeBytes,
                DepotIdsJson = JsonSerializer.Serialize(game.DepotIds),
                SampleUrlsJson = JsonSerializer.Serialize(game.SampleUrls),
                CacheFilePathsJson = JsonSerializer.Serialize(game.CacheFilePaths),
                LastDetectedUtc = now,
                CreatedAtUtc = now
            };

            // Use upsert pattern - update if exists, insert if new
            var existing = await dbContext.CachedGameDetections
                .FirstOrDefaultAsync(g => g.GameAppId == game.GameAppId);

            if (existing != null)
            {
                existing.GameName = cachedGame.GameName;
                existing.CacheFilesFound = cachedGame.CacheFilesFound;
                existing.TotalSizeBytes = cachedGame.TotalSizeBytes;
                existing.DepotIdsJson = cachedGame.DepotIdsJson;
                existing.SampleUrlsJson = cachedGame.SampleUrlsJson;
                existing.CacheFilePathsJson = cachedGame.CacheFilePathsJson;
                existing.LastDetectedUtc = now;
            }
            else
            {
                dbContext.CachedGameDetections.Add(cachedGame);
            }
        }

        await dbContext.SaveChangesAsync();
    }

    private static GameCacheInfo ConvertToGameCacheInfo(CachedGameDetection cached)
    {
        return new GameCacheInfo
        {
            GameAppId = cached.GameAppId,
            GameName = cached.GameName,
            CacheFilesFound = cached.CacheFilesFound,
            TotalSizeBytes = cached.TotalSizeBytes,
            DepotIds = JsonSerializer.Deserialize<List<uint>>(cached.DepotIdsJson) ?? new List<uint>(),
            SampleUrls = JsonSerializer.Deserialize<List<string>>(cached.SampleUrlsJson) ?? new List<string>(),
            CacheFilePaths = JsonSerializer.Deserialize<List<string>>(cached.CacheFilePathsJson) ?? new List<string>()
        };
    }

    private class GameDetectionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("total_games_detected")]
        public int TotalGamesDetected { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("games")]
        public List<GameCacheInfo> Games { get; set; } = new();
    }
}
