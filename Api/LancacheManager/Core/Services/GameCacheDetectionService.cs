using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Core.Services;

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
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly ConcurrentDictionary<string, DetectionOperation> _operations = new();
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private const string FailedDepotsStateKey = "failedDepotResolutions";

    public class DetectionOperation
    {
        public string OperationId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string Status { get; set; } = "running"; // running, complete, failed
        public string? Message { get; set; }
        public string? StatusMessage => Message; // Alias for frontend compatibility
        public double PercentComplete { get; set; }
        public string ScanType { get; set; } = "incremental"; // "full" or "incremental"
        public List<GameCacheInfo>? Games { get; set; }
        public List<ServiceCacheInfo>? Services { get; set; }
        public int TotalGamesDetected { get; set; }
        public int TotalServicesDetected { get; set; }
        public string? Error { get; set; }
    }

    public GameCacheDetectionService(
        ILogger<GameCacheDetectionService> logger,
        IPathResolver pathResolver,
        OperationStateService operationStateService,
        IDbContextFactory<AppDbContext> dbContextFactory,
        SteamKit2Service steamKit2Service,
        ProcessManager processManager,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _operationStateService = operationStateService;
        _dbContextFactory = dbContextFactory;
        _steamKit2Service = steamKit2Service;
        _processManager = processManager;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _datasourceService = datasourceService;

        _logger.LogInformation("GameCacheDetectionService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        // Restore any interrupted operations on startup
        RestoreInterruptedOperations();
    }

    public async Task<string?> StartDetectionAsync(bool incremental = true)
    {
        await _startLock.WaitAsync();
        try
        {
            // Check if there's already an active detection
            var activeOp = _operations.Values.FirstOrDefault(o => o.Status == "running");
            if (activeOp != null)
            {
                _logger.LogWarning("[GameDetection] Detection already in progress: {OperationId}", activeOp.OperationId);
                return null; // Return null to indicate operation already running
            }

            // Cancel any existing token source and create a new one
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            var operationId = Guid.NewGuid().ToString();
            var scanType = incremental ? "incremental" : "full";
            var message = incremental
                ? "Starting incremental scan (new games and services only)..."
                : "Starting full scan (all games and services)...";

            var operation = new DetectionOperation
            {
                OperationId = operationId,
                StartTime = DateTime.UtcNow,
                Status = "running",
                Message = message,
                ScanType = scanType,
                PercentComplete = 0
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

            // Send SignalR notification that detection started
            _ = Task.Run(async () =>
            {
                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionStarted, new
                {
                    operationId,
                    scanType,
                    message,
                    timestamp = DateTime.UtcNow
                });
            });

            // Start detection in background with cancellation token
            var cancellationToken = _cancellationTokenSource.Token;
            _ = Task.Run(async () => await RunDetectionAsync(operationId, incremental, cancellationToken), cancellationToken);

            return operationId;
        }
        finally
        {
            _startLock.Release();
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

    private async Task RunDetectionAsync(string operationId, bool incremental, CancellationToken cancellationToken = default)
    {
        if (!_operations.TryGetValue(operationId, out var operation))
        {
            return;
        }

        string? excludedIdsPath = null;
        List<GameCacheInfo>? existingGames = null;
        var outputJsonFiles = new List<string>();

        // Helper to send progress notification
        async Task SendProgressAsync(string status, string message, int gamesDetected = 0, int servicesDetected = 0, double progressPercent = 0)
        {
            operation.Message = message;
            operation.PercentComplete = progressPercent;
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
            {
                operationId,
                status,
                message,
                gamesDetected,
                servicesDetected,
                progressPercent
            });
        }

        try
        {
            _logger.LogInformation("[GameDetection] Starting detection for operation {OperationId} (incremental={Incremental})", operationId, incremental);

            // Check for cancellation at start
            cancellationToken.ThrowIfCancellationRequested();

            // Smart pre-check: If incremental scan and we have 3+ unknown games, try applying mappings first
            if (incremental)
            {
                await ApplyMappingsPreCheckAsync(operation);
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Game cache detector");

            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException($"Database not found at {dbPath}");
            }

            // Get all datasources
            var datasources = _datasourceService.GetDatasources();
            _logger.LogInformation("[GameDetection] Scanning {Count} datasource(s)", datasources.Count);

            // Send initial progress
            await SendProgressAsync("preparing", "Preparing detection scan...", 0, 0, 5);

            // Prepare excluded IDs for incremental scans
            List<ServiceCacheInfo>? existingServices = null;
            if (incremental)
            {
                // Progress: Checking for new games
                await SendProgressAsync("preparing", "Checking for new games...", 0, 0, 15);

                // Load existing games and services from database
                await using (var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken))
                {
                    var cachedGames = await dbContext.CachedGameDetections.ToListAsync(cancellationToken);
                    if (cachedGames.Count > 0)
                    {
                        // Convert database records to GameCacheInfo
                        existingGames = cachedGames.Select(ConvertToGameCacheInfo).ToList();
                        var excludedGameIds = existingGames.Select(g => g.GameAppId).ToList();

                        excludedIdsPath = Path.Combine(operationsDir, $"excluded_game_ids_{operationId}.json");
                        var excludedIdsJson = JsonSerializer.Serialize(excludedGameIds);
                        File.WriteAllText(excludedIdsPath, excludedIdsJson);

                        _logger.LogInformation("[GameDetection] Incremental scan: excluding {ExcludedCount} already-detected games", excludedGameIds.Count);
                        await SendProgressAsync("preparing", $"Scanning for new games (skipping {excludedGameIds.Count} already detected)...", existingGames.Count, 0, 25);
                    }
                    else
                    {
                        _logger.LogInformation("[GameDetection] No cached results found, performing full scan");
                        await SendProgressAsync("preparing", "No cached results found, performing full scan...", 0, 0, 25);
                    }

                    // Load existing services for incremental mode (we skip service scanning in incremental mode)
                    var cachedServices = await dbContext.CachedServiceDetections.ToListAsync(cancellationToken);
                    if (cachedServices.Count > 0)
                    {
                        existingServices = cachedServices.Select(ConvertToServiceCacheInfo).ToList();
                        _logger.LogInformation("[GameDetection] Incremental scan: preserving {ServiceCount} existing services", existingServices.Count);
                    }
                }
            }
            else
            {
                _logger.LogInformation("[GameDetection] Force refresh: performing full scan");
                await SendProgressAsync("preparing", "Preparing full scan...", 0, 0, 15);
            }

            // Check for cancellation before scanning
            cancellationToken.ThrowIfCancellationRequested();

            // Progress: About to scan datasources
            await SendProgressAsync("scanning", "Scanning datasources...", 0, 0, 30);

            // Aggregate results from all datasources
            var aggregatedGames = new List<GameCacheInfo>();
            var aggregatedServices = new List<ServiceCacheInfo>();
            var gameAppIdSet = new HashSet<uint>(); // Track unique game app IDs across datasources
            var serviceNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Track unique services

            // Scan each datasource
            var datasourceIndex = 0;
            foreach (var datasource in datasources)
            {
                // Check for cancellation before each datasource
                cancellationToken.ThrowIfCancellationRequested();

                var cachePath = datasource.CachePath;
                var outputJson = Path.Combine(operationsDir, $"game_detection_{operationId}_{datasource.Name}.json");
                outputJsonFiles.Add(outputJson);

                _logger.LogInformation("[GameDetection] Scanning datasource '{DatasourceName}': {CachePath}", datasource.Name, cachePath);
                
                var scanMessage = datasources.Count > 1
                    ? $"Scanning datasource '{datasource.Name}'..."
                    : "Scanning database and cache directory...";
                
                // Calculate progress: 30-70% for scanning (40% range divided by datasource count)
                var progressBase = 30 + (40.0 * datasourceIndex / datasources.Count);
                await SendProgressAsync("scanning", scanMessage, aggregatedGames.Count, aggregatedServices.Count, progressBase);

                // Build arguments
                // Add --incremental flag for quick scans to skip the expensive cache directory scan
                var incrementalFlag = incremental ? " --incremental" : "";
                string arguments = !string.IsNullOrEmpty(excludedIdsPath)
                    ? $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\" \"{excludedIdsPath}\"{incrementalFlag}"
                    : $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\"{incrementalFlag}";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

                var result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, cancellationToken);

                // Log diagnostic output from stderr (contains scan progress and stats)
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    _logger.LogInformation("[GameDetection] Diagnostic output for '{DatasourceName}':\n{DiagnosticOutput}",
                        datasource.Name, result.Error);
                }

                if (result.ExitCode != 0)
                {
                    _logger.LogError("[GameDetection] Process failed for datasource '{DatasourceName}' with exit code {ExitCode}. Error: {Error}",
                        datasource.Name, result.ExitCode, result.Error);
                    throw new Exception($"Game detection failed for datasource '{datasource.Name}': {result.Error}");
                }

                // Read results from JSON
                if (!File.Exists(outputJson))
                {
                    throw new FileNotFoundException($"Output file not found: {outputJson}");
                }

                var json = await File.ReadAllTextAsync(outputJson, cancellationToken);
                var detectionResult = JsonSerializer.Deserialize<GameDetectionResult>(json);

                if (detectionResult == null)
                {
                    throw new Exception($"Failed to parse detection results for datasource '{datasource.Name}'");
                }

                // Aggregate games (deduplicate by GameAppId)
                // Progress range for game aggregation: from current progress to next datasource's base
                var totalGamesInResult = detectionResult.Games.Count;
                var gameIndex = 0;
                var lastProgressUpdate = 0;
                
                // Calculate progress range for this datasource's game processing
                // Games processing takes 30% of the datasource's share (from base to base + 30% of share)
                var datasourceProgressShare = 40.0 / datasources.Count;
                var gameProcessingStart = progressBase;
                var gameProcessingEnd = progressBase + (datasourceProgressShare * 0.75); // 75% of datasource share for game processing
                
                foreach (var game in detectionResult.Games)
                {
                    if (!gameAppIdSet.Contains(game.GameAppId))
                    {
                        gameAppIdSet.Add(game.GameAppId);
                        // Initialize datasources list with current datasource
                        game.Datasources = new List<string> { datasource.Name };
                        aggregatedGames.Add(game);
                    }
                    else
                    {
                        // Game already found in another datasource - merge cache file info
                        var existingGame = aggregatedGames.First(g => g.GameAppId == game.GameAppId);
                        existingGame.CacheFilesFound += game.CacheFilesFound;
                        existingGame.TotalSizeBytes += game.TotalSizeBytes;
                        existingGame.CacheFilePaths.AddRange(game.CacheFilePaths);
                        existingGame.SampleUrls.AddRange(game.SampleUrls.Take(5 - existingGame.SampleUrls.Count));
                        foreach (var depotId in game.DepotIds)
                        {
                            if (!existingGame.DepotIds.Contains(depotId))
                            {
                                existingGame.DepotIds.Add(depotId);
                            }
                        }
                        // Track that this game was also found in this datasource
                        if (!existingGame.Datasources.Contains(datasource.Name))
                        {
                            existingGame.Datasources.Add(datasource.Name);
                        }
                    }
                    
                    gameIndex++;
                    
                    // Send progress updates every 5 games or every 10% of total games
                    var progressThreshold = Math.Max(5, totalGamesInResult / 10);
                    if (gameIndex - lastProgressUpdate >= progressThreshold || gameIndex == totalGamesInResult)
                    {
                        lastProgressUpdate = gameIndex;
                        var gameProgress = gameProcessingStart + ((gameProcessingEnd - gameProcessingStart) * gameIndex / totalGamesInResult);
                        await SendProgressAsync(
                            "scanning",
                            $"Processing games ({gameIndex}/{totalGamesInResult})...",
                            aggregatedGames.Count,
                            aggregatedServices.Count,
                            gameProgress);
                    }
                }

                // Aggregate services (deduplicate by ServiceName)
                foreach (var service in detectionResult.Services)
                {
                    if (!serviceNameSet.Contains(service.ServiceName))
                    {
                        serviceNameSet.Add(service.ServiceName);
                        // Initialize datasources list with current datasource
                        service.Datasources = new List<string> { datasource.Name };
                        aggregatedServices.Add(service);
                    }
                    else
                    {
                        // Service already found in another datasource - merge cache file info
                        var existingService = aggregatedServices.First(s =>
                            s.ServiceName.Equals(service.ServiceName, StringComparison.OrdinalIgnoreCase));
                        existingService.CacheFilesFound += service.CacheFilesFound;
                        existingService.TotalSizeBytes += service.TotalSizeBytes;
                        existingService.CacheFilePaths.AddRange(service.CacheFilePaths);
                        existingService.SampleUrls.AddRange(service.SampleUrls.Take(5 - existingService.SampleUrls.Count));
                        // Track that this service was also found in this datasource
                        if (!existingService.Datasources.Contains(datasource.Name))
                        {
                            existingService.Datasources.Add(datasource.Name);
                        }
                    }
                }

                _logger.LogInformation("[GameDetection] Datasource '{DatasourceName}' found {GameCount} games, {ServiceCount} services",
                    datasource.Name, detectionResult.Games.Count, detectionResult.Services.Count);
                
                datasourceIndex++;
            }

            // Check for cancellation before finalizing
            cancellationToken.ThrowIfCancellationRequested();

            // Progress: Datasource scanning complete
            await SendProgressAsync("processing", "Merging results...", aggregatedGames.Count, aggregatedServices.Count, 70);

            // Send progress for applying depot mappings
            await SendProgressAsync("applying_mappings", "Applying depot mappings...", aggregatedGames.Count, aggregatedServices.Count, 85);

            // Merge with existing games if this was an incremental scan
            List<GameCacheInfo> finalGames;
            int totalGamesDetected;
            int newGamesCount = aggregatedGames.Count;

            if (incremental && existingGames != null && existingGames.Count > 0)
            {
                // Merge existing games with newly detected games
                finalGames = existingGames.ToList();
                finalGames.AddRange(aggregatedGames);
                totalGamesDetected = finalGames.Count;

                _logger.LogInformation("[GameDetection] Incremental scan complete: {NewCount} new games, {TotalCount} total",
                    newGamesCount, totalGamesDetected);

                operation.Message = newGamesCount > 0
                    ? $"Found {newGamesCount} new game{(newGamesCount != 1 ? "s" : "")} ({totalGamesDetected} total with cache files)"
                    : $"No new games detected ({totalGamesDetected} total with cache files)";
            }
            else
            {
                finalGames = aggregatedGames;
                totalGamesDetected = aggregatedGames.Count;
                operation.Message = $"Detected {totalGamesDetected} games with cache files";
            }

            // Add datasource info to message if multiple datasources
            if (datasources.Count > 1)
            {
                operation.Message += $" across {datasources.Count} datasources";
            }

            operation.Status = "complete";
            operation.Games = finalGames;
            operation.TotalGamesDetected = totalGamesDetected;

            // For incremental mode, use existing services (service detection is skipped)
            // For full scan, use newly detected services
            var finalServices = incremental && existingServices != null && existingServices.Count > 0
                ? existingServices
                : aggregatedServices;

            operation.Services = finalServices;
            operation.TotalServicesDetected = finalServices.Count;

            // Send progress for saving to database
            await SendProgressAsync("saving", "Saving results to database...", totalGamesDetected, finalServices.Count, 90);

            // Save results to database (replaces in-memory cache)
            await SaveGamesToDatabaseAsync(finalGames, incremental);
            _logger.LogInformation("[GameDetection] Results saved to database - {Count} games total", totalGamesDetected);

            // For incremental scans, resolve any unknown games that now have depot mappings
            // Full scans already query fresh mappings, so this is only needed for incremental
            if (incremental)
            {
                var resolvedCount = await ResolveUnknownGamesInCacheAsync();
                if (resolvedCount > 0)
                {
                    _logger.LogInformation("[GameDetection] Resolved {Count} unknown games after incremental scan", resolvedCount);
                    operation.Message += $" (resolved {resolvedCount} previously unknown)";
                }
            }

            // Save services to database (only for full scan, incremental preserves existing)
            if (!incremental && aggregatedServices.Count > 0)
            {
                await SaveServicesToDatabaseAsync(aggregatedServices);
                _logger.LogInformation("[GameDetection] Services saved to database - {Count} services total", aggregatedServices.Count);
            }

            // Update persisted state with complete status
            _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
            {
                Key = $"gameDetection_{operationId}",
                Type = "gameDetection",
                Status = "complete",
                Message = operation.Message,
                Data = JsonSerializer.SerializeToElement(new { operationId, totalGamesDetected })
            });

            _logger.LogInformation("[GameDetection] Completed: {Count} games detected across {DatasourceCount} datasource(s)",
                totalGamesDetected, datasources.Count);

            // Send SignalR notification that detection completed successfully
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, new
            {
                success = true,
                operationId,
                totalGamesDetected,
                totalServicesDetected = finalServices.Count,
                message = operation.Message,
                timestamp = DateTime.UtcNow
            });

            // Clean up temporary files
            foreach (var outputJson in outputJsonFiles)
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(outputJson);
            }
            if (!string.IsNullOrEmpty(excludedIdsPath))
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(excludedIdsPath);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GameDetection] Operation {OperationId} was cancelled", operationId);
            operation.Status = "cancelled";
            operation.Message = "Detection cancelled by user";

            // Update persisted state with cancelled status
            _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
            {
                Key = $"gameDetection_{operationId}",
                Type = "gameDetection",
                Status = "cancelled",
                Message = operation.Message,
                Data = JsonSerializer.SerializeToElement(new { operationId, cancelled = true })
            });

            // Send SignalR notification that detection was cancelled
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, new
            {
                success = false,
                operationId,
                message = operation.Message,
                cancelled = true,
                timestamp = DateTime.UtcNow
            });

            // Clean up temporary files
            foreach (var outputJson in outputJsonFiles)
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(outputJson);
            }
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

            // Send SignalR notification that detection failed
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, new
            {
                success = false,
                operationId,
                message = operation.Message,
                error = ex.Message,
                timestamp = DateTime.UtcNow
            });

            // Clean up temporary files in error path too
            foreach (var outputJson in outputJsonFiles)
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(outputJson);
            }
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


    public DetectionOperation? GetActiveOperation()
    {
        return _operations.Values.FirstOrDefault(op => op.Status == "running");
    }

    /// <summary>
    /// Cancel the currently running detection operation.
    /// </summary>
    public void CancelDetection()
    {
        var activeOp = GetActiveOperation();
        if (activeOp != null)
        {
            _logger.LogInformation("[GameDetection] Cancelling detection operation {OperationId}", activeOp.OperationId);
            _cancellationTokenSource?.Cancel();
            activeOp.Status = "cancelled";
            activeOp.Message = "Detection cancelled by user";
        }
    }

    public async Task<DetectionOperation?> GetCachedDetectionAsync()
    {
        // First, try to resolve any unknown games in the cache using available mappings
        var resolvedCount = await ResolveUnknownGamesInCacheAsync();
        if (resolvedCount > 0)
        {
            _logger.LogInformation("[GameDetection] Auto-resolved {Count} unknown games when loading cache", resolvedCount);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var cachedGames = await dbContext.CachedGameDetections.ToListAsync();
        var cachedServices = await dbContext.CachedServiceDetections.ToListAsync();

        if (cachedGames.Count == 0 && cachedServices.Count == 0)
        {
            return null;
        }

        var games = cachedGames.Select(ConvertToGameCacheInfo).ToList();
        var services = cachedServices.Select(ConvertToServiceCacheInfo).ToList();

        var lastDetectedTime = DateTime.MinValue;
        if (cachedGames.Count > 0)
        {
            lastDetectedTime = cachedGames.Max(g => g.LastDetectedUtc);
        }
        if (cachedServices.Count > 0)
        {
            var servicesMaxTime = cachedServices.Max(s => s.LastDetectedUtc);
            if (servicesMaxTime > lastDetectedTime)
            {
                lastDetectedTime = servicesMaxTime;
            }
        }

        var message = games.Count > 0 && services.Count > 0
            ? $"Loaded {games.Count} games and {services.Count} services from cache"
            : games.Count > 0
                ? $"Loaded {games.Count} games from cache"
                : $"Loaded {services.Count} services from cache";

        return new DetectionOperation
        {
            OperationId = "cached",
            StartTime = lastDetectedTime,
            Status = "complete",
            Message = message,
            Games = games,
            Services = services,
            TotalGamesDetected = games.Count,
            TotalServicesDetected = services.Count
        };
    }

    public async Task InvalidateCacheAsync()
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        await dbContext.CachedGameDetections.ExecuteDeleteAsync();
        await dbContext.CachedServiceDetections.ExecuteDeleteAsync();
        await dbContext.SaveChangesAsync();
        _logger.LogInformation("[GameDetection] Cache invalidated - all cached games and services deleted from database");
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

    public async Task RemoveServiceFromCacheAsync(string serviceName)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();
        var service = await dbContext.CachedServiceDetections
            .FirstOrDefaultAsync(s => s.ServiceName.ToLower() == serviceName.ToLower());

        if (service != null)
        {
            dbContext.CachedServiceDetections.Remove(service);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("[GameDetection] Removed service '{ServiceName}' from cache", serviceName);
        }
    }

    /// <summary>
    /// Resolve unknown games in the cache by looking up their depot IDs in SteamDepotMappings.
    /// This updates cached "Unknown Game (Depot X)" entries when mappings become available.
    /// Returns the number of games that were resolved.
    /// </summary>
    public async Task<int> ResolveUnknownGamesInCacheAsync()
    {
        try
        {
            await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            // Get unknown games from cache
            var unknownGames = await dbContext.CachedGameDetections
                .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
                .ToListAsync();

            if (unknownGames.Count == 0)
            {
                return 0;
            }

            // Get previously failed depots from state service
            var failedDepotsState = _operationStateService.GetState(FailedDepotsStateKey);
            var previouslyFailedDepots = new HashSet<uint>();
            if (failedDepotsState?.Data != null)
            {
                var dataDict = failedDepotsState.GetDataAsDictionary();
                if (dataDict.TryGetValue("depotIds", out var depotIdsObj) && depotIdsObj is List<object> depotIdsList)
                {
                    foreach (var id in depotIdsList)
                    {
                        if (id is long longId)
                            previouslyFailedDepots.Add((uint)longId);
                        else if (id is int intId)
                            previouslyFailedDepots.Add((uint)intId);
                        else if (uint.TryParse(id?.ToString(), out var parsedId))
                            previouslyFailedDepots.Add(parsedId);
                    }
                }
            }

            // Filter out games we've already failed to resolve
            var gamesToResolve = unknownGames.Where(g => !previouslyFailedDepots.Contains(g.GameAppId)).ToList();

            if (gamesToResolve.Count == 0)
            {
                return 0;
            }

            _logger.LogInformation("[GameDetection] Found {Count} unknown games in cache, attempting to resolve", gamesToResolve.Count);

            int resolvedCount = 0;
            var newlyFailedDepots = new List<uint>();
            var entriesToRemove = new List<CachedGameDetection>();
            
            // Track AppIds we've already resolved to in this batch to prevent UNIQUE constraint violations
            // Key: AppId, Value: the CachedGameDetection entity that will have this AppId after save
            var pendingAppIdAssignments = new Dictionary<uint, CachedGameDetection>();

            foreach (var unknownGame in gamesToResolve)
            {
                // For unknown games, the depot ID is stored as GameAppId
                var depotId = unknownGame.GameAppId;

                // Look up the depot in SteamDepotMappings
                var mapping = await dbContext.SteamDepotMappings
                    .Where(m => m.DepotId == depotId && m.IsOwner)
                    .FirstOrDefaultAsync();

                if (mapping != null)
                {
                    // Found a mapping! Determine the best name to use
                    var resolvedName = !string.IsNullOrEmpty(mapping.AppName)
                        ? mapping.AppName
                        : !string.IsNullOrEmpty(mapping.DepotName)
                            ? mapping.DepotName
                            : $"App {mapping.AppId}";

                    _logger.LogInformation("[GameDetection] Resolved depot {DepotId} -> {AppId} ({Name})",
                        depotId, mapping.AppId, resolvedName);

                    // Check if we've already assigned this AppId in this batch
                    if (pendingAppIdAssignments.TryGetValue(mapping.AppId, out var pendingGame))
                    {
                        // Another unknown game in this batch already resolved to this AppId - merge into it
                        pendingGame.CacheFilesFound += unknownGame.CacheFilesFound;
                        pendingGame.TotalSizeBytes += unknownGame.TotalSizeBytes;
                        pendingGame.LastDetectedUtc = pendingGame.LastDetectedUtc > unknownGame.LastDetectedUtc
                            ? pendingGame.LastDetectedUtc : unknownGame.LastDetectedUtc;

                        // Merge depot IDs
                        var pendingDepots = JsonSerializer.Deserialize<List<uint>>(pendingGame.DepotIdsJson) ?? new List<uint>();
                        var unknownDepots = JsonSerializer.Deserialize<List<uint>>(unknownGame.DepotIdsJson) ?? new List<uint>();
                        pendingDepots.AddRange(unknownDepots);
                        pendingDepots = pendingDepots.Distinct().ToList();
                        pendingGame.DepotIdsJson = JsonSerializer.Serialize(pendingDepots);

                        // Merge cache file paths
                        var pendingPaths = JsonSerializer.Deserialize<List<string>>(pendingGame.CacheFilePathsJson) ?? new List<string>();
                        var unknownPaths = JsonSerializer.Deserialize<List<string>>(unknownGame.CacheFilePathsJson) ?? new List<string>();
                        pendingPaths.AddRange(unknownPaths);
                        pendingPaths = pendingPaths.Distinct().ToList();
                        pendingGame.CacheFilePathsJson = JsonSerializer.Serialize(pendingPaths);

                        // Mark unknown game for removal
                        entriesToRemove.Add(unknownGame);
                        _logger.LogInformation("[GameDetection] Merged depot {DepotId} into pending game {AppId} ({Name})",
                            depotId, mapping.AppId, resolvedName);
                        
                        resolvedCount++;
                        previouslyFailedDepots.Remove(depotId);
                        continue;
                    }

                    // Check if a record with this AppId already exists in database (unique constraint protection)
                    var existingGame = await dbContext.CachedGameDetections
                        .FirstOrDefaultAsync(g => g.GameAppId == mapping.AppId);

                    if (existingGame != null && existingGame.Id != unknownGame.Id)
                    {
                        // Merge data into existing record - this depot is another depot for the same game
                        existingGame.CacheFilesFound += unknownGame.CacheFilesFound;
                        existingGame.TotalSizeBytes += unknownGame.TotalSizeBytes;
                        existingGame.LastDetectedUtc = existingGame.LastDetectedUtc > unknownGame.LastDetectedUtc
                            ? existingGame.LastDetectedUtc : unknownGame.LastDetectedUtc;

                        // Merge depot IDs
                        var existingDepots = JsonSerializer.Deserialize<List<uint>>(existingGame.DepotIdsJson) ?? new List<uint>();
                        var unknownDepots = JsonSerializer.Deserialize<List<uint>>(unknownGame.DepotIdsJson) ?? new List<uint>();
                        existingDepots.AddRange(unknownDepots);
                        existingDepots = existingDepots.Distinct().ToList();
                        existingGame.DepotIdsJson = JsonSerializer.Serialize(existingDepots);

                        // Merge cache file paths
                        var existingPaths = JsonSerializer.Deserialize<List<string>>(existingGame.CacheFilePathsJson) ?? new List<string>();
                        var unknownPaths = JsonSerializer.Deserialize<List<string>>(unknownGame.CacheFilePathsJson) ?? new List<string>();
                        existingPaths.AddRange(unknownPaths);
                        existingPaths = existingPaths.Distinct().ToList();
                        existingGame.CacheFilePathsJson = JsonSerializer.Serialize(existingPaths);

                        // Mark unknown game for removal
                        entriesToRemove.Add(unknownGame);
                        
                        // Track this existing game so future unknowns in this batch merge into it
                        pendingAppIdAssignments[mapping.AppId] = existingGame;
                        
                        _logger.LogInformation("[GameDetection] Merged depot {DepotId} into existing game {AppId} ({Name})",
                            depotId, mapping.AppId, resolvedName);
                    }
                    else
                    {
                        // No existing record, update the unknown game directly
                        unknownGame.GameName = resolvedName;
                        unknownGame.GameAppId = mapping.AppId;
                        
                        // Track this assignment so other unknowns in this batch can merge
                        pendingAppIdAssignments[mapping.AppId] = unknownGame;
                    }

                    resolvedCount++;

                    // If this depot was previously in failed list, it's now resolved - remove it
                    previouslyFailedDepots.Remove(depotId);
                }
                else
                {
                    // Failed to resolve - track this depot so we don't spam logs
                    newlyFailedDepots.Add(depotId);
                }
            }

            // Remove merged entries
            if (entriesToRemove.Count > 0)
            {
                dbContext.CachedGameDetections.RemoveRange(entriesToRemove);
                _logger.LogInformation("[GameDetection] Removed {Count} duplicate entries after merging", entriesToRemove.Count);
            }

            if (resolvedCount > 0)
            {
                await dbContext.SaveChangesAsync();
                _logger.LogInformation("[GameDetection] Resolved {Count} unknown games in cache", resolvedCount);
            }

            // Log and persist failed resolutions to state.json
            if (newlyFailedDepots.Count > 0)
            {
                _logger.LogInformation("[GameDetection] Failed to resolve {Count} depot(s): {DepotIds}. Marked as Unknown - will retry in 24 hours",
                    newlyFailedDepots.Count, string.Join(", ", newlyFailedDepots));
            }

            // Update state with all failed depots (previous + new, minus any resolved)
            var allFailedDepots = previouslyFailedDepots.Union(newlyFailedDepots.Select(d => d)).ToList();
            if (allFailedDepots.Count > 0 || failedDepotsState != null)
            {
                _operationStateService.SaveState(FailedDepotsStateKey, new OperationState
                {
                    Key = FailedDepotsStateKey,
                    Type = "failedDepotResolutions",
                    Status = "tracked",
                    Message = $"{allFailedDepots.Count} depot(s) could not be resolved",
                    Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                    {
                        { "depotIds", allFailedDepots },
                        { "lastAttempt", DateTime.UtcNow.ToString("o") }
                    })
                });
            }

            return resolvedCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Failed to resolve unknown games in cache");
            return 0;
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

        // Deduplicate games by GameAppId (keep last occurrence in case of duplicates)
        var uniqueGames = games
            .GroupBy(g => g.GameAppId)
            .Select(group => group.Last())
            .ToList();

        if (uniqueGames.Count < games.Count)
        {
            _logger.LogWarning(
                "[GameDetection] Removed {DuplicateCount} duplicate GameAppIds from detection results",
                games.Count - uniqueGames.Count
            );
        }

        var now = DateTime.UtcNow;

        foreach (var game in uniqueGames)
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
                DatasourcesJson = JsonSerializer.Serialize(game.Datasources),
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
                existing.DatasourcesJson = cachedGame.DatasourcesJson;
                existing.LastDetectedUtc = now;
            }
            else
            {
                dbContext.CachedGameDetections.Add(cachedGame);
            }
        }

        try
        {
            await dbContext.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sqliteEx && sqliteEx.SqliteErrorCode == 19)
        {
            // UNIQUE constraint violation - log warning and continue
            // This can happen in rare race conditions
            _logger.LogWarning(
                ex,
                "[GameDetection] UNIQUE constraint error when saving games - some records may already exist. Continuing..."
            );
        }
    }

    private async Task SaveServicesToDatabaseAsync(List<ServiceCacheInfo> services)
    {
        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Always clear existing services on detection (services don't support incremental)
        await dbContext.CachedServiceDetections.ExecuteDeleteAsync();

        var now = DateTime.UtcNow;

        foreach (var service in services)
        {
            var cachedService = new CachedServiceDetection
            {
                ServiceName = service.ServiceName,
                CacheFilesFound = service.CacheFilesFound,
                TotalSizeBytes = service.TotalSizeBytes,
                SampleUrlsJson = JsonSerializer.Serialize(service.SampleUrls),
                CacheFilePathsJson = JsonSerializer.Serialize(service.CacheFilePaths),
                DatasourcesJson = JsonSerializer.Serialize(service.Datasources),
                LastDetectedUtc = now,
                CreatedAtUtc = now
            };

            dbContext.CachedServiceDetections.Add(cachedService);
        }

        await dbContext.SaveChangesAsync();
    }

    private static GameCacheInfo ConvertToGameCacheInfo(CachedGameDetection cached)
    {
        var datasourcesJson = string.IsNullOrWhiteSpace(cached.DatasourcesJson) ? "[]" : cached.DatasourcesJson;
        return new GameCacheInfo
        {
            GameAppId = cached.GameAppId,
            GameName = cached.GameName,
            CacheFilesFound = cached.CacheFilesFound,
            TotalSizeBytes = cached.TotalSizeBytes,
            DepotIds = JsonSerializer.Deserialize<List<uint>>(cached.DepotIdsJson) ?? new List<uint>(),
            SampleUrls = JsonSerializer.Deserialize<List<string>>(cached.SampleUrlsJson) ?? new List<string>(),
            CacheFilePaths = JsonSerializer.Deserialize<List<string>>(cached.CacheFilePathsJson) ?? new List<string>(),
            Datasources = JsonSerializer.Deserialize<List<string>>(datasourcesJson) ?? new List<string>()
        };
    }

    private static ServiceCacheInfo ConvertToServiceCacheInfo(CachedServiceDetection cached)
    {
        var datasourcesJson = string.IsNullOrWhiteSpace(cached.DatasourcesJson) ? "[]" : cached.DatasourcesJson;
        return new ServiceCacheInfo
        {
            ServiceName = cached.ServiceName,
            CacheFilesFound = cached.CacheFilesFound,
            TotalSizeBytes = cached.TotalSizeBytes,
            SampleUrls = JsonSerializer.Deserialize<List<string>>(cached.SampleUrlsJson) ?? new List<string>(),
            CacheFilePaths = JsonSerializer.Deserialize<List<string>>(cached.CacheFilePathsJson) ?? new List<string>(),
            Datasources = JsonSerializer.Deserialize<List<string>>(datasourcesJson) ?? new List<string>()
        };
    }

    private class GameDetectionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("total_games_detected")]
        public int TotalGamesDetected { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("total_services_detected")]
        public int TotalServicesDetected { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("games")]
        public List<GameCacheInfo> Games { get; set; } = new();

        [System.Text.Json.Serialization.JsonPropertyName("services")]
        public List<ServiceCacheInfo> Services { get; set; } = new();
    }
}
