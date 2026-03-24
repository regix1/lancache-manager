using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Core.Services.SteamKit2;

namespace LancacheManager.Core.Services;

/// <summary>
/// Service for running game cache detection as a background operation
/// </summary>
public class GameCacheDetectionService : IDisposable
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
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private string? _currentTrackerOperationId;
    private bool _disposed;
    private const string FailedDepotsStateKey = "failedDepotResolutions";

    /// <summary>
    /// Response DTO that preserves the JSON shape expected by the frontend.
    /// Built from OperationInfo + GameDetectionMetrics metadata.
    /// </summary>
    public class DetectionOperationResponse
    {
        public string OperationId { get; set; } = string.Empty;
        public DateTime StartTime { get; set; }
        public string Status { get; set; } = "running";
        public string? Message { get; set; }
        public string? StatusMessage => Message; // Alias for frontend compatibility
        public double PercentComplete { get; set; }
        public string ScanType { get; set; } = "incremental";
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
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
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
        _operationTracker = operationTracker;

        _logger.LogInformation("GameCacheDetectionService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        // Restore any interrupted operations on startup
        RestoreInterruptedOperations();
    }

    public async Task<string?> StartDetectionAsync(bool incremental = true)
    {
        await _startLock.WaitAsync();
        try
        {
            // Clean up stale operations (running for more than 30 minutes)
            var staleOperations = _operationTracker.GetActiveOperations(OperationType.GameDetection)
                .Where(op => op.StartedAt < DateTime.UtcNow.AddMinutes(-30))
                .ToList();
            foreach (var stale in staleOperations)
            {
                _logger.LogWarning("Cleaning up stale operation {OperationId} that started at {StartTime}", stale.Id, stale.StartedAt);
                _operationTracker.CompleteOperation(stale.Id, success: false, error: "Stale operation cleaned up");
            }

            // Check if there's already an active detection
            var activeOp = _operationTracker.GetActiveOperations(OperationType.GameDetection).FirstOrDefault();
            if (activeOp != null)
            {
                _logger.LogWarning("[GameDetection] Detection already in progress: {OperationId}", activeOp.Id);
                return null; // Return null to indicate operation already running
            }

            // Create a new cancellation token source
            // Note: Don't cancel/dispose old one here - it may have been disposed by CompleteOperation
            _cancellationTokenSource = new CancellationTokenSource();

            var scanType = incremental ? "incremental" : "full";
            var message = incremental
                ? "Starting incremental scan (new games and services only)..."
                : "Starting full scan (all games and services)...";

            // Register with unified operation tracker for centralized cancellation
            var metadata = new GameDetectionMetrics { ScanType = scanType };
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.GameDetection,
                "Game Detection",
                _cancellationTokenSource,
                metadata
            );
            var operationId = _currentTrackerOperationId;

            // Set initial progress message
            _operationTracker.UpdateProgress(operationId, 0, message);

            // Save to OperationStateService for persistence
            _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
            {
                Key = $"gameDetection_{operationId}",
                Type = "gameDetection",
                Status = "running",
                Message = message,
                Data = JsonSerializer.SerializeToElement(new { operationId })
            });

            // Send SignalR notification that detection started
            _ = Task.Run(async () =>
            {
                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionStarted, new
                {
                    OperationId = operationId,
                    Message = message,
                    scanType,
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
            var unknownGames = await GetUnknownGamesCachedAsync(dbContext);

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
    private async Task<bool> ApplyMappingsPreCheckAsync(string operationId)
    {
        try
        {
            var (totalUnknowns, nowMapped) = await CheckUnknownDepotsForMappingsAsync();

            if (totalUnknowns >= 3 && nowMapped > 0)
            {
                _logger.LogInformation("[GameDetection] Found {UnknownCount} unknown games in cache, {MappedCount} now have depot mappings available",
                    totalUnknowns, nowMapped);
                _operationTracker.UpdateProgress(operationId, 0, $"Found {nowMapped} new depot mapping(s) for unknown games - invalidating cache...");

                // Invalidate cache so the scan will pick up the new mappings
                await InvalidateCacheAsync();

                _logger.LogInformation("[GameDetection] Cache invalidated - fresh scan will use new depot mappings");
                _operationTracker.UpdateProgress(operationId, 0, $"Cache invalidated - scanning with {nowMapped} new mapping(s)...");
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
        var trackerOp = _operationTracker.GetOperation(operationId);
        if (trackerOp == null)
        {
            return;
        }

        string? excludedIdsPath = null;
        List<GameCacheInfo>? existingGames = null;
        var outputJsonFiles = new List<string>();
        string? progressFilePath = null;

        // Helper to send progress notification
        async Task SendProgressAsync(string status, string message, int gamesDetected = 0, int servicesDetected = 0, double progressPercent = 0)
        {
            _operationTracker.UpdateProgress(operationId, progressPercent, message);
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
            {
                OperationId = operationId,
                PercentComplete = progressPercent,
                Status = OperationStatus.Running,
                Message = message,
                gamesDetected,
                servicesDetected
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
                await ApplyMappingsPreCheckAsync(operationId);
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var dbPath = _pathResolver.GetDatabasePath();
            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();

            _rustProcessHelper.ValidateRustBinaryExists(rustBinaryPath, "Game cache detector");

            // Get all datasources
            var datasources = _datasourceService.GetDatasources();
            _logger.LogInformation("[GameDetection] Scanning {Count} datasource(s)", datasources.Count);

            // Prepare excluded IDs for incremental scans
            List<ServiceCacheInfo>? existingServices = null;
            if (incremental)
            {
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
                    }
                    else
                    {
                        _logger.LogInformation("[GameDetection] No cached results found, performing full scan");
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
            }

            // Check for cancellation before scanning
            cancellationToken.ThrowIfCancellationRequested();

            // Aggregate results from all datasources
            var aggregatedGames = new List<GameCacheInfo>();
            var aggregatedServices = new List<ServiceCacheInfo>();
            var gameAppIdSet = new HashSet<uint>(); // Track unique game app IDs across datasources
            var serviceNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Track unique services

            // Scan each datasource
            var datasourceIndex = 0;
            progressFilePath = Path.Combine(operationsDir, $"game_detection_progress_{operationId}.json");
            foreach (var datasource in datasources)
            {
                // Check for cancellation before each datasource
                cancellationToken.ThrowIfCancellationRequested();

                var cachePath = datasource.CachePath;
                var outputJson = Path.Combine(operationsDir, $"game_detection_{operationId}_{datasource.Name}.json");
                outputJsonFiles.Add(outputJson);

                _logger.LogInformation("[GameDetection] Scanning datasource '{DatasourceName}': {CachePath}", datasource.Name, cachePath);

                // Calculate progress base for this datasource (used for game processing progress later)
                // Note: We don't send progress here - the Rust process will send accurate live progress
                var progressBase = 30 + (40.0 * datasourceIndex / datasources.Count);

                // Build arguments
                // Add --incremental flag for quick scans to skip the expensive cache directory scan
                var incrementalFlag = incremental ? " --incremental" : "";
                string arguments = !string.IsNullOrEmpty(excludedIdsPath)
                    ? $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\" \"{excludedIdsPath}\"{incrementalFlag} --progress-file \"{progressFilePath}\""
                    : $"\"{dbPath}\" \"{cachePath}\" \"{outputJson}\"{incrementalFlag} --progress-file \"{progressFilePath}\"";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

                // Start progress monitoring task
                var progressCts = new CancellationTokenSource();
                var progressMonitorTask = Task.Run(async () =>
                {
                    try
                    {
                        while (!progressCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                        {
                            await Task.Delay(500, progressCts.Token);

                            var progress = await _rustProcessHelper.ReadProgressFileAsync<GameDetectionProgressData>(progressFilePath);
                            if (progress != null)
                            {
                                _operationTracker.UpdateProgress(operationId, progress.PercentComplete, progress.Message);

                                // Send SignalR notification for live updates
                                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
                                {
                                    OperationId = operationId,
                                    PercentComplete = progress.PercentComplete,
                                    Status = OperationStatus.Running,
                                    Message = progress.Message,
                                    gamesProcessed = progress.GamesProcessed,
                                    totalGames = progress.TotalGames
                                });
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected when cancellation is requested
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[GameDetection] Error in progress monitoring task");
                    }
                }, progressCts.Token);

                ProcessExecutionResult result;
                try
                {
                    result = await _rustProcessHelper.ExecuteProcessAsync(startInfo, cancellationToken);
                }
                finally
                {
                    // Stop progress monitoring
                    await progressCts.CancelAsync();
                    try { await progressMonitorTask; } catch { /* ignore cancellation */ }
                    progressCts.Dispose();
                }

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
            string completionMessage;

            if (incremental && existingGames != null && existingGames.Count > 0)
            {
                // Merge existing games with newly detected games
                finalGames = existingGames.ToList();
                finalGames.AddRange(aggregatedGames);
                totalGamesDetected = finalGames.Count;

                _logger.LogInformation("[GameDetection] Incremental scan complete: {NewCount} new games, {TotalCount} total",
                    newGamesCount, totalGamesDetected);

                completionMessage = newGamesCount > 0
                    ? $"Found {newGamesCount} new game{(newGamesCount != 1 ? "s" : "")} ({totalGamesDetected} total with cache files)"
                    : $"No new games detected ({totalGamesDetected} total with cache files)";
            }
            else
            {
                finalGames = aggregatedGames;
                totalGamesDetected = aggregatedGames.Count;
                completionMessage = $"Detected {totalGamesDetected} games with cache files";
            }

            // Add datasource info to message if multiple datasources
            if (datasources.Count > 1)
            {
                completionMessage += $" across {datasources.Count} datasources";
            }

            // Merge Epic games from resolved downloads
            var epicGames = await GetEpicGamesFromDownloadsAsync();
            if (epicGames.Count > 0)
            {
                finalGames.AddRange(epicGames);
                totalGamesDetected = finalGames.Count;
                _logger.LogInformation("[GameDetection] Added {Count} Epic games from resolved downloads", epicGames.Count);
            }

            // For incremental mode, use existing services (service detection is skipped)
            // For full scan, use newly detected services
            var finalServices = incremental && existingServices != null && existingServices.Count > 0
                ? existingServices
                : aggregatedServices;

            // Update tracker progress and metadata with final results
            _operationTracker.UpdateProgress(operationId, 90, completionMessage);
            _operationTracker.UpdateMetadata(operationId, m =>
            {
                var metrics = (GameDetectionMetrics)m;
                metrics.Games = finalGames;
                metrics.Services = finalServices;
                metrics.TotalGamesDetected = totalGamesDetected;
                metrics.TotalServicesDetected = finalServices.Count;
            });

            // Send progress for saving to database
            await SendProgressAsync("saving", "Saving results to database...", totalGamesDetected, finalServices.Count, 90);

            // Save results to database (replaces in-memory cache)
            // Epic games are excluded from DB persistence - they are always re-derived fresh
            // from the Downloads table via GetEpicGamesFromDownloadsAsync()
            var steamGamesForDb = finalGames.Where(g => g.Service != "epicgames").ToList();
            await SaveGamesToDatabaseAsync(steamGamesForDb, incremental);
            _logger.LogInformation("[GameDetection] Results saved to database - {SteamCount} Steam games persisted, {TotalCount} total (including {EpicCount} Epic)",
                steamGamesForDb.Count, totalGamesDetected, finalGames.Count - steamGamesForDb.Count);

            // For incremental scans, resolve any unknown games that now have depot mappings
            // Full scans already query fresh mappings, so this is only needed for incremental
            if (incremental)
            {
                var resolvedCount = await ResolveUnknownGamesInCacheAsync();
                if (resolvedCount > 0)
                {
                    _logger.LogInformation("[GameDetection] Resolved {Count} unknown games after incremental scan", resolvedCount);
                    completionMessage += $" (resolved {resolvedCount} previously unknown)";
                    _operationTracker.UpdateProgress(operationId, 95, completionMessage);
                }
            }

            // Save services to database (only for full scan, incremental preserves existing)
            if (!incremental && aggregatedServices.Count > 0)
            {
                await SaveServicesToDatabaseAsync(aggregatedServices);
                _logger.LogInformation("[GameDetection] Services saved to database - {Count} services total", aggregatedServices.Count);
            }

            _logger.LogInformation("[GameDetection] Completed: {Count} games detected across {DatasourceCount} datasource(s)",
                totalGamesDetected, datasources.Count);

            await FinalizeDetectionAsync(operationId, success: true,
                status: OperationStatus.Completed, message: completionMessage, cancelled: false,
                gamesDetected: totalGamesDetected, servicesDetected: finalServices.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[GameDetection] Operation {OperationId} was cancelled", operationId);

            await FinalizeDetectionAsync(operationId, success: false,
                status: OperationStatus.Cancelled, message: "Detection cancelled by user", cancelled: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Operation {OperationId} failed", operationId);

            // Update metadata with error before completing
            _operationTracker.UpdateMetadata(operationId, m =>
            {
                var metrics = (GameDetectionMetrics)m;
                metrics.Error = ex.Message;
            });

            await FinalizeDetectionAsync(operationId, success: false,
                status: OperationStatus.Failed, message: $"Detection failed: {ex.Message}", cancelled: false);
        }
        finally
        {
            // Clean up temporary files in all paths (success, cancel, error)
            foreach (var outputJson in outputJsonFiles)
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(outputJson);
            }
            if (!string.IsNullOrEmpty(excludedIdsPath))
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(excludedIdsPath);
            }
            if (!string.IsNullOrEmpty(progressFilePath))
            {
                await _rustProcessHelper.DeleteTemporaryFileAsync(progressFilePath);
            }
        }
    }

    /// <summary>
    /// Finalizes a detection operation by updating the tracker, persisted state, and sending SignalR notification.
    /// Consolidates the common teardown logic shared across success, cancel, and error paths.
    /// </summary>
    private async Task FinalizeDetectionAsync(
        string operationId, bool success, string status, string message, bool cancelled,
        int? gamesDetected = null, int? servicesDetected = null)
    {
        // Determine error string for tracker (cancelled = "Cancelled by user", failed = message, success = null)
        var trackerError = success ? null : (cancelled ? "Cancelled by user" : message);

        // Mark operation as complete in unified tracker
        _operationTracker.CompleteOperation(operationId, success: success, error: trackerError);
        _currentTrackerOperationId = null;

        // Build persisted state data
        var stateData = cancelled
            ? JsonSerializer.SerializeToElement(new { operationId, cancelled = true })
            : success
                ? JsonSerializer.SerializeToElement(new { operationId, totalGamesDetected = gamesDetected ?? 0 })
                : JsonSerializer.SerializeToElement(new { operationId, error = message });

        // Update persisted state
        _operationStateService.SaveState($"gameDetection_{operationId}", new OperationState
        {
            Key = $"gameDetection_{operationId}",
            Type = "gameDetection",
            Status = status,
            Message = message,
            Data = stateData
        });

        // Build and send SignalR completion notification
        await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, new
        {
            OperationId = operationId,
            Success = success,
            Status = status,
            Message = message,
            Cancelled = cancelled,
            totalGamesDetected = gamesDetected,
            totalServicesDetected = servicesDetected,
            timestamp = DateTime.UtcNow
        });
    }

    public DetectionOperationResponse? GetOperationStatus(string operationId)
    {
        var opInfo = _operationTracker.GetOperation(operationId);
        if (opInfo == null)
        {
            return null;
        }

        return BuildResponseFromOperationInfo(opInfo);
    }


    public DetectionOperationResponse? GetActiveOperation()
    {
        var activeOp = _operationTracker.GetActiveOperations(OperationType.GameDetection).FirstOrDefault();
        if (activeOp == null)
        {
            return null;
        }

        return BuildResponseFromOperationInfo(activeOp);
    }

    /// <summary>
    /// Cancel the currently running detection operation.
    /// Delegates to UnifiedOperationTracker for centralized cancellation.
    /// </summary>
    /// <remarks>
    /// This method exists for backward compatibility with GamesController.
    /// Prefer using OperationsController.CancelOperation with operationId for new code.
    /// </remarks>
    public void CancelDetection()
    {
        if (!string.IsNullOrEmpty(_currentTrackerOperationId))
        {
            _logger.LogInformation("[GameDetection] Cancelling detection via UnifiedOperationTracker: {OperationId}", _currentTrackerOperationId);
            _operationTracker.CancelOperation(_currentTrackerOperationId);
        }
        else
        {
            // Fallback: Cancel directly if tracker ID not available
            var activeOp = _operationTracker.GetActiveOperations(OperationType.GameDetection).FirstOrDefault();
            if (activeOp != null)
            {
                _logger.LogInformation("[GameDetection] Cancelling detection operation {OperationId} directly", activeOp.Id);
                _cancellationTokenSource?.Cancel();
            }
        }
    }

    public async Task<DetectionOperationResponse?> GetCachedDetectionAsync()
    {
        // First, try to resolve any unknown games in the cache using available mappings
        var resolvedCount = await ResolveUnknownGamesInCacheAsync();
        if (resolvedCount > 0)
        {
            _logger.LogInformation("[GameDetection] Auto-resolved {Count} unknown games when loading cache", resolvedCount);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        // Clean up any legacy GameAppId=0 entries left by the old Epic dedup bug
        var legacyZeroEntries = await dbContext.CachedGameDetections
            .Where(g => g.GameAppId == 0)
            .ToListAsync();
        if (legacyZeroEntries.Count > 0)
        {
            dbContext.CachedGameDetections.RemoveRange(legacyZeroEntries);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("[GameDetection] Cleaned up {Count} legacy GameAppId=0 entries from cache", legacyZeroEntries.Count);
        }

        var cachedGames = await dbContext.CachedGameDetections.AsNoTracking().ToListAsync();
        var cachedServices = await dbContext.CachedServiceDetections.AsNoTracking().ToListAsync();

        var games = cachedGames.Select(ConvertToGameCacheInfo).ToList();
        var services = cachedServices.Select(ConvertToServiceCacheInfo).ToList();

        // Merge Epic games from resolved downloads (Epic games are always derived fresh,
        // not persisted to CachedGameDetections, to avoid dedup issues with hash-based IDs)
        var epicGames = await GetEpicGamesFromDownloadsAsync();
        if (epicGames.Count > 0)
        {
            games.AddRange(epicGames);
        }

        if (games.Count == 0 && services.Count == 0)
        {
            return null;
        }

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

        return new DetectionOperationResponse
        {
            OperationId = "cached",
            StartTime = lastDetectedTime,
            Status = OperationStatus.Completed,
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
            var unknownGames = await GetUnknownGamesCachedAsync(dbContext);

            if (unknownGames.Count == 0)
            {
                return 0;
            }

            _logger.LogInformation("[GameDetection] Found {Count} unknown games in cache, attempting to resolve", unknownGames.Count);

            // First tier: Build a lookup from Downloads table (already has correct names from Rust processor)
            var unknownDepotIds = unknownGames.Select(g => (int)g.GameAppId).ToHashSet();
            var downloadsLookup = await dbContext.Downloads
                .Where(d => d.GameName != null && d.GameAppId != null && d.GameAppId > 0 && d.DepotId != null)
                .Select(d => new { d.GameAppId, d.GameName, d.DepotId })
                .Distinct()
                .ToListAsync();

            var depotToGameFromDownloads = downloadsLookup
                .Where(d => d.DepotId.HasValue && unknownDepotIds.Contains((int)d.DepotId.Value))
                .GroupBy(d => (int)d.DepotId!.Value)
                .ToDictionary(g => g.Key, g => g.First());

            if (depotToGameFromDownloads.Count > 0)
            {
                _logger.LogInformation("[GameDetection] Downloads table pre-lookup found {Count} depot(s) with resolved names", depotToGameFromDownloads.Count);
            }

            int resolvedCount = 0;
            var newlyFailedDepots = new List<uint>();
            var entriesToRemove = new List<CachedGameDetection>();

            // Track AppIds we've already resolved to in this batch to prevent UNIQUE constraint violations
            // Key: AppId, Value: the CachedGameDetection entity that will have this AppId after save
            var pendingAppIdAssignments = new Dictionary<uint, CachedGameDetection>();

            foreach (var unknownGame in unknownGames)
            {
                // For unknown games, the depot ID is stored as GameAppId
                var depotId = unknownGame.GameAppId;

                // First tier: Try to resolve from Downloads table (has correct names from Rust processor)
                if (depotToGameFromDownloads.TryGetValue((int)depotId, out var downloadsMatch))
                {
                    var resolvedAppId = downloadsMatch.GameAppId!.Value;
                    var resolvedName = downloadsMatch.GameName!;

                    _logger.LogInformation("[GameDetection] Resolved depot {DepotId} -> {AppId} ({Name}) via Downloads table",
                        depotId, resolvedAppId, resolvedName);

                    // Check if we've already assigned this AppId in this batch
                    if (pendingAppIdAssignments.TryGetValue(resolvedAppId, out var pendingGame))
                    {
                        MergeUnknownGameIntoTarget(pendingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        _logger.LogInformation("[GameDetection] Merged depot {DepotId} into pending game {AppId} ({Name}) via Downloads table",
                            depotId, resolvedAppId, resolvedName);
                        resolvedCount++;
                        continue;
                    }

                    // Check if a record with this AppId already exists in database
                    var existingGame = await dbContext.CachedGameDetections
                        .FirstOrDefaultAsync(g => g.GameAppId == resolvedAppId);

                    if (existingGame != null && existingGame.Id != unknownGame.Id)
                    {
                        MergeUnknownGameIntoTarget(existingGame, unknownGame);
                        entriesToRemove.Add(unknownGame);
                        pendingAppIdAssignments[resolvedAppId] = existingGame;
                        _logger.LogInformation("[GameDetection] Merged depot {DepotId} into existing game {AppId} ({Name}) via Downloads table",
                            depotId, resolvedAppId, resolvedName);
                    }
                    else
                    {
                        unknownGame.GameName = resolvedName;
                        unknownGame.GameAppId = resolvedAppId;
                        pendingAppIdAssignments[resolvedAppId] = unknownGame;
                    }

                    resolvedCount++;
                    continue;
                }

                // Not found in Downloads DB — will remain as "Unknown Game" until download activity creates a mapping
                newlyFailedDepots.Add(depotId);
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

            // Log unresolved depots (Downloads DB is the only resolution source for Game Cache Detection)
            if (newlyFailedDepots.Count > 0)
            {
                _logger.LogInformation("[GameDetection] Could not resolve {Count} depot(s) from Downloads DB: {DepotIds}",
                    newlyFailedDepots.Count, string.Join(", ", newlyFailedDepots));
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
                        // Register with UnifiedOperationTracker for recovery
                        _cancellationTokenSource = new CancellationTokenSource();
                        var metadata = new GameDetectionMetrics { ScanType = "incremental" };
                        var trackerOpId = _operationTracker.RegisterOperation(
                            OperationType.GameDetection,
                            "Game Detection",
                            _cancellationTokenSource,
                            metadata
                        );
                        _currentTrackerOperationId = trackerOpId;
                        _operationTracker.UpdateProgress(trackerOpId, 0, state.Message ?? "Resuming game cache detection...");

                        _logger.LogInformation("[GameDetection] Restored interrupted operation {OperationId} as {TrackerOpId}", operationId, trackerOpId);

                        // Restart the detection task with incremental scanning (default)
                        var cancellationToken = _cancellationTokenSource.Token;
                        _ = Task.Run(async () => await RunDetectionAsync(trackerOpId, incremental: true, cancellationToken));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[GameDetection] Error restoring interrupted operations");
        }
    }

    /// <summary>
    /// Builds a DetectionOperationResponse from an OperationInfo, preserving the JSON shape expected by the frontend.
    /// </summary>
    private static DetectionOperationResponse BuildResponseFromOperationInfo(OperationInfo opInfo)
    {
        var metrics = opInfo.Metadata as GameDetectionMetrics;

        return new DetectionOperationResponse
        {
            OperationId = opInfo.Id,
            StartTime = opInfo.StartedAt,
            Status = opInfo.Status,
            Message = opInfo.Message,
            PercentComplete = opInfo.PercentComplete,
            ScanType = metrics?.ScanType ?? "incremental",
            Games = metrics?.Games,
            Services = metrics?.Services,
            TotalGamesDetected = metrics?.TotalGamesDetected ?? 0,
            TotalServicesDetected = metrics?.TotalServicesDetected ?? 0,
            Error = metrics?.Error
        };
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
                .FirstOrDefaultAsync(g => g.GameAppId == cachedGame.GameAppId);

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
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException pgEx && pgEx.SqlState == "23505")
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

    private static async Task<List<CachedGameDetection>> GetUnknownGamesCachedAsync(AppDbContext dbContext)
    {
        return await dbContext.CachedGameDetections
            .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
            .ToListAsync();
    }

    private static List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
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
            SampleUrls = DeserializeStringList(cached.SampleUrlsJson),
            CacheFilePaths = DeserializeStringList(cached.CacheFilePathsJson),
            Datasources = DeserializeStringList(datasourcesJson),
            ImageUrl = null
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
            SampleUrls = DeserializeStringList(cached.SampleUrlsJson),
            CacheFilePaths = DeserializeStringList(cached.CacheFilePathsJson),
            Datasources = DeserializeStringList(datasourcesJson)
        };
    }

    /// <summary>
    /// Query the database for resolved Epic downloads and create GameCacheInfo entries.
    /// These are merged into the game detection results alongside Rust-detected Steam games.
    /// </summary>
    private async Task<List<GameCacheInfo>> GetEpicGamesFromDownloadsAsync()
    {
        try
        {
            await using var db = await _dbContextFactory.CreateDbContextAsync();

            var epicDownloads = await db.Downloads
                .Where(d => d.EpicAppId != null && d.GameName != null)
                .ToListAsync();

            if (epicDownloads.Count == 0)
                return new List<GameCacheInfo>();

            // Group by EpicAppId to aggregate per-game stats
            var epicGames = epicDownloads
                .GroupBy(d => d.EpicAppId!)
                .Select(g => new GameCacheInfo
                {
                    GameAppId = GenerateEpicGameAppId(g.Key),
                    GameName = g.First().GameName ?? $"Epic Game ({g.Key})",
                    Service = "epicgames",
                    CacheFilesFound = g.Count(),
                    TotalSizeBytes = (ulong)g.Sum(d => d.CacheHitBytes + d.CacheMissBytes),
                    DepotIds = new List<uint>(),
                    SampleUrls = g.Where(d => d.LastUrl != null).Select(d => d.LastUrl!).Take(3).ToList(),
                    CacheFilePaths = new List<string>(),
                    Datasources = g.Select(d => d.Datasource).Distinct().ToList(),
                    ImageUrl = null,
                    EpicAppId = g.Key
                })
                .ToList();

            _logger.LogInformation("Found {Count} Epic games from resolved downloads", epicGames.Count);
            return epicGames;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get Epic games from downloads");
            return new List<GameCacheInfo>();
        }
    }

    /// <summary>
    /// Merges cache file counts, size, depot IDs, and cache file paths from source into target.
    /// Used when multiple unknown games resolve to the same AppId during game detection.
    /// </summary>
    private static void MergeUnknownGameIntoTarget(CachedGameDetection target, CachedGameDetection source)
    {
        target.CacheFilesFound += source.CacheFilesFound;
        target.TotalSizeBytes += source.TotalSizeBytes;
        target.LastDetectedUtc = target.LastDetectedUtc > source.LastDetectedUtc
            ? target.LastDetectedUtc : source.LastDetectedUtc;

        // Merge depot IDs
        var targetDepots = JsonSerializer.Deserialize<List<uint>>(target.DepotIdsJson) ?? new List<uint>();
        var sourceDepots = JsonSerializer.Deserialize<List<uint>>(source.DepotIdsJson) ?? new List<uint>();
        targetDepots.AddRange(sourceDepots);
        target.DepotIdsJson = JsonSerializer.Serialize(targetDepots.Distinct().ToList());

        // Merge cache file paths
        var targetPaths = DeserializeStringList(target.CacheFilePathsJson);
        var sourcePaths = DeserializeStringList(source.CacheFilePathsJson);
        targetPaths.AddRange(sourcePaths);
        target.CacheFilePathsJson = JsonSerializer.Serialize(targetPaths.Distinct().ToList());
    }

    /// <summary>
    /// Generates a deterministic unique uint GameAppId from an Epic string AppId.
    /// Uses SHA256 hash mapped to the upper half of uint range (2,147,483,648 - 4,294,967,295)
    /// to avoid collisions with Steam AppIds which use the lower range (typically under 3,000,000).
    /// </summary>
    private static uint GenerateEpicGameAppId(string epicAppId)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(epicAppId));
        // Use first 4 bytes of SHA256 hash as a uint
        var rawHash = BitConverter.ToUInt32(hashBytes, 0);
        // Map to upper half of uint range (set the high bit) to avoid Steam AppId collisions.
        // Also ensure non-zero by OR-ing with the high bit.
        return rawHash | 0x80000000;
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

    /// <summary>
    /// JSON model for Rust game detection progress.
    /// </summary>
    private class GameDetectionProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
        public double PercentComplete { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("gamesProcessed")]
        public int GamesProcessed { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("totalGames")]
        public int TotalGames { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("timestamp")]
        public string? Timestamp { get; set; }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _startLock.Dispose();

        _disposed = true;
    }
}
