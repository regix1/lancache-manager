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
public partial class GameCacheDetectionService : IDisposable
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
    private readonly DatasourceCapabilityService _capabilityService;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CancellationTokenSource? _cancellationTokenSource;
    private Guid? _currentTrackerOperationId;

    // Terminal-emit payload: completion metrics are only known when FinalizeDetectionAsync runs,
    // so it stores them here (by value) immediately before CompleteOperation fires the onTerminalEmit
    // closure registered in StartDetectionAsync/RestoreInterruptedOperations. The closure reads this
    // to build the typed GameDetectionComplete record (success/failed paths know which via info).
    private GameDetectionTerminalPayload _terminalPayload;

    // In-memory cache for detection response - avoids 10+ DB queries on every dashboard load.
    // Invalidated when detection scans, eviction scans, or game removals change the data.
    private DetectionOperationResponse? _cachedDetectionResponse;
    private readonly SemaphoreSlim _detectionCacheLock = new(1, 1);

    private bool _disposed;

    /// <summary>
    /// Strongly-typed carrier for the data the onTerminalEmit closure needs to build a
    /// GameDetectionComplete record. Populated by FinalizeDetectionAsync just before CompleteOperation.
    /// Success/cancel/error is supplied by OperationTerminalInfo, so only the metrics live here.
    /// </summary>
    private readonly record struct GameDetectionTerminalPayload(
        string StageKey,
        Dictionary<string, object?>? Context,
        int? GamesDetected,
        int? ServicesDetected,
        int? NewGamesCount,
        OperationStatus Status);

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

        /// <summary>
        /// Run-stable display flag for the active run. Silent automatic detections leave this
        /// false so the recovery endpoint can decline to resurrect a card on page reload.
        /// </summary>
        public bool ShowNotification { get; set; } = true;

        /// <summary>
        /// i18n interpolation values for <see cref="Message"/> when it is a signalr stage key.
        /// </summary>
        public Dictionary<string, object?>? Context { get; set; }

        /// <summary>
        /// Persisted deduplicated on-disk totals from the last summary refresh.
        /// </summary>
        public IdentifiedCacheAggregate? DiskSummary { get; set; }

        /// <summary>
        /// UTC timestamp when <see cref="DiskSummary"/> was last computed.
        /// </summary>
        public DateTime? SummaryComputedAtUtc { get; set; }
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
        DatasourceCapabilityService capabilityService,
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
        _capabilityService = capabilityService;
        _operationTracker = operationTracker;

        _logger.LogInformation("GameCacheDetectionService initialized with {Count} datasource(s)", _datasourceService.DatasourceCount);

        // Restore any interrupted operations on startup
        RestoreInterruptedOperations();
    }

    public async Task<Guid?> StartDetectionAsync(bool incremental = true, bool showNotification = true)
    {
        // Game detection derives logical objects from cache keys, so ambiguous datasource
        // evidence must be rejected even when this service is called without a controller.
        var capabilityDenial = _capabilityService.CheckAllCanMapLogicalObjects();
        if (capabilityDenial != null)
        {
            throw new InvalidOperationException(capabilityDenial);
        }

        await _startLock.WaitAsync();
        try
        {
            // Clean up stale operations (running for more than 30 minutes). Each stale op's terminal
            // is fired below via CompleteOperation, which runs THAT op's own onTerminalEmit closure -
            // carrying the stale run's own captured visibility, never this new attempt's flag.
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
            var metadata = new GameDetectionMetrics { ScanType = scanType, ShowNotification = showNotification };
            var registeredId = default(Guid);
            _currentTrackerOperationId = _operationTracker.RegisterOperation(
                OperationType.GameDetection,
                "Game Detection",
                _cancellationTokenSource,
                metadata,
                // Universal-force-kill safety net: the tracker invokes this on terminal regardless of
                // which path completed the op, so a force-kill that bypasses FinalizeDetectionAsync still
                // clears the service-local "detection running" marker (mirrors the reset at line ~777).
                onTerminalCleanup: () => { _currentTrackerOperationId = null; },
                // Capture this run's visibility in the closure so the terminal always carries the flag
                // the run started with, even if a concurrent StartDetectionAsync arrives mid-flight.
                onTerminalEmit: info => EmitDetectionCompleteAsync(registeredId, info, showNotification)
            );
            var operationId = _currentTrackerOperationId.Value;
            registeredId = operationId;

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

            // Send SignalR notification that detection started. Awaited (not fire-and-forget) so the
            // Started event is on the wire before any progress tick can be emitted by the background
            // run below, which prevents a progress event racing ahead of the card's creation.
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionStarted, new
            {
                OperationId = operationId,
                StageKey = stageKeyStarting,
                scanType,
                timestamp = DateTime.UtcNow,
                ShowNotification = showNotification
            });

            // Start detection in background with cancellation token
            var cancellationToken = _cancellationTokenSource.Token;
            _ = Task.Run(async () => await RunDetectionAsync(operationId, incremental, showNotification, cancellationToken), cancellationToken);

            return operationId;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Check if unknown depot IDs now have mappings available in SteamDepotMappings
    /// Returns the count of unknown depot IDs that now have mappings.
    /// On failure (logged as a warning), returns (0, 0) - indistinguishable from "no unknowns
    /// found"; this is a best-effort pre-check, not a required value, so the caller proceeds
    /// with a normal scan either way.
    /// </summary>
    private async Task<(int totalUnknowns, int nowMapped)> CountMappedDepotsAsync()
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
    /// If mappings exist, invalidate cache to trigger fresh scan with new mappings.
    /// On failure (logged as a warning), returns false and the scan proceeds normally - this is a
    /// best-effort optimization, not a required step.
    /// </summary>
    private async Task<bool> PreCheckMappingsAsync(Guid operationId)
    {
        try
        {
            var (totalUnknowns, nowMapped) = await CountMappedDepotsAsync();

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

    private async Task RunDetectionAsync(Guid operationId, bool incremental, bool showNotification, CancellationToken cancellationToken = default)
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

        // Highest percent emitted so far this run. Every emission (Rust scan tick and the phase
        // milestones below) is raised to this floor so the bar never regresses, a backstop on top of
        // the global-denominator scan mapping.
        double highestEmittedPercent = 0;
        double ClampMonotonic(double percent)
        {
            if (percent > highestEmittedPercent)
            {
                highestEmittedPercent = percent;
            }
            return highestEmittedPercent;
        }

        // Helper to send progress notification
        async Task SendProgressAsync(string status, string stageKey, int gamesDetected = 0, int servicesDetected = 0, double progressPercent = 0, Dictionary<string, object?>? context = null)
        {
            progressPercent = ClampMonotonic(progressPercent);
            _operationTracker.UpdateProgress(operationId, progressPercent, stageKey);
            // The tracker stores only the stage KEY; persist the interpolation context on the
            // metrics so the /api/games/detect/active recovery endpoint can translate it.
            _operationTracker.UpdateMetadata(operationId, (object meta) =>
            {
                var metrics = (GameDetectionMetrics)meta;
                metrics.CurrentContext = context;
            });
            await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
            {
                OperationId = operationId,
                PercentComplete = progressPercent,
                Status = OperationStatus.Running,
                StageKey = stageKey,
                Context = context,
                gamesDetected,
                servicesDetected,
                ShowNotification = showNotification
            });
        }

        try
        {
            _logger.LogInformation("[GameDetection] Starting detection for operation {OperationId} (incremental={Incremental})", operationId, incremental);

            // Check for cancellation at start
            cancellationToken.ThrowIfCancellationRequested();

            // Queue promotion and worker scheduling can delay execution after the start-time
            // check, so refresh the evidence before any cache-key-dependent processing launches.
            var executionDenial = _capabilityService.CheckAllCanMapLogicalObjects();
            if (executionDenial != null)
            {
                throw new InvalidOperationException(executionDenial);
            }

            // Smart pre-check: If incremental scan and we have 3+ unknown games, try applying mappings first
            if (incremental)
            {
                await PreCheckMappingsAsync(operationId);
            }

            var operationsDir = _pathResolver.GetOperationsDirectory();
            var rustBinaryPath = _pathResolver.GetRustGameDetectorPath();

            _rustProcessHelper.EnsureBinaryExists(rustBinaryPath, "Game cache detector");

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
                        existingGames = cachedGames.Select(ToGameCacheInfo).ToList();
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
                        existingServices = cachedServices.Select(ToServiceCacheInfo).ToList();
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

            // Aggregate results from all datasources.
            // Games are deduplicated by IDENTITY, not by GameAppId alone: Steam games use GameAppId,
            // Epic games use EpicAppId, and named (Blizzard/Riot) games use (Service, GameName) because
            // they ALL share GameAppId=0. Keying on GameAppId alone collapsed every named game into a
            // single row. This identity mirrors SaveGamesAsync's persistence buckets exactly.
            var gameIdentityMap = new Dictionary<string, GameCacheInfo>(StringComparer.Ordinal); // identity -> aggregated game
            var serviceNameSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // Track unique services

            // Full-scan reports that indexed zero cache files prove nothing about the disk
            // (wrong mount or path); count them so an all-empty scan can refuse to replace a
            // non-empty snapshot instead of silently wiping it.
            var fullScanReports = 0;
            var emptyIndexFullScanReports = 0;

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
                var keyScheme = _capabilityService.GetKeySchemeWireValue(datasource);
                // --progress (distinct from --progress-file) enables cache_game_detect.rs's live
                // stdout progress events, which the hybrid callback below waits on; without it
                // ProgressReporter.is_enabled() is false and no events flow.
                string arguments = !string.IsNullOrEmpty(excludedIdsPath)
                    ? $"\"{cachePath}\" \"{outputJson}\" \"{excludedIdsPath}\"{incrementalFlag}{skipServiceScanFlag} --progress-file \"{progressFilePath}\" --progress --key-scheme {keyScheme}"
                    : $"\"{cachePath}\" \"{outputJson}\"{incrementalFlag}{skipServiceScanFlag} --progress-file \"{progressFilePath}\" --progress --key-scheme {keyScheme}";

                var startInfo = _rustProcessHelper.CreateProcessStartInfo(rustBinaryPath, arguments);

                // cache_game_detect.rs emits genuine per-tick percent/context data over stdout
                // (confirmed by reading the binary directly), so each live stdout event now
                // triggers exactly one authoritative progress-file read in place of the old
                // Task.Delay(500)-based poll loop. The callback body below (tracker update +
                // SignalR notify) is unchanged from the previous poll-loop version.
                var lastProgressEmitTicks = long.MinValue;
                string? lastProgressStageKey = null;
                var result = await _rustProcessHelper.ExecuteTrackedProcessWithProgressEventsAsync(
                    startInfo,
                    operationId,
                    cancellationToken,
                    async _ =>
                    {
                        var progress = await _rustProcessHelper.ReadProgressFileAsync<GameDetectionProgressData>(progressFilePath);
                        if (progress == null)
                        {
                            return;
                        }

                        // Map this datasource's Rust progress into its slice of the global 1-30
                        // scanning band, then clamp to the running max so percent stays monotonic
                        // across datasources (a fresh Rust process restarts its own count at 0).
                        var scaledPercent = ClampMonotonic(
                            MapDatasourceScanPercent(progress.PercentComplete, datasourceIndex, datasources.Count));
                        _operationTracker.UpdateProgress(operationId, scaledPercent, progress.StageKey ?? string.Empty);
                        // The tracker stores only the stage KEY; persist the Rust progress
                        // context (e.g. processed/total for services.progress) on the metrics
                        // so the /api/games/detect/active recovery endpoint can translate it.
                        _operationTracker.UpdateMetadata(operationId, (object meta) =>
                        {
                            var metrics = (GameDetectionMetrics)meta;
                            metrics.CurrentContext = progress.Context;
                        });

                        // Gate the broadcast (the tracker updates above stay per-tick for recovery
                        // accuracy): rust can tick many times per second and every emit re-renders
                        // every connected client. Emit on stage change or at most every 250ms; the
                        // GameDetectionComplete event carries the final state, never a gated tick.
                        var nowTicks = Environment.TickCount64;
                        if (progress.StageKey == lastProgressStageKey &&
                            nowTicks - lastProgressEmitTicks < RustProcessHelper.ProgressEmitMinIntervalMs)
                        {
                            return;
                        }
                        lastProgressStageKey = progress.StageKey;
                        lastProgressEmitTicks = nowTicks;

                        // Send SignalR notification for live updates.
                        // gamesDetected/servicesDetected mirror the cross-datasource running totals
                        // SendProgressAsync already reports elsewhere in this method (aggregatedGames/
                        // aggregatedServices reflect every datasource fully processed so far; the
                        // datasource currently being scanned by this Rust process hasn't been merged
                        // in yet, so the count is real but not yet inclusive of it).
                        // servicesDetected falls back to existingServices when skipServiceScan is active -
                        // aggregatedServices never gets populated in that mode (Rust is told to skip
                        // service scanning), so without this fallback the live count would flatline at 0
                        // for the whole incremental run instead of reporting the real known total (mirrors
                        // the same existingServices fallback finalServices uses further below).
                        await _notifications.NotifyAllAsync(SignalREvents.GameDetectionProgress, new
                        {
                            OperationId = operationId,
                            PercentComplete = scaledPercent,
                            Status = OperationStatus.Running,
                            StageKey = progress.StageKey,
                            Context = progress.Context,
                            gamesDetected = aggregatedGames.Count,
                            servicesDetected = skipServiceScan && existingServices != null ? existingServices.Count : aggregatedServices.Count,
                            gamesProcessed = progress.GamesProcessed,
                            totalGames = progress.TotalGames,
                            ShowNotification = showNotification
                        });
                    },
                    "cache_game_detect");

                // Log diagnostic output from stderr (contains scan progress and stats)
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    _logger.LogInformation("[GameDetection] Diagnostic output for '{DatasourceName}':\n{DiagnosticOutput}",
                        datasource.Name, result.Error);
                }

                // No-op when ExitCode==0; otherwise throws a typed RustProcessException carrying the
                // exit code and stderr (result.Error, already logged above regardless of outcome).
                // The outer catch (Exception ex) logs it with the exception object and completes
                // the detection operation as a failure.
                result.EnsureSuccess("cache_game_detect", datasource.Name);

                // Read results from JSON
                if (!File.Exists(outputJson))
                {
                    throw new FileNotFoundException($"Output file not found: {outputJson}");
                }

                // Deserialize straight off the stream: the full-scan report carries every matched
                // cache path, so ReadAllText would first materialize hundreds of MB as one string.
                GameDetectionResult? detectionResult;
                await using (var outputStream = File.OpenRead(outputJson))
                {
                    detectionResult = await JsonSerializer.DeserializeAsync<GameDetectionResult>(
                        outputStream, cancellationToken: cancellationToken);
                }

                if (detectionResult == null)
                {
                    throw new Exception($"Failed to parse detection results for datasource '{datasource.Name}'");
                }

                if (detectionResult.IndexedCacheFiles is { } indexedCacheFiles)
                {
                    fullScanReports++;
                    if (indexedCacheFiles == 0)
                    {
                        emptyIndexFullScanReports++;
                        _logger.LogWarning(
                            "[GameDetection] Datasource '{DatasourceName}' cache directory {CachePath} exists but contained no cache files; the report is not detection evidence and its empty results are skipped",
                            datasource.Name, cachePath);
                        detectionResult.Games.Clear();
                        detectionResult.Services.Clear();
                    }
                }

                // Aggregate games (deduplicate by GameAppId)
                // Progress range for game aggregation: from current progress to next datasource's base
                var totalGamesInResult = detectionResult.Games.Count;
                var gameIndex = 0;
                var lastMatchProgressEmitTicks = long.MinValue;

                // Calculate progress range for this datasource's game processing
                // Games processing takes 30% of the datasource's share (from base to base + 30% of share)
                var datasourceProgressShare = 40.0 / datasources.Count;
                var gameProcessingStart = progressBase;
                var gameProcessingEnd = progressBase + (datasourceProgressShare * 0.75); // 75% of datasource share for game processing

                foreach (var game in detectionResult.Games)
                {
                    var identity = BuildGameIdentityKey(game);
                    if (!gameIdentityMap.TryGetValue(identity, out var existingGame))
                    {
                        // Initialize datasources list with current datasource
                        game.Datasources = new List<string> { datasource.Name };
                        gameIdentityMap[identity] = game;
                        aggregatedGames.Add(game);
                    }
                    else
                    {
                        // Game already found in another datasource - merge without double-counting files
                        GameCacheInfoMergeHelper.MergeGame(existingGame, game, datasource.Name);
                    }

                    gameIndex++;

                    // Send progress updates at most every 250ms, plus always the final game.
                    // The loop is in-memory and can spin through thousands of games in a second;
                    // emitting every few games broadcast-storms every connected client.
                    var matchEmitNowTicks = Environment.TickCount64;
                    if (gameIndex == totalGamesInResult ||
                        matchEmitNowTicks - lastMatchProgressEmitTicks >= RustProcessHelper.ProgressEmitMinIntervalMs)
                    {
                        lastMatchProgressEmitTicks = matchEmitNowTicks;
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
                        // Service already found in another datasource - merge without double-counting files
                        var existingService = aggregatedServices.First(s =>
                            s.ServiceName.Equals(service.ServiceName, StringComparison.OrdinalIgnoreCase));
                        GameCacheInfoMergeHelper.MergeService(existingService, service, datasource.Name);
                    }
                }

                _logger.LogInformation("[GameDetection] Datasource '{DatasourceName}' found {GameCount} games, {ServiceCount} services",
                    datasource.Name, detectionResult.Games.Count, detectionResult.Services.Count);

                datasourceIndex++;
            }

            // Check for cancellation before finalizing
            cancellationToken.ThrowIfCancellationRequested();

            // Every full-scan report came from an empty index: nothing about the disk was
            // observed, so replacing a non-empty snapshot would wipe real detections over a
            // mount or path problem. Fail the operation visibly instead; a fresh install
            // (no previous detections) still completes normally with an empty result.
            if (fullScanReports > 0 && emptyIndexFullScanReports == fullScanReports)
            {
                await using var snapshotContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
                var previousDetections = await snapshotContext.CachedGameDetections.CountAsync(cancellationToken);
                if (previousDetections > 0)
                {
                    throw new InvalidOperationException(
                        $"Game detection indexed zero cache files in every scanned cache directory, but {previousDetections} previously detected entries exist. The previous snapshot was kept; verify the datasource cache paths point at real cache data.");
                }
            }

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
                metrics.CompletionContext = completionContext;
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
            // Always run on full scan even with zero services - zero incoming means everything should be evicted
            if (!incremental)
            {
                await SaveServicesToDatabaseAsync(aggregatedServices, cancellationToken);
                _logger.LogInformation("[GameDetection] Services saved to database - {Count} services total", aggregatedServices.Count);
            }

            // The summary recompute is the long pole of the whole scan on large databases
            // (minutes). Give it its own labeled stage at 97% so the bar honestly shows
            // substantial remaining work instead of sitting on the last scan message.
            _operationTracker.UpdateProgress(operationId, 97, "signalr.gameDetect.refreshingSummary");

            await _detectionDataService.RefreshDiskSummaryAsync(cancellationToken);

            // Game detection owns only its own on-disk summary. The cache-file scan (files/size and
            // its own staleness) is a separate scheduled scan and is left untouched here, so a
            // detection run never changes the Cache Files card or clears the cache-file scan.

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
                        // CancellationToken.None is critical - the original token is already cancelled.
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
                await _rustProcessHelper.DeleteTempFileAsync(outputJson);
            }
            if (!string.IsNullOrEmpty(excludedIdsPath))
            {
                await _rustProcessHelper.DeleteTempFileAsync(excludedIdsPath);
            }
            if (!string.IsNullOrEmpty(progressFilePath))
            {
                await _rustProcessHelper.DeleteTempFileAsync(progressFilePath);
            }
        }
    }

    /// <summary>
    /// Finalizes a detection operation by updating the tracker, persisted state, and sending SignalR notification.
    /// Consolidates the common teardown logic shared across success, cancel, and error paths.
    /// </summary>
    private Task FinalizeDetectionAsync(
        Guid operationId, bool success, OperationStatus status, string stageKey, bool cancelled,
        Dictionary<string, object?>? context = null, int? gamesDetected = null, int? servicesDetected = null)
    {
        // Invalidate in-memory detection cache so next dashboard load picks up new data
        InvalidateDetectionCache();

        // Determine error string for tracker (cancelled = "Cancelled by user", failed = stageKey, success = null)
        var trackerError = success ? null : (cancelled ? "Cancelled by user" : stageKey);

        // Extract newGamesCount from context for the terminal emit payload
        int? newGamesCount = null;
        if (context != null && context.TryGetValue("newGamesCount", out var newGamesRaw) && newGamesRaw != null)
        {
            newGamesCount = Convert.ToInt32(newGamesRaw);
        }

        // Stash the completion metrics by value BEFORE CompleteOperation so the onTerminalEmit closure
        // (registered in StartDetectionAsync/RestoreInterruptedOperations) can read them when it fires.
        _terminalPayload = new GameDetectionTerminalPayload(
            StageKey: stageKey,
            Context: context,
            GamesDetected: gamesDetected,
            ServicesDetected: servicesDetected,
            NewGamesCount: newGamesCount,
            Status: status);

        // Mark operation as complete in unified tracker. This fires onTerminalEmit (exactly once,
        // CompletedFlag-gated) which sends the typed GameDetectionComplete record — no direct emit here.
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

        return Task.CompletedTask;
    }

    /// <summary>
    /// Sends the typed <see cref="SignalRNotifications.GameDetectionComplete"/> terminal event.
    /// Invoked EXACTLY ONCE by the unified tracker inside CompleteOperation (CompletedFlag-gated),
    /// for success, error, AND universal force-kill — replacing the old direct emit and the legacy
    /// force-kill switch. Reads the metrics FinalizeDetectionAsync stashed in <see cref="_terminalPayload"/>;
    /// on a force-kill that bypasses Finalize, the payload defaults to a cancelled-shaped record.
    /// </summary>
    private Task EmitDetectionCompleteAsync(Guid operationId, OperationTerminalInfo info, bool showNotification)
    {
        var payload = _terminalPayload;
        var status = info.Cancelled
            ? OperationStatus.Cancelled
            : info.Success
                ? OperationStatus.Completed
                : OperationStatus.Failed;

        // Fall back to a sensible stageKey when a force-kill bypassed FinalizeDetectionAsync.
        var stageKey = !string.IsNullOrEmpty(payload.StageKey)
            ? payload.StageKey
            : info.Cancelled
                ? "signalr.gameDetect.starting.incremental"
                : "signalr.generic.failed";

        var record = new SignalRNotifications.GameDetectionComplete(
            Success: info.Success,
            OperationId: operationId,
            StageKey: stageKey,
            Status: status,
            Cancelled: info.Cancelled,
            TotalGamesDetected: payload.GamesDetected,
            TotalServicesDetected: payload.ServicesDetected,
            NewGamesCount: payload.NewGamesCount,
            Timestamp: DateTime.UtcNow,
            Context: payload.Context,
            Error: info.Error,
            ShowNotification: showNotification);

        // A genuine failure (not cancellation) routes through the uniform failure broadcast so the
        // reason is logged centrally and guaranteed on IOperationComplete.Error; success and cancel
        // stay on the plain broadcast.
        return status == OperationStatus.Failed
            ? _notifications.NotifyOperationFailedAsync(SignalREvents.GameDetectionComplete, record)
            : _notifications.NotifyAllAsync(SignalREvents.GameDetectionComplete, record);
    }

    public DetectionOperationResponse? GetOperationStatus(Guid operationId)
    {
        var opInfo = _operationTracker.GetOperation(operationId);
        if (opInfo == null)
        {
            return null;
        }

        return ToDetectionResponse(opInfo);
    }


    public DetectionOperationResponse? GetActiveOperation()
    {
        var activeOp = _operationTracker.GetActiveOperations(OperationType.GameDetection).FirstOrDefault();
        if (activeOp == null)
        {
            return null;
        }

        return ToDetectionResponse(activeOp);
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
    /// Called once on startup by GameDetectionService - NOT on every dashboard load.
    /// </summary>
    public async Task ReconcileDetectionDataAsync()
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
                "[GameDetection] Trigger #2 reverse-reconcile failed on startup - proceeding with stale data");
        }

        try
        {
            var serviceSelfHealedCount = await CacheReconciliationService.UnevictCachedServiceDetectionsAsync(dbContext, _logger, _detectionDataService, default);
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
                "[ServiceDetection] Service self-heal failed on startup - proceeding with stale data");
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

        await _detectionDataService.RefreshDiskSummaryAsync();
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

    /// <summary>
    /// Recomputes persisted disk-summary totals and clears the in-memory detection cache.
    /// Call once after batch detection mutations (evictions, removals, scans).
    /// </summary>
    public async Task RefreshDiskSummaryAndInvalidateAsync(
        CancellationToken cancellationToken = default,
        Action<int, int>? onPathProgress = null)
    {
        await _detectionDataService.RefreshDiskSummaryAsync(cancellationToken, onPathProgress);
        InvalidateDetectionCache();
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

            // The retained cache deliberately excludes CacheFilePaths: across all rows the paths
            // are millions of strings, and no consumer of the cached response reads them.
            // Path-bearing responses are served per request by GetCachedDetectionWithPathsAsync.
            var result = await LoadDetectionAsync(includeCacheFilePaths: false);
            _cachedDetectionResponse = result;
            return result;
        }
        finally
        {
            _detectionCacheLock.Release();
        }
    }

    /// <summary>
    /// Loads detection results INCLUDING per-entity cache file paths. Never cached: the paths are
    /// the dominant allocation, so they are materialized per request and reclaimed by the GC
    /// instead of living on this singleton.
    /// </summary>
    public Task<DetectionOperationResponse?> GetCachedDetectionWithPathsAsync(
        CancellationToken cancellationToken = default) =>
        LoadDetectionAsync(cancellationToken, includeCacheFilePaths: true);

    private Task<DetectionOperationResponse?> LoadDetectionAsync(
        CancellationToken cancellationToken = default,
        bool includeCacheFilePaths = true) =>
        _detectionDataService.LoadDetectionAsync(cancellationToken, includeCacheFilePaths);

    public async Task InvalidateCacheAsync()
    {
        await _detectionDataService.InvalidateCacheAsync();
        InvalidateDetectionCache();
    }

    /// <summary>
    /// Resolve unknown games in the cache by looking up their depot IDs in SteamDepotMappings.
    /// This updates cached "Unknown Game (Depot X)" entries when mappings become available.
    /// Returns the number of games that were resolved.
    /// </summary>
    public Task<int> ResolveUnknownGamesInCacheAsync(CancellationToken cancellationToken = default) =>
        _unknownGameResolutionService.ResolveUnknownGamesAsync(cancellationToken);

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
                    _logger.LogWarning("[GameDetection] Persisted operationId '{OperationId}' is not a valid Guid - skipping", operationIdString);
                    continue;
                }

                _cancellationTokenSource = new CancellationTokenSource();
                var metadata = new GameDetectionMetrics { ScanType = DetectionScanType.Incremental };

                if (!_operationTracker.TryRestoreOperation(
                        persistedGuid,
                        OperationType.GameDetection,
                        "Game Detection",
                        _cancellationTokenSource,
                        metadata,
                        onTerminalCleanup: () => { _currentTrackerOperationId = null; },
                        // Original trigger's visibility is not persisted, so a restored run defaults to
                        // visible rather than inheriting some later attempt's flag.
                        onTerminalEmit: info => EmitDetectionCompleteAsync(persistedGuid, info, showNotification: true)))
                {
                    // core-7: the tracker did NOT adopt this CTS (ID already in use), so we still own it.
                    // Dispose the just-created CTS before continuing so it is not leaked.
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                    _logger.LogWarning("[GameDetection] Persisted operation {Id} already registered - skipping", persistedGuid);
                    continue;
                }

                _currentTrackerOperationId = persistedGuid;
                _operationTracker.UpdateProgress(persistedGuid, 0, state.Message ?? "Resuming game cache detection...");

                _logger.LogInformation("[GameDetection] Restored interrupted operation {OperationId}", persistedGuid);

                // Restart the detection task with incremental scanning (default)
                var cancellationToken = _cancellationTokenSource.Token;
                _ = Task.Run(async () => await RunDetectionAsync(persistedGuid, incremental: true, showNotification: true, cancellationToken));
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
    private static DetectionOperationResponse ToDetectionResponse(OperationInfo opInfo)
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
            ShowNotification = metrics?.ShowNotification ?? true,
            Games = metrics?.Games,
            Services = metrics?.Services,
            TotalGamesDetected = metrics?.TotalGamesDetected ?? 0,
            TotalServicesDetected = metrics?.TotalServicesDetected ?? 0,
            Error = metrics?.Error,
            // CompletionContext wins (terminal stage); fall back to the latest progress-tick
            // context so mid-run recovery can interpolate placeholder-bearing progress keys.
            Context = metrics?.CompletionContext ?? metrics?.CurrentContext
        };
    }

    private Task SaveGamesToDatabaseAsync(
        List<GameCacheInfo> games,
        bool incremental,
        CancellationToken cancellationToken = default) =>
        _detectionDataService.SaveGamesAsync(games, incremental, cancellationToken);

    /// <summary>
    /// Surfaces entities whose underlying Downloads are already marked <c>IsEvicted = true</c>
    /// but which do not yet have a matching cached detection row. Public so
    /// <see cref="CacheReconciliationService.ReconcileCacheFilesAsync"/> can call it after
    /// every eviction scan - without this, a game whose downloads were evicted but which was
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
        _detectionDataService.SaveServicesAsync(services, cancellationToken);

    private static async Task<List<CachedGameDetection>> GetUnknownGamesCachedAsync(AppDbContext dbContext)
    {
        return await dbContext.CachedGameDetections
            .Where(g => g.GameName.StartsWith("Unknown Game (Depot"))
            .ToListAsync();
    }

    /// <summary>
    /// Deserializes a persisted JSON string-array column. Returns an empty list both when the
    /// column is genuinely empty AND when the JSON fails to parse (logged as a warning) - the two
    /// cases are indistinguishable to callers by design: this backs best-effort display fields
    /// (sample URLs, cache file paths, datasources), not a required value.
    /// </summary>
    private List<string> DeserializeStringList(string? json)
    {
        if (string.IsNullOrEmpty(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[GameDetection] Failed to deserialize JSON string list, treating as empty: {Json}", json);
            return new List<string>();
        }
    }

    /// <summary>
    /// Stable cross-datasource dedup identity for a detected game. Mirrors the persistence
    /// buckets in <see cref="GameCacheDetectionDataService.SaveGamesAsync"/> exactly so the
    /// in-memory aggregation can never collapse distinct games:
    /// <list type="bullet">
    /// <item>Epic games: keyed by <c>EpicAppId</c> (GameAppId is a synthetic non-zero id).</item>
    /// <item>Named (Blizzard/Riot) games: GameAppId is always 0 and EpicAppId is null, so they
    /// are keyed by <c>(Service, GameName)</c>. Keying on GameAppId alone collapsed all of them
    /// into one row.</item>
    /// <item>Steam games: keyed by <c>GameAppId</c> (unique per game).</item>
    /// </list>
    /// The <c></c> separator cannot appear in a service or game name, matching the Rust
    /// composite-key separator, so named keys can never ambiguously collide.
    /// </summary>
    private static string BuildGameIdentityKey(GameCacheInfo game)
    {
        if (game.EpicAppId != null)
        {
            return $"epic:{game.EpicAppId}";
        }

        if (game.GameAppId == 0 && game.Service != null && game.GameName != "")
        {
            return $"named:{game.Service.ToLowerInvariant()}{game.GameName}";
        }

        return $"steam:{game.GameAppId}";
    }

    /// <summary>
    /// Maps a single datasource's Rust progress (0..100) into its equal slice of the global
    /// scanning band (1..30). Each datasource owns <c>29 / datasourceCount</c> percentage points,
    /// so the emitted percent advances through a shared global denominator instead of resetting to
    /// the band floor when the next datasource's Rust process starts at 0. This keeps the scan-phase
    /// percent monotonic across every datasource; single-datasource runs collapse to the original
    /// <c>1 + rustPercent * 29 / 100</c> mapping. Callers apply an additional running-max clamp as a
    /// backstop.
    /// </summary>
    internal static double MapDatasourceScanPercent(double rustPercent, int datasourceIndex, int datasourceCount)
    {
        const double bandStart = 1.0;
        const double bandWidth = 29.0;

        var effectiveCount = datasourceCount < 1 ? 1 : datasourceCount;
        var boundedIndex = Math.Clamp(datasourceIndex, 0, effectiveCount - 1);
        var sliceWidth = bandWidth / effectiveCount;
        var sliceStart = bandStart + (sliceWidth * boundedIndex);
        var clampedRust = Math.Clamp(rustPercent, 0.0, 100.0);
        return sliceStart + (clampedRust * sliceWidth / 100.0);
    }

    private GameCacheInfo ToGameCacheInfo(CachedGameDetection cached)
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

    private ServiceCacheInfo ToServiceCacheInfo(CachedServiceDetection cached)
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

        /// <summary>
        /// Full-scan only (null for incremental reports): how many cache files the Rust walk
        /// indexed. Zero from an existing directory is indistinguishable from a wrong mount or
        /// path, so such a report must not replace previously detected entries.
        /// </summary>
        [System.Text.Json.Serialization.JsonPropertyName("indexed_cache_files")]
        public long? IndexedCacheFiles { get; set; }

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
