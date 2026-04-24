using System.Text.Json;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Core.Interfaces;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

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
    private readonly GameCacheDetectionDataService _detectionDataService;
    private readonly EvictedDetectionPreservationService _evictedDetectionPreservationService;
    private readonly UnknownGameResolutionService _unknownGameResolutionService;
    private readonly RustProcessHelper _rustProcessHelper;
    private readonly ISignalRNotificationService _notifications;
    private readonly DatasourceService _datasourceService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentTrackerOperationId;

    // In-memory cache for detection response — avoids 10+ DB queries on every dashboard load.
    // Invalidated when detection scans, eviction scans, or game removals change the data.
    private DetectionOperationResponse? _cachedDetectionResponse;
    private readonly SemaphoreSlim _detectionCacheLock = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Response DTO that preserves the JSON shape expected by the frontend.
    /// Built from OperationInfo + GameDetectionMetrics metadata.
    /// </summary>
    public class DetectionOperationResponse
    {
        public Guid OperationId { get; set; }
        public DateTime StartTime { get; set; }
        public OperationStatus Status { get; set; } = OperationStatus.Running;
        public string? Message { get; set; }
        public string? StatusMessage => Message; // Alias for frontend compatibility
        public double PercentComplete { get; set; }
        public DetectionScanType ScanType { get; set; } = DetectionScanType.Incremental;
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
        GameCacheDetectionDataService detectionDataService,
        EvictedDetectionPreservationService evictedDetectionPreservationService,
        UnknownGameResolutionService unknownGameResolutionService,
        RustProcessHelper rustProcessHelper,
        ISignalRNotificationService notifications,
        DatasourceService datasourceService,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _pathResolver = pathResolver;
        _operationStateService = operationStateService;
        _dbContextFactory = dbContextFactory;
        _detectionDataService = detectionDataService;
        _evictedDetectionPreservationService = evictedDetectionPreservationService;
        _unknownGameResolutionService = unknownGameResolutionService;
        _rustProcessHelper = rustProcessHelper;
        _notifications = notifications;
        _datasourceService = datasourceService;
        _operationTracker = operationTracker;

        _logger.LogInformation("GameCacheDetectionService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        // Restore any interrupted operations on startup
        RestoreInterruptedOperations();
    }

    public async Task<Guid?> StartDetectionAsync(bool incremental = true)
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

            var scanType = incremental ? DetectionScanType.Incremental : DetectionScanType.Full;
            var stageKeyStarting = incremental
                ? "signalr.gameDetect.starting.incremental"
                : "signalr.gameDetect.starting.full";

            // Register with unified operation tracker for centralized cancellation
            var metadata = new GameDetectionMetrics { ScanType = scanType };
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.GameDetection,
                "Game Detection",
                _cancellationTokenSource,
                metadata
            );
            var operationId = _currentTrackerOperationId.Value;

            // Set initial progress message
            _operationTracker.UpdateProgress(operationId, 0, stageKeyStarting);

            // Save to OperationStateService for persistence
            _operationStateService.SaveState($"{OperationType.GameDetection.ToWireString()}_{operationId}", new OperationState
            {
                Key = $"{OperationType.GameDetection.ToWireString()}_{operationId}",
                Type = OperationType.GameDetection.ToWireString(),
                Status = OperationStatus.Running.ToWireString(),
                Message = stageKeyStarting,
                Data = JsonSerializer.SerializeToElement(new { operationId })
            });

            // Send SignalR notification that detection started
            _ = Task.Run(async () =>
            {
                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionStarted, new
                {
                    OperationId = operationId,
                    StageKey = stageKeyStarting,
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
    private async Task<bool> ApplyMappingsPreCheckAsync(Guid operationId)
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

    private async Task RunDetectionAsync(Guid operationId, bool incremental, CancellationToken cancellationToken = default)
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
        var aggregatedGames = new List<GameCacheInfo>();
        var aggregatedServices = new List<ServiceCacheInfo>();

        // Helper to send progress notification
        async Task SendProgressAsync(string status, string stageKey, int gamesDetected = 0, int servicesDetected = 0, double progressPercent = 0, Dictionary<string, object?>? context = null)
        {
            _operationTracker.UpdateProgress(operationId, progressPercent, stageKey);
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
            {
                OperationId = operationId,
                PercentComplete = progressPercent,
                Status = OperationStatus.Running,
                StageKey = stageKey,
                Context = context,
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
                    var cachedGames = await dbContext.CachedGameDetections
                        .AsNoTracking()
                        .ToListAsync(cancellationToken);
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
                    var cachedServices = await dbContext.CachedServiceDetections
                        .AsNoTracking()
                        .ToListAsync(cancellationToken);
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

            // Incremental refreshes (for example after Steam/Epic mapping updates) preserve the
            // existing service rows. Skip Rust's service scan when we already loaded those rows
            // to avoid reprocessing large non-game service buckets that the caller will ignore.
            var skipServiceScan = incremental && existingServices is { Count: > 0 };

            // Aggregate results from all datasources
            var gameAppIdSet = new HashSet<long>(); // Track unique game app IDs across datasources
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
                var skipServiceScanFlag = skipServiceScan ? " --skip-service-scan" : "";
                string arguments = !string.IsNullOrEmpty(excludedIdsPath)
                    ? $"\"{cachePath}\" \"{outputJson}\" \"{excludedIdsPath}\"{incrementalFlag}{skipServiceScanFlag} --progress-file \"{progressFilePath}\""
                    : $"\"{cachePath}\" \"{outputJson}\"{incrementalFlag}{skipServiceScanFlag} --progress-file \"{progressFilePath}\"";

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
                                // Scale Rust progress (0-100%) to the scanning phase range (1-30%)
                                var scaledPercent = 1 + (progress.PercentComplete * 29.0 / 100.0);
                                _operationTracker.UpdateProgress(operationId, scaledPercent, progress.StageKey ?? string.Empty);

                                // Send SignalR notification for live updates
                                await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
                                {
                                    OperationId = operationId,
                                    PercentComplete = scaledPercent,
                                    Status = OperationStatus.Running,
                                    StageKey = progress.StageKey,
                                    Context = progress.Context,
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

                    // Send progress updates every 3 games or at the end (loop is in-memory, so frequent updates are cheap)
                    if (gameIndex - lastProgressUpdate >= 3 || gameIndex == totalGamesInResult)
                    {
                        lastProgressUpdate = gameIndex;
                        var gameProgress = gameProcessingStart + ((gameProcessingEnd - gameProcessingStart) * gameIndex / totalGamesInResult);
                        await SendProgressAsync(
                            "scanning",
                            "signalr.gameDetect.matching.progress",
                            aggregatedGames.Count,
                            aggregatedServices.Count,
                            gameProgress,
                            new Dictionary<string, object?> { ["processed"] = gameIndex, ["totalGames"] = totalGamesInResult });
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
            await SendProgressAsync("processing", "signalr.gameDetect.writing", aggregatedGames.Count, aggregatedServices.Count, 70);

            // Send progress for applying depot mappings
            await SendProgressAsync("applying_mappings", "signalr.gameDetect.db.querying", aggregatedGames.Count, aggregatedServices.Count, 85);

            // Merge with existing games if this was an incremental scan
            List<GameCacheInfo> finalGames;
            int totalGamesDetected;
            int newGamesCount = aggregatedGames.Count;
            string completionStageKey;
            Dictionary<string, object?> completionContext;

            if (incremental && existingGames != null && existingGames.Count > 0)
            {
                // Merge existing games with newly detected games
                finalGames = existingGames.ToList();
                finalGames.AddRange(aggregatedGames);
                totalGamesDetected = finalGames.Count;

                _logger.LogInformation("[GameDetection] Incremental scan complete: {NewCount} new games, {TotalCount} total",
                    newGamesCount, totalGamesDetected);

                completionStageKey = newGamesCount > 0
                    ? "signalr.gameDetect.complete.newGames"
                    : "signalr.gameDetect.complete.noNewGames";
                completionContext = new Dictionary<string, object?> { ["newGamesCount"] = newGamesCount, ["totalGamesDetected"] = totalGamesDetected };
            }
            else
            {
                finalGames = aggregatedGames;
                totalGamesDetected = aggregatedGames.Count;
                completionStageKey = "signalr.gameDetect.complete.full";
                completionContext = new Dictionary<string, object?> { ["totalGamesDetected"] = totalGamesDetected };
            }

            // Add datasource count to context if multiple datasources
            if (datasources.Count > 1)
            {
                completionContext["datasourceCount"] = datasources.Count;
            }

            // Epic games are now detected by the Rust processor using actual cache file sizes
            // (same approach as Steam games), so no separate C# merge is needed.

            // For incremental mode, use existing services (service detection is skipped)
            // For full scan, use newly detected services
            var finalServices = incremental && existingServices != null && existingServices.Count > 0
                ? existingServices
                : aggregatedServices;

            // Update tracker progress and metadata with final results
            _operationTracker.UpdateProgress(operationId, 90, completionStageKey);
            _operationTracker.UpdateMetadata(operationId, m =>
            {
                var metrics = (GameDetectionMetrics)m;
                metrics.Games = finalGames;
                metrics.Services = finalServices;
                metrics.TotalGamesDetected = totalGamesDetected;
                metrics.TotalServicesDetected = finalServices.Count;
            });

            // Send progress for saving to database
            await SendProgressAsync("saving", "signalr.gameDetect.db.complete", totalGamesDetected, finalServices.Count, 90);

            // Save all games (Steam + Epic) to database - Epic games now use actual cache file sizes
            // from the Rust processor, so they can be persisted alongside Steam games.
            await SaveGamesToDatabaseAsync(finalGames, incremental, cancellationToken);
            _logger.LogInformation("[GameDetection] Results saved to database - {Count} games persisted", finalGames.Count);

            // Recover evicted games: find games in Downloads that have no cache files on disk
            // and add them to CachedGameDetections so they appear in the evicted games list.
            // Run on both full and incremental scans so evicted games are always recovered.
            var evictedCount = await RecoverEvictedGamesAsync(cancellationToken);
            if (evictedCount > 0)
            {
                _logger.LogInformation("[GameDetection] Recovered {Count} evicted games from Downloads history", evictedCount);
                totalGamesDetected += evictedCount;
            }

            var epicGameCount = finalGames.Count(g => g.EpicAppId != null);
            var steamGameCount = finalGames.Count(g => g.EpicAppId == null);
            _logger.LogDebug(
                "[GameDetection] === Detection Summary === Steam games: {SteamCount} | Epic games: {EpicCount} | Total games: {TotalCount} | Evicted (recovered): {EvictedCount}",
                steamGameCount, epicGameCount, totalGamesDetected, evictedCount);

            // For incremental scans, resolve any unknown games that now have depot mappings
            // Full scans already query fresh mappings, so this is only needed for incremental
            if (incremental)
            {
                // Apply a timeout so a DB deadlock cannot stall the operation indefinitely.
                // If the timeout fires, the OperationCanceledException is NOT caught by
                // ResolveUnknownGamesInCacheAsync's own catch (it only catches non-cancellation
                // exceptions), so it bubbles up to our outer catch block which calls
                // FinalizeDetectionAsync with success:false, ensuring the frontend notification clears.
                using var resolveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                resolveCts.CancelAfter(TimeSpan.FromSeconds(60));

                var resolvedCount = await ResolveUnknownGamesInCacheAsync(resolveCts.Token);
                if (resolvedCount > 0)
                {
                    _logger.LogInformation("[GameDetection] Resolved {Count} unknown games after incremental scan", resolvedCount);
                    completionContext["resolvedCount"] = resolvedCount;
                    _operationTracker.UpdateProgress(operationId, 95, completionStageKey);
                }
            }

            // Save services to database (only for full scan, incremental preserves existing)
            // Always run on full scan even with zero services — zero incoming means everything should be evicted
            if (!incremental)
            {
                await SaveServicesToDatabaseAsync(aggregatedServices, cancellationToken);
                _logger.LogInformation("[GameDetection] Services saved to database - {Count} services total", aggregatedServices.Count);
            }

            _logger.LogInformation("[GameDetection] Completed: {Count} games detected across {DatasourceCount} datasource(s)",
                totalGamesDetected, datasources.Count);

            await FinalizeDetectionAsync(operationId, success: true,
                status: OperationStatus.Completed, stageKey: completionStageKey, cancelled: false,
                context: completionContext, gamesDetected: totalGamesDetected, servicesDetected: finalServices.Count);
        }
        catch (OperationCanceledException oce)
        {
            // Only treat as user-cancelled if the root cancellation token was signalled.
            // If a linked timeout token (e.g. from ResolveUnknownGamesInCacheAsync) fired instead,
            // fall through to the failure path so the frontend gets a clear error message.
            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("[GameDetection] Operation {OperationId} was cancelled", operationId);

                // Save any partial results accumulated before cancellation so the next startup
                // can resume from where we left off rather than re-scanning from scratch.
                if (aggregatedGames.Count > 0)
                {
                    try
                    {
                        // Use incremental=true so existing DB rows are preserved/updated rather than deleted.
                        // CancellationToken.None is critical — the original token is already cancelled.
                        await SaveGamesToDatabaseAsync(
                            aggregatedGames,
                            incremental: true,
                            CancellationToken.None);
                        _logger.LogInformation("[GameDetection] Saved {Count} partial game detection results before cancellation", aggregatedGames.Count);
                    }
                    catch (Exception saveEx)
                    {
                        _logger.LogWarning(saveEx, "[GameDetection] Failed to save partial game detection results");
                    }
                }

                await FinalizeDetectionAsync(operationId, success: false,
                    status: OperationStatus.Cancelled, stageKey: "signalr.gameDetect.starting.incremental", cancelled: true);
            }
            else
            {
                _logger.LogError(oce, "[GameDetection] Operation {OperationId} failed due to timeout or internal cancellation", operationId);

                _operationTracker.UpdateMetadata(operationId, m =>
                {
                    var metrics = (GameDetectionMetrics)m;
                    metrics.Error = oce.Message;
                });

                await FinalizeDetectionAsync(operationId, success: false,
                    status: OperationStatus.Failed, stageKey: "signalr.generic.failed", cancelled: false,
                    context: new Dictionary<string, object?> { ["errorDetail"] = oce.Message });
            }
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
                status: OperationStatus.Failed, stageKey: "signalr.generic.failed", cancelled: false,
                context: new Dictionary<string, object?> { ["errorDetail"] = ex.Message });
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
        Guid operationId, bool success, OperationStatus status, string stageKey, bool cancelled,
        Dictionary<string, object?>? context = null, int? gamesDetected = null, int? servicesDetected = null)
    {
        // Invalidate in-memory detection cache so next dashboard load picks up new data
        InvalidateDetectionCache();

        // Determine error string for tracker (cancelled = "Cancelled by user", failed = stageKey, success = null)
        var trackerError = success ? null : (cancelled ? "Cancelled by user" : stageKey);

        // Mark operation as complete in unified tracker
        _operationTracker.CompleteOperation(operationId, success: success, error: trackerError);
        _currentTrackerOperationId = null;

        // Build persisted state data
        var stateData = cancelled
            ? JsonSerializer.SerializeToElement(new { operationId, cancelled = true })
            : success
                ? JsonSerializer.SerializeToElement(new { operationId, totalGamesDetected = gamesDetected ?? 0 })
                : JsonSerializer.SerializeToElement(new { operationId, error = stageKey });

        // Update persisted state
        _operationStateService.SaveState($"{OperationType.GameDetection.ToWireString()}_{operationId}", new OperationState
        {
            Key = $"{OperationType.GameDetection.ToWireString()}_{operationId}",
            Type = OperationType.GameDetection.ToWireString(),
            Status = status.ToWireString(),
            Message = stageKey,
            Data = stateData
        });

        // Build and send SignalR completion notification
        await _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, new
        {
            OperationId = operationId,
            Success = success,
            Status = status,
            StageKey = stageKey,
            Context = context,
            Cancelled = cancelled,
            totalGamesDetected = gamesDetected,
            totalServicesDetected = servicesDetected,
            timestamp = DateTime.UtcNow
        });
    }

    public DetectionOperationResponse? GetOperationStatus(Guid operationId)
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
        if (_currentTrackerOperationId.HasValue)
        {
            _logger.LogInformation("[GameDetection] Cancelling detection via UnifiedOperationTracker: {OperationId}", _currentTrackerOperationId);
            _operationTracker.CancelOperation(_currentTrackerOperationId.Value);
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

    /// <summary>
    /// Runs recovery, self-healing, and cleanup operations on cached detection data.
    /// Called once on startup by GameDetectionService — NOT on every dashboard load.
    /// </summary>
    public async Task ReconcileCachedDetectionDataAsync()
    {
        var recoveredCount = await RecoverEvictedGamesAsync();
        if (recoveredCount > 0)
        {
            _logger.LogInformation("[GameDetection] Recovered {Count} evicted games from Downloads history on startup", recoveredCount);
        }

        var resolvedCount = await ResolveUnknownGamesInCacheAsync();
        if (resolvedCount > 0)
        {
            _logger.LogInformation("[GameDetection] Auto-resolved {Count} unknown games on startup", resolvedCount);
        }

        await using var dbContext = await _dbContextFactory.CreateDbContextAsync();

        try
        {
            var selfHealedCount = await CacheReconciliationService.UnevictCachedGameDetectionsAsync(
                dbContext,
                _logger,
                _detectionDataService,
                _evictedDetectionPreservationService,
                default);
            if (selfHealedCount > 0)
            {
                _logger.LogInformation(
                    "[GameDetection] Self-healed {Count} CachedGameDetection rows on startup (Trigger #2)",
                    selfHealedCount);
            }
        }
        catch (Exception selfHealEx)
        {
            _logger.LogWarning(selfHealEx,
                "[GameDetection] Trigger #2 reverse-reconcile failed on startup — proceeding with stale data");
        }

        try
        {
            var serviceSelfHealedCount = await CacheReconciliationService.UnevictCachedServiceDetectionsAsync(dbContext, _logger, default);
            if (serviceSelfHealedCount > 0)
            {
                _logger.LogInformation(
                    "[ServiceDetection] Self-healed {Count} CachedServiceDetection rows on startup",
                    serviceSelfHealedCount);
            }
        }
        catch (Exception selfHealEx)
        {
            _logger.LogWarning(selfHealEx,
                "[ServiceDetection] Service self-heal failed on startup — proceeding with stale data");
        }

        var legacyZeroEntries = await dbContext.CachedGameDetections
            .Where(g => g.GameAppId == 0 && g.EpicAppId == null)
            .ToListAsync();
        if (legacyZeroEntries.Count > 0)
        {
            dbContext.CachedGameDetections.RemoveRange(legacyZeroEntries);
            await dbContext.SaveChangesAsync();
            _logger.LogInformation("[GameDetection] Cleaned up {Count} legacy GameAppId=0 entries from cache on startup", legacyZeroEntries.Count);
        }

        InvalidateDetectionCache();
        _logger.LogInformation("[GameDetection] Startup reconciliation complete");
    }

    /// <summary>
    /// Clears the in-memory detection cache, forcing the next GetCachedDetectionAsync call
    /// to re-query the database. Call this after detection scans, eviction scans, or game removals.
    /// </summary>
    public void InvalidateDetectionCache()
    {
        _detectionCacheLock.Wait();
        try
        {
            _cachedDetectionResponse = null;
            _logger.LogDebug("[GameDetection] In-memory detection cache invalidated");
        }
        finally
        {
            _detectionCacheLock.Release();
        }
    }

    public async Task<DetectionOperationResponse?> GetCachedDetectionAsync()
    {
        await _detectionCacheLock.WaitAsync();
        try
        {
            if (_cachedDetectionResponse != null)
            {
                return _cachedDetectionResponse;
            }

            var result = await LoadDetectionFromDatabaseAsync();
            _cachedDetectionResponse = result;
            return result;
        }
        finally
        {
            _detectionCacheLock.Release();
        }
    }

    private Task<DetectionOperationResponse?> LoadDetectionFromDatabaseAsync(
        CancellationToken cancellationToken = default) =>
        _detectionDataService.LoadDetectionFromDatabaseAsync(cancellationToken);

    public async Task InvalidateCacheAsync()
    {
        await _detectionDataService.InvalidateCacheAsync();
        InvalidateDetectionCache();
    }

    public async Task RemoveGameFromCacheAsync(long gameAppId)
    {
        await _detectionDataService.RemoveGameFromCacheAsync(gameAppId);
        InvalidateDetectionCache();
    }

    public async Task RemoveServiceFromCacheAsync(string serviceName)
    {
        await _detectionDataService.RemoveServiceFromCacheAsync(serviceName);
        InvalidateDetectionCache();
    }

    /// <summary>
    /// Resolve unknown games in the cache by looking up their depot IDs in SteamDepotMappings.
    /// This updates cached "Unknown Game (Depot X)" entries when mappings become available.
    /// Returns the number of games that were resolved.
    /// </summary>
    public Task<int> ResolveUnknownGamesInCacheAsync(CancellationToken cancellationToken = default) =>
        _unknownGameResolutionService.ResolveUnknownGamesInCacheAsync(cancellationToken);

    private void RestoreInterruptedOperations()
    {
        try
        {
            var allStates = _operationStateService.GetAllStates();

            // Only restore operations that are recent (within last 5 minutes) to avoid re-running old completed operations
            var recentCutoff = DateTime.UtcNow.AddMinutes(-5);
            var gameDetectionStates = allStates.Where(s =>
                s.Type == OperationType.GameDetection.ToWireString() &&
                s.Status == OperationStatus.Running.ToWireString() &&
                s.CreatedAt > recentCutoff);

            foreach (var state in gameDetectionStates)
            {
                if (!state.Data.HasValue || !state.Data.Value.TryGetProperty("operationId", out var opIdElement))
                {
                    continue;
                }

                var operationIdString = opIdElement.GetString();
                if (string.IsNullOrEmpty(operationIdString))
                {
                    continue;
                }

                if (!Guid.TryParse(operationIdString, out var persistedGuid))
                {
                    _logger.LogWarning("[GameDetection] Persisted operationId '{OperationId}' is not a valid Guid — skipping", operationIdString);
                    continue;
                }

                _cancellationTokenSource = new CancellationTokenSource();
                var metadata = new GameDetectionMetrics { ScanType = DetectionScanType.Incremental };

                if (!_operationTracker.TryRestoreOperation(
                        persistedGuid,
                        OperationType.GameDetection,
                        "Game Detection",
                        _cancellationTokenSource,
                        metadata))
                {
                    _logger.LogWarning("[GameDetection] Persisted operation {Id} already registered — skipping", persistedGuid);
                    continue;
                }

                _currentTrackerOperationId = persistedGuid;
                _operationTracker.UpdateProgress(persistedGuid, 0, state.Message ?? "Resuming game cache detection...");

                _logger.LogInformation("[GameDetection] Restored interrupted operation {OperationId}", persistedGuid);

                // Restart the detection task with incremental scanning (default)
                var cancellationToken = _cancellationTokenSource.Token;
                _ = Task.Run(async () => await RunDetectionAsync(persistedGuid, incremental: true, cancellationToken));
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
            ScanType = metrics?.ScanType ?? DetectionScanType.Incremental,
            Games = metrics?.Games,
            Services = metrics?.Services,
            TotalGamesDetected = metrics?.TotalGamesDetected ?? 0,
            TotalServicesDetected = metrics?.TotalServicesDetected ?? 0,
            Error = metrics?.Error
        };
    }

    private Task SaveGamesToDatabaseAsync(
        List<GameCacheInfo> games,
        bool incremental,
        CancellationToken cancellationToken = default) =>
        _detectionDataService.SaveGamesToDatabaseAsync(games, incremental, cancellationToken);

    /// <summary>
    /// Surfaces entities whose underlying Downloads are already marked <c>IsEvicted = true</c>
    /// but which do not yet have a matching cached detection row. Public so
    /// <see cref="CacheReconciliationService.ReconcileCacheFilesAsync"/> can call it after
    /// every eviction scan — without this, a game whose downloads were evicted but which was
    /// never in <see cref="CachedGameDetection"/> (for example after a cache clear or before
    /// the first full detection) will not appear in the Evicted Items UI until the next app
    /// restart or full detection pass.
    /// </summary>
    public Task<int> RecoverEvictedGamesAsync(CancellationToken cancellationToken = default) =>
        _detectionDataService.RecoverEvictedGamesAsync(cancellationToken);

    /// <summary>
    /// Service-scope analogue of <see cref="RecoverEvictedGamesAsync"/>. Finds non-game
    /// services (wsus, xboxlive, blizzard-only traffic, etc.) that have evicted Downloads
    /// but no matching CachedServiceDetection row, and inserts an IsEvicted=true detection
    /// row so the Evicted Items UI can display them without waiting for a full cache scan.
    /// A "service Downloads row" is one with both GameAppId and EpicAppId set to null.
    /// </summary>
    public Task<int> RecoverEvictedServicesAsync(CancellationToken cancellationToken = default) =>
        _detectionDataService.RecoverEvictedServicesAsync(cancellationToken);

    private Task SaveServicesToDatabaseAsync(
        List<ServiceCacheInfo> services,
        CancellationToken cancellationToken = default) =>
        _detectionDataService.SaveServicesToDatabaseAsync(services, cancellationToken);

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
            Service = cached.Service,
            EpicAppId = cached.EpicAppId,
            IsEvicted = cached.IsEvicted
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
            Datasources = DeserializeStringList(datasourcesJson),
            IsEvicted = cached.IsEvicted
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

    /// <summary>
    /// JSON model for Rust game detection progress.
    /// </summary>
    private class GameDetectionProgressData
    {
        [System.Text.Json.Serialization.JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("stageKey")]
        public string? StageKey { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("context")]
        public Dictionary<string, object?>? Context { get; set; }

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

        try { _cancellationTokenSource?.Cancel(); } catch (ObjectDisposedException) { }
        try { _cancellationTokenSource?.Dispose(); } catch (ObjectDisposedException) { }
        _startLock.Dispose();
        _detectionCacheLock.Dispose();

        _disposed = true;
    }
}
