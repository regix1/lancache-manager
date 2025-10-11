using SteamKit2;
using SteamKit2.Authentication;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using LancacheManager.Data;
using LancacheManager.Models;
using System.Text.Json;
using LancacheManager.Services;

namespace LancacheManager.Services;

/// <summary>
/// SteamKit2 service for real-time Steam depot mapping using PICS
/// Based on DepotDownloader approach - properly queries Steam for depot-to-app mappings
/// </summary>
public class SteamKit2Service : IHostedService, IDisposable
{
    private readonly ILogger<SteamKit2Service> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SteamService _steamService;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private SteamClient? _steamClient;
    private CallbackManager? _manager;
    private SteamUser? _steamUser;
    private SteamApps? _steamApps;

    private bool _isRunning = false;
    private bool _isLoggedOn = false;
    private bool _intentionalDisconnect = false;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _currentBuildTask;
    private CancellationTokenSource? _currentRebuildCts;
    private int _rebuildActive;
    private bool _disposed;

    // Connection keep-alive for viability checks
    private Timer? _idleDisconnectTimer;
    private DateTime _lastConnectionActivity = DateTime.MinValue;
    private const int ConnectionKeepAliveSeconds = 60; // Keep connection alive for 60 seconds after viability check

    // Scheduling for periodic PICS crawls
    private Timer? _periodicTimer;
    private DateTime _lastCrawlTime = DateTime.MinValue;
    private TimeSpan _crawlInterval = TimeSpan.FromHours(1); // Default: Run incremental updates every hour
    private bool _crawlIncrementalMode = true; // Default: Run incremental scans
    private readonly PicsDataService _picsDataService;
    private uint _lastChangeNumberSeen;
    private bool _lastScanWasForced = false; // Track if the last scan was forced to be full due to Steam requirements

    // depotId -> set of appIds (can be multiple for shared depots)
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    // depotId -> owner appId (from depotfromapp PICS field)
    private readonly ConcurrentDictionary<uint, uint> _depotOwners = new();
    private readonly HashSet<uint> _scannedApps = new();

    // Cache for app names
    private readonly ConcurrentDictionary<uint, string> _appNames = new();

    // Tune batch sizes to stay friendly
    private const int AppBatchSize = 200; // was 400 - Above 200, token/product calls time out more frequently

    // Task completion sources for async operations
    private TaskCompletionSource? _connectedTcs;
    private TaskCompletionSource? _loggedOnTcs;

    // Progress tracking for PICS crawl
    private int _totalAppsToProcess;
    private int _processedApps;
    private int _totalBatches;
    private int _processedBatches;
    private string _currentStatus = "Idle";
    private int _sessionStartDepotCount = 0;  // Track depot count at start of session

    public SteamKit2Service(
        ILogger<SteamKit2Service> logger,
        IServiceScopeFactory scopeFactory,
        SteamService steamService,
        PicsDataService picsDataService,
        IPathResolver pathResolver,
        StateService stateService)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _steamService = steamService;
        _picsDataService = picsDataService;
        _pathResolver = pathResolver;
        _stateService = stateService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SteamKit2Service with PICS depot mapping");

        try
        {
            // Load existing depot mappings from database first
            await LoadExistingDepotMappings();

            // Initialize session tracking to current count (so session shows 0 when idle)
            _sessionStartDepotCount = _depotToAppMappings.Count;

            // Load saved crawl interval from state
            var savedInterval = _stateService.GetCrawlIntervalHours();
            if (savedInterval > 0)
            {
                _crawlInterval = TimeSpan.FromHours(savedInterval);
                _logger.LogInformation("Loaded crawl interval from state: {Hours} hour(s)", savedInterval);
            }

            // Load saved crawl mode from state
            _crawlIncrementalMode = _stateService.GetCrawlIncrementalMode();
            _logger.LogInformation("Loaded crawl mode from state: {Mode}", _crawlIncrementalMode ? "Incremental" : "Full");

            // Load PICS metadata (crawl time and change number) from JSON or state
            await LoadPicsMetadataAsync();

            // Check for interrupted depot processing and resume if needed
            var depotState = _stateService.GetDepotProcessingState();
            if (depotState.IsActive && depotState.RemainingApps.Any())
            {
                _logger.LogInformation("Found interrupted depot processing, will resume after initialization");
                _totalAppsToProcess = depotState.TotalApps;
                _processedApps = depotState.ProcessedApps;
                _totalBatches = depotState.TotalBatches;
                _processedBatches = depotState.ProcessedBatches;
                _currentStatus = depotState.Status;
            }

            // Initialize SteamKit2
            _steamClient = new SteamClient();
            _manager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>();
            _steamApps = _steamClient.GetHandler<SteamApps>();

            // Subscribe to callbacks
            _manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            _manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            _isRunning = true;

            // Start callback handling loop
            _ = Task.Run(() => HandleCallbacks(_cancellationTokenSource.Token), CancellationToken.None);

            // DISABLED: No automatic PICS crawls - users must choose initialization method via UI
            // SetupPeriodicCrawls();

            _logger.LogInformation("SteamKit2Service started with incremental PICS updates every hour");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start SteamKit2Service");
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping SteamKit2Service");

        _isRunning = false;

        // Stop the periodic timer
        _periodicTimer?.Dispose();
        _periodicTimer = null;

        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // Token source already disposed
        }

        _intentionalDisconnect = true;
        await DisconnectFromSteamAsync();

        if (_currentBuildTask is not null)
        {
            try
            {
                await _currentBuildTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Steam depot crawl cancelled during shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Steam depot crawl finished with an error during shutdown");
            }
        }

        _logger.LogInformation("SteamKit2Service stopped");
    }

    private async Task HandleCallbacks(CancellationToken cancellationToken)
    {
        while (_isRunning && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                _manager?.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                await Task.Yield();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in callback handling");
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Enable periodic PICS crawls after initial depot data has been set up
    /// </summary>
    public void EnablePeriodicCrawls()
    {
        if (_periodicTimer != null)
        {
            _logger.LogInformation("Periodic crawls already enabled");
            return;
        }

        _logger.LogInformation("Enabling periodic PICS crawls");
        SetupPeriodicCrawls();
    }

    private void SetupPeriodicCrawls()
    {
        // Don't auto-start any crawls - user must explicitly trigger via UI
        // This prevents unwanted background processing during initialization

        // Don't set up timer if interval is 0 (disabled)
        if (_crawlInterval.TotalHours == 0)
        {
            _logger.LogInformation("Periodic crawls are disabled (interval = 0)");
            return;
        }

        // Set up the periodic timer for subsequent crawls
        _periodicTimer = new Timer(OnPeriodicCrawlTimer, null, _crawlInterval, _crawlInterval);
        _logger.LogInformation($"Scheduled incremental PICS updates every {_crawlInterval.TotalHours} hour(s)");
    }

    private void OnPeriodicCrawlTimer(object? state)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
        {
            return;
        }

        // Use configured scan mode for automatic scheduled scans
        _ = Task.Run(async () =>
        {
            if (!IsRebuildRunning)
            {
                var scanType = _crawlIncrementalMode ? "incremental" : "full";
                _logger.LogInformation("Starting scheduled {ScanType} PICS update", scanType);
                if (TryStartRebuild(_cancellationTokenSource.Token, incrementalOnly: _crawlIncrementalMode))
                {
                    _lastCrawlTime = DateTime.UtcNow;
                }
            }
        });
    }

    private async Task ConnectAndBuildIndexAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        const int maxRetries = 2; // Max 2 retries = 3 total attempts
        int attempt = 0;

        while (attempt <= maxRetries)
        {
            try
            {
                if (attempt > 0)
                {
                    _logger.LogInformation("Retry attempt {Attempt}/{MaxRetries} for PICS crawl", attempt, maxRetries);
                    await Task.Delay(5000, ct); // Wait 5 seconds before retry
                }

                // Stop idle disconnect timer since we're actively using the connection
                StopIdleDisconnectTimer();

                await ConnectAndLoginAsync(ct);
                UpdateConnectionActivity();
                await BuildDepotIndexAsync(ct, incrementalOnly);

                // Check if we still have unmapped downloads after the scan
                if (incrementalOnly)
                {
                    int unmappedCount = await CheckUnmappedDownloadsAsync();
                    if (unmappedCount > 0 && attempt < maxRetries)
                    {
                        _logger.LogWarning("Found {UnmappedCount} unmapped downloads after incremental scan - will retry", unmappedCount);
                        attempt++;
                        continue;
                    }
                    else if (unmappedCount > 0)
                    {
                        _logger.LogWarning("Found {UnmappedCount} unmapped downloads after all retries", unmappedCount);
                    }
                }

                // Success - exit retry loop
                break;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogError(ex, "Failed to connect and build depot index (attempt {Attempt}/{MaxRetries})", attempt + 1, maxRetries + 1);
                attempt++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect and build depot index after all retries");
                break;
            }
        }
    }

    private async Task<int> CheckUnmappedDownloadsAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Count downloads with depot IDs but no game info
            return await context.Downloads
                .Where(d => d.Service.ToLower() == "steam"
                       && d.DepotId.HasValue
                       && !d.GameAppId.HasValue)
                .CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check unmapped downloads");
            return 0;
        }
    }

    private async Task ConnectAndLoginAsync(CancellationToken ct)
    {
        if (_isLoggedOn && _steamClient?.IsConnected == true)
        {
            _logger.LogDebug("Already connected to Steam; skipping logon");
            return;
        }

        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

        _logger.LogInformation("Connecting to Steam...");
        _steamClient!.Connect();

        // Wait for connected
        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(30), ct);

        // Check if we have a saved refresh token for authenticated login
        var refreshToken = _stateService.GetSteamRefreshToken();
        var authMode = _stateService.GetSteamAuthMode();

        if (!string.IsNullOrEmpty(refreshToken) && authMode == "authenticated")
        {
            var username = _stateService.GetSteamUsername();
            _logger.LogInformation("Logging in with saved refresh token for user: {Username}", username);

            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });
        }
        else
        {
            _logger.LogInformation("Logging in anonymously...");
            _steamUser!.LogOnAnonymous();
        }

        // Wait for logged on
        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(30), ct);
    }

    private async Task WaitForTaskWithTimeout(Task task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

        if (completedTask != task)
        {
            throw new TimeoutException("Operation timed out");
        }

        await task; // Rethrow if faulted
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _logger.LogInformation("Connected to Steam");
        _connectedTcs?.TrySetResult();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        // Log as info if intentional, warning if unexpected
        if (_intentionalDisconnect)
        {
            _logger.LogInformation("Disconnected from Steam (intentional)");
            _intentionalDisconnect = false;
        }
        else
        {
            _logger.LogWarning("Disconnected from Steam");
        }
        _isLoggedOn = false;

        if (!_connectedTcs?.Task.IsCompleted ?? false)
        {
            _connectedTcs?.TrySetException(new Exception("Disconnected during connect"));
        }

        // Only reconnect if we're running AND there's an active rebuild
        // This prevents endless reconnection loops after PICS crawls complete
        if (_isRunning && IsRebuildRunning)
        {
            _logger.LogInformation("Unexpected disconnection during active rebuild - attempting to reconnect in 5 seconds...");
            Task.Delay(5000, _cancellationTokenSource.Token).ContinueWith(_ =>
            {
                if (_isRunning && IsRebuildRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _steamClient?.Connect();
                }
            }, _cancellationTokenSource.Token);
        }
        else
        {
            _logger.LogDebug("Steam disconnected normally - no reconnection needed");
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _isLoggedOn = true;
            _loggedOnTcs?.TrySetResult();
            _logger.LogInformation("Successfully logged onto Steam!");
        }
        else
        {
            _loggedOnTcs?.TrySetException(new Exception($"Logon failed: {callback.Result} / {callback.ExtendedResult}"));
            _logger.LogError($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _logger.LogWarning($"Logged off of Steam: {callback.Result}");
        _isLoggedOn = false;
    }

    /// <summary>
    /// Build depot index by querying PICS for all apps
    /// </summary>
    private async Task BuildDepotIndexAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        try
        {
            _currentStatus = "Connecting and enumerating apps";
            _logger.LogInformation("Starting to build depot index via Steam PICS (incremental={Incremental}, Current mappings in memory: {Count})...",
                incrementalOnly, _depotToAppMappings.Count);

            // Always check database for existing data first
            int databaseDepotCount = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                databaseDepotCount = await context.SteamDepotMappings.CountAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to count database depot mappings");
            }

            // Load existing data from JSON file if it exists
            var existingData = await _picsDataService.LoadPicsDataFromJsonAsync();
            bool hasExistingJsonData = existingData?.DepotMappings?.Any() == true;
            bool hasExistingDatabaseData = databaseDepotCount > 0;
            bool hasExistingData = hasExistingJsonData || hasExistingDatabaseData;

            if (!incrementalOnly)
            {
                // Full rebuild - start fresh, don't load change number
                _logger.LogInformation("Starting full PICS generation (will use Web API for app enumeration)");
                _depotToAppMappings.Clear();
                _appNames.Clear();
                _lastChangeNumberSeen = 0; // Reset to ensure Web API enumeration is used
            }
            else if (incrementalOnly)
            {
                // For incremental updates, load existing mappings and change number
                _logger.LogInformation("Incremental update mode - loading existing data");

                // Load existing mappings from database
                await LoadExistingDepotMappings();

                // Load change number from JSON if available
                if (hasExistingJsonData && existingData?.Metadata != null)
                {
                    _lastChangeNumberSeen = existingData.Metadata.LastChangeNumber;
                    _logger.LogInformation("Loaded change number {ChangeNumber} from existing data", _lastChangeNumberSeen);

                    // Merge existing depot mappings from JSON
                    var (jsonMappingsMerged, _) = MergeDepotMappingsFromJson(existingData);
                    if (jsonMappingsMerged > 0)
                    {
                        _logger.LogInformation("Merged {Count} existing mappings from JSON", jsonMappingsMerged);
                    }
                }

                _logger.LogInformation("Starting incremental update with {Count} existing depot mappings", _depotToAppMappings.Count);
            }

            // Enumerate every appid by crawling the PICS changelist with retry logic
            List<uint> appIds = new List<uint>();
            int retryCount = 0;
            const int maxRetries = 3;

            while (retryCount < maxRetries)
            {
                try
                {
                    appIds = await EnumerateAllAppIdsViaPicsChangesAsync(ct, incrementalOnly);
                    _logger.LogInformation("Retrieved {Count} app IDs from PICS", appIds.Count);
                    break; // Success, exit retry loop
                }
                catch (OperationCanceledException) when (retryCount < maxRetries - 1)
                {
                    retryCount++;
                    _logger.LogWarning("PICS enumeration timed out or was cancelled (attempt {Attempt}/{Max}), retrying in 10 seconds...",
                        retryCount, maxRetries);

                    // Wait before retrying, but check if we're being cancelled
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        // If the delay was cancelled, propagate the cancellation
                        throw;
                    }

                    // Ensure we're still connected before retrying
                    if (!_isLoggedOn)
                    {
                        await ConnectAndLoginAsync(ct);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Final retry failed or explicit cancellation, propagate
                    _logger.LogError("PICS enumeration failed after {Attempts} attempts", retryCount + 1);
                    throw;
                }
            }

            var allBatches = appIds
                .Where(id => !_scannedApps.Contains(id))
                .Chunk(AppBatchSize)
                .ToList();

            _totalAppsToProcess = appIds.Count;
            _processedApps = 0;
            _totalBatches = allBatches.Count;
            _processedBatches = 0;
            _currentStatus = "Processing app data";

            // Reset session start count to track new mappings found
            _sessionStartDepotCount = _depotToAppMappings.Count;
            _logger.LogInformation("Session start depot count: {Count}", _sessionStartDepotCount);

            _logger.LogInformation("Processing {BatchCount} appinfo batches via PICS", allBatches.Count);

            foreach (var batch in allBatches)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogDebug("Processing batch {BatchNum}/{TotalBatches} with {AppCount} apps",
                        _processedBatches + 1, allBatches.Count, batch.Length);

                    // Acquire access tokens for protected appinfo entries when available
                    var tokensJob = _steamApps!.PICSGetAccessTokens(batch, Enumerable.Empty<uint>());
                    var tokens = await WaitForCallbackAsync(tokensJob, ct);

                    _logger.LogDebug("Received {GrantedTokens} granted tokens and {DeniedTokens} denied tokens for batch",
                        tokens.AppTokens.Count, tokens.AppTokensDenied.Count);

                    // Prepare the product info request with tokens attached when needed
                    var appRequests = new List<SteamApps.PICSRequest>(batch.Length);
                    foreach (var appId in batch)
                    {
                        var request = new SteamApps.PICSRequest(appId);
                        if (tokens.AppTokens.TryGetValue(appId, out var token))
                        {
                            request.AccessToken = token;
                            _logger.LogTrace("Using access token for app {AppId}", appId);
                        }

                        appRequests.Add(request);
                    }

                    var productJob = _steamApps.PICSGetProductInfo(appRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                    var productCallbacks = await WaitForAllProductInfoAsync(productJob, ct);

                    int appsInThisBatch = 0, unknownInThisBatch = 0;
                    var dlcAppsToScan = new List<uint>();

                    foreach (var cb in productCallbacks)
                    {
                        appsInThisBatch += cb.Apps.Count;
                        unknownInThisBatch += cb.UnknownApps.Count;

                        foreach (var app in cb.Apps.Values)
                        {
                            var dlcList = ProcessAppDepots(app);
                            dlcAppsToScan.AddRange(dlcList);
                            _processedApps++;
                        }
                    }

                    // Process DLC apps found in this batch
                    if (dlcAppsToScan.Count > 0)
                    {
                        _logger.LogInformation("Found {Count} DLC apps to scan in this batch", dlcAppsToScan.Count);

                        // Process DLC apps in smaller sub-batches
                        var dlcBatches = dlcAppsToScan.Distinct().Chunk(50).ToList();
                        foreach (var dlcBatch in dlcBatches)
                        {
                            try
                            {
                                var dlcTokensJob = _steamApps.PICSGetAccessTokens(dlcBatch, Enumerable.Empty<uint>());
                                var dlcTokens = await WaitForCallbackAsync(dlcTokensJob, ct);

                                var dlcAppRequests = new List<SteamApps.PICSRequest>();
                                foreach (var dlcAppId in dlcBatch)
                                {
                                    var request = new SteamApps.PICSRequest(dlcAppId);
                                    if (dlcTokens.AppTokens.TryGetValue(dlcAppId, out var token))
                                    {
                                        request.AccessToken = token;
                                    }
                                    dlcAppRequests.Add(request);
                                }

                                var dlcProductJob = _steamApps.PICSGetProductInfo(dlcAppRequests, Enumerable.Empty<SteamApps.PICSRequest>());
                                var dlcProductCallbacks = await WaitForAllProductInfoAsync(dlcProductJob, ct);

                                foreach (var dlcCb in dlcProductCallbacks)
                                {
                                    foreach (var dlcApp in dlcCb.Apps.Values)
                                    {
                                        ProcessAppDepots(dlcApp);  // Don't need to scan DLC's DLCs recursively
                                        _processedApps++;
                                    }
                                }

                                await Task.Delay(100, ct);  // Small delay between DLC batches
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to process DLC batch. Continuing...");
                            }
                        }
                    }

                    _logger.LogDebug(
                        "Received product info for {Apps} apps, {Unknown} unknown across {Parts} parts",
                        appsInThisBatch, unknownInThisBatch, productCallbacks.Count);

                    _processedBatches++;

                    // More frequent progress updates for better visibility
                    if (_processedBatches % 5 == 0)
                    {
                        _logger.LogInformation(
                            "Processed {Processed}/{Total} batches ({Percent:F1}%); depot mappings found={Mappings}",
                            _processedBatches,
                            allBatches.Count,
                            (_processedBatches * 100.0 / allBatches.Count),
                            _depotToAppMappings.Count);

                        // Use merge for incremental, full save for complete rebuild
                        await SaveAllMappingsToJsonAsync(incrementalOnly);
                    }

                    // Adaptive delay based on current performance
                    var delayMs = _depotToAppMappings.Count > 1000 ? 100 : 150;
                    await Task.Delay(delayMs, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process batch {Batch}/{Total}. Continuing...",
                        _processedBatches + 1, allBatches.Count);
                }
            }

            _currentStatus = "Saving PICS data to JSON";
            _logger.LogInformation("Depot index built. Total depot mappings: {Count}", _depotToAppMappings.Count);

            // DISABLED: Using OwnerId from PICS data instead of log-based fallback
            // Add log-based fallback mappings for depots that appear in actual downloads
            // Only adds mappings where depotId = appId and no existing mapping exists
            // await AddLogBasedFallbackDepotMappingsAsync();

            // Final save - use merge for incremental, full save for complete rebuild
            await SaveAllMappingsToJsonAsync(incrementalOnly);

            _currentStatus = "Importing to database";
            // Import to database BEFORE updating downloads so mappings are available
            try
            {
                await ImportJsonToDatabase();
                _logger.LogInformation("Database import completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Database import failed");
            }

            // Auto-apply depot mappings to downloads after PICS data is ready
            _currentStatus = "Applying depot mappings";
            _logger.LogInformation("Automatically applying depot mappings after PICS completion");
            await UpdateDownloadsWithDepotMappings();

            _currentStatus = "Complete";
            _lastCrawlTime = DateTime.UtcNow;
            SaveLastCrawlTime();
        }
        catch (Exception ex)
        {
            _currentStatus = "Error occurred";
            _logger.LogError(ex, "Error building depot index");
        }
    }

    private static uint? AsUInt(KeyValue kv)
    {
        if (kv == KeyValue.Invalid || kv.Value == null) return null;
        if (uint.TryParse(kv.AsString() ?? string.Empty, out var v)) return v;
        return null;
    }

    private List<uint> ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
    {
        var dlcAppIdsToScan = new List<uint>();

        try
        {
            var appId = app.ID;
            var kv = app.KeyValues;

            var appinfo = kv["appinfo"];
            var common = appinfo != KeyValue.Invalid ? appinfo["common"] : kv["common"];
            var depots = appinfo != KeyValue.Invalid ? appinfo["depots"] : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            var appType = common?["type"]?.AsString()?.ToLower() ?? "unknown";
            _appNames[appId] = appName;

            // Extract DLC list for DLC depot discovery
            var listofdlc = common?["listofdlc"];
            if (listofdlc != KeyValue.Invalid && listofdlc?.Children != null)
            {
                foreach (var dlcChild in listofdlc.Children)
                {
                    if (uint.TryParse(dlcChild.AsString(), out var dlcAppId))
                    {
                        // Add DLC to scan list if not already processed
                        if (!_appNames.ContainsKey(dlcAppId))
                        {
                            dlcAppIdsToScan.Add(dlcAppId);
                        }
                    }
                }
            }

            if (depots == KeyValue.Invalid)
            {
                return dlcAppIdsToScan;
            }

            foreach (var child in depots.Children)
            {
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                var ownerFromPics = AsUInt(child["depotfromapp"]);
                var ownerAppId = ownerFromPics ?? appId;

                // Self-referencing depots (depot ID = app ID) are valid owner depots
                // This includes both base games and DLCs
                var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                set.Add(ownerAppId);

                // Store the owner app for this depot
                _depotOwners.TryAdd(depotId, ownerAppId);

                // Store owner app name
                if (ownerFromPics.HasValue && !_appNames.ContainsKey(ownerAppId))
                {
                    _appNames[ownerAppId] = $"App {ownerAppId}";
                }
            }

            _scannedApps.Add(appId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Warning: Error processing app {AppId}", app.ID);
        }

        return dlcAppIdsToScan;
    }


    private async Task<List<uint>> EnumerateAllAppIdsViaPicsChangesAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        if (_steamApps is null)
        {
            throw new InvalidOperationException("SteamApps handler not ready.");
        }

        var allApps = new HashSet<uint>();

        // Only use Web API for full update when there's truly no existing data
        // If we have mappings loaded (from JSON or DB), use PICS even with change number 0
        bool hasExistingMappings = _depotToAppMappings.Count > 0;

        if (!incrementalOnly && _lastChangeNumberSeen == 0 && !hasExistingMappings)
        {
            _logger.LogInformation("Full update mode with no existing data: Using Steam Web API to get all app IDs");
            var webApiApps = await TryGetAllAppIdsFromWebApiAsync();
            _logger.LogInformation("Retrieved {Count} app IDs from Steam Web API", webApiApps.Count);

            // Get and save current change number so future incremental updates work
            var changeNumberJob = _steamApps.PICSGetChangesSince(0, false, false);
            var changeNumberResult = await WaitForCallbackAsync(changeNumberJob, ct);
            _lastChangeNumberSeen = changeNumberResult.CurrentChangeNumber;
            _logger.LogInformation("Initialized change number to {ChangeNumber} for future incremental updates", _lastChangeNumberSeen);

            return webApiApps;
        }

        if (incrementalOnly && hasExistingMappings && _lastChangeNumberSeen == 0)
        {
            _logger.LogInformation("Incremental mode with existing data but no change number - will use PICS to check for updates");
        }

        // For incremental updates, use PICS changes
        uint since = 0;

        // Get current change number
        var initialJob = _steamApps.PICSGetChangesSince(0, false, false);
        var initialChanges = await WaitForCallbackAsync(initialJob, ct);
        var currentChangeNumber = initialChanges.CurrentChangeNumber;

        // Use saved change number for incremental, or start from recent point if we have existing data
        if (incrementalOnly && _lastChangeNumberSeen > 0)
        {
            since = _lastChangeNumberSeen;
            _logger.LogInformation("Incremental update from saved change #{FromChange} to #{CurrentChange}", since, currentChangeNumber);
        }
        else if (incrementalOnly && hasExistingMappings && _lastChangeNumberSeen == 0)
        {
            // Have data but no change number - be more aggressive and scan more changes
            since = Math.Max(0, currentChangeNumber - 50000); // Last ~50k changes (about 2-3 months) - more aggressive
            _logger.LogInformation("Incremental update with existing data but no change number - starting from recent change #{FromChange} to #{CurrentChange} (aggressive scan)", since, currentChangeNumber);
        }
        else
        {
            // Full mode or no existing data - start from recent point for partial updates
            since = Math.Max(0, currentChangeNumber - 50000);
            _logger.LogInformation("Enumerating from change #{FromChange} to #{CurrentChange}", since, currentChangeNumber);
        }

        // Update change number NOW so it gets saved during batch processing
        if (currentChangeNumber > _lastChangeNumberSeen)
        {
            _lastChangeNumberSeen = currentChangeNumber;
            _logger.LogInformation("Updated change number to {ChangeNumber} for this crawl", _lastChangeNumberSeen);
        }

        int consecutiveFullUpdates = 0;
        const int maxFullUpdates = 3;

        while (since < currentChangeNumber && consecutiveFullUpdates < maxFullUpdates)
        {
            ct.ThrowIfCancellationRequested();

            var job = _steamApps.PICSGetChangesSince(since, true, true);
            var changes = await WaitForCallbackAsync(job, ct);

            if (changes.RequiresFullUpdate || changes.RequiresFullAppUpdate)
            {
                consecutiveFullUpdates++;
                _logger.LogWarning("PICS requesting full update, falling back to Web API");
                _lastScanWasForced = true; // Mark that this scan was forced to be full

                // Fall back to Web API
                var webApiApps = await TryGetAllAppIdsFromWebApiAsync();
                _logger.LogInformation("Retrieved {Count} app IDs from Steam Web API fallback", webApiApps.Count);

                // Update change number even when falling back to Web API so future incremental updates work
                if (currentChangeNumber > _lastChangeNumberSeen)
                {
                    _lastChangeNumberSeen = currentChangeNumber;
                    _logger.LogInformation("Updated change number to {ChangeNumber} after Web API fallback", _lastChangeNumberSeen);
                }

                return webApiApps;
            }

            consecutiveFullUpdates = 0;

            foreach (var change in changes.AppChanges)
            {
                allApps.Add(change.Key);
            }

            var last = changes.LastChangeNumber;
            if (last <= since)
            {
                if (changes.AppChanges.Count == 0)
                {
                    since += 500;
                    await Task.Delay(100, ct);
                    continue;
                }
                last = (uint)Math.Min((long)currentChangeNumber, (long)since + Math.Max(1, changes.AppChanges.Count));
            }

            since = last;

            if (allApps.Count >= 500000)
                break;

            await Task.Delay(100, ct);
        }

        var list = allApps.ToList();
        list.Sort();

        _logger.LogInformation("PICS enumeration complete. Found {Count} apps", list.Count);
        return list;
    }

    private async Task<T> WaitForCallbackAsync<T>(AsyncJob<T> job, CancellationToken ct, TimeSpan? timeout = null) where T : CallbackMsg
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("Callback manager not initialized.");
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var jobId = job.JobID;

        Action<T>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                tcs.TrySetResult(callback);
            }
        };

        using var subscription = _manager.Subscribe(handler!);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Use longer timeout for PICS operations which can be slow with unreliable connections
        linkedCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));

        while (!tcs.Task.IsCompleted)
        {
            // Check if we're still connected
            if (!_isLoggedOn || _steamClient?.IsConnected != true)
            {
                // Give it a moment for auto-reconnection to kick in
                await Task.Delay(TimeSpan.FromSeconds(1), linkedCts.Token);

                // If still not connected after waiting, throw a more descriptive exception
                if (!_isLoggedOn || _steamClient?.IsConnected != true)
                {
                    throw new InvalidOperationException("Lost connection to Steam during PICS operation");
                }
            }

            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            linkedCts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }

        return await tcs.Task;
    }

    private async Task<IReadOnlyList<SteamApps.PICSProductInfoCallback>> WaitForAllProductInfoAsync(
        AsyncJobMultiple<SteamApps.PICSProductInfoCallback> job,
        CancellationToken ct)
    {
        if (_manager is null)
        {
            throw new InvalidOperationException("Callback manager not initialized.");
        }

        var callbacks = new List<SteamApps.PICSProductInfoCallback>();
        var jobId = job.JobID;
        var isCompleted = false;

        Action<SteamApps.PICSProductInfoCallback>? handler = null;
        handler = callback =>
        {
            if (callback.JobID == jobId)
            {
                callbacks.Add(callback);
                if (!callback.ResponsePending) // This is the final callback
                {
                    isCompleted = true;
                }
            }
        };

        using var subscription = _manager.Subscribe(handler!);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(TimeSpan.FromMinutes(10)); // batches can be slow, especially with reconnections

        while (!isCompleted && !linkedCts.Token.IsCancellationRequested)
        {
            _manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
            await Task.Yield();
        }

        linkedCts.Token.ThrowIfCancellationRequested();
        return callbacks.AsReadOnly();
    }

    private async Task SaveAllMappingsToJsonAsync(bool incrementalOnly = false)
    {
        try
        {
            // Convert ConcurrentDictionary to Dictionary for the service call
            var (depotMappingsDict, appNamesDict, depotOwnersDict) = ConvertMappingsDictionaries();

            if (incrementalOnly)
            {
                // Pass validateExisting=true to clean up corrupted entries during incremental updates
                await _picsDataService.MergePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, validateExisting: true, depotOwners: depotOwnersDict);
                _logger.LogInformation(
                    "Merged {DepotCount} unique depot mappings to JSON (incremental); JSON metadata totals will list depot/app pairs when depots are shared",
                    depotMappingsDict.Count);
            }
            else
            {
                await _picsDataService.SavePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, depotOwners: depotOwnersDict);
                _logger.LogInformation(
                    "Saved {DepotCount} unique depot mappings to JSON file (full); JSON metadata totals will list depot/app pairs when depots are shared",
                    _depotToAppMappings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save depot mappings to JSON file");
        }
    }

    /// <summary>
    /// Import JSON data to database after PICS crawl
    /// </summary>
    private async Task ImportJsonToDatabase()
    {
        try
        {
            await _picsDataService.ImportJsonDataToDatabaseAsync();
            _logger.LogInformation("Successfully imported PICS JSON data to database");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import PICS JSON data to database");
        }
    }

    /// <summary>
    /// Get all app IDs from Steam Web API (no auth needed)
    /// </summary>
    private async Task<List<uint>> TryGetAllAppIdsFromWebApiAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        var json = await http.GetStringAsync("https://api.steampowered.com/ISteamApps/GetAppList/v2/");
        using var doc = JsonDocument.Parse(json);

        var apps = doc.RootElement.GetProperty("applist").GetProperty("apps");
        var ids = new List<uint>(apps.GetArrayLength());
        foreach (var e in apps.EnumerateArray())
        {
            if (e.TryGetProperty("appid", out var idElem) && idElem.TryGetUInt32(out var id))
                ids.Add(id);
        }

        ids.Sort();
        return ids;
    }

    /// <summary>
    /// Get current depot mapping count (always from database for accuracy)
    /// </summary>
    public int GetDepotMappingCount()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            return context.SteamDepotMappings.Count();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database for depot mapping count");
            return 0;
        }
    }

    /// <summary>
    /// Check if service is ready
    /// </summary>
    public bool IsReady => _isLoggedOn && _steamClient?.IsConnected == true && _depotToAppMappings.Count > 0 && !IsRebuildRunning;

    /// <summary>
    /// Get or set the crawl interval in hours
    /// </summary>
    public double CrawlIntervalHours
    {
        get => _crawlInterval.TotalHours;
        set
        {
            _crawlInterval = TimeSpan.FromHours(value);

            // Save to state for persistence across restarts
            _stateService.SetCrawlIntervalHours(value);

            if (value == 0)
            {
                // Disable periodic crawls
                _logger.LogInformation("Automatic crawl schedule disabled");

                // Stop and dispose the timer
                if (_periodicTimer != null)
                {
                    _periodicTimer?.Dispose();
                    _periodicTimer = null;
                    _logger.LogInformation("Stopped periodic crawl timer");
                }
            }
            else
            {
                _logger.LogInformation("Saved crawl interval to state: {Hours} hour(s)", value);

                // Reset the last crawl time to now so the countdown starts fresh with the new interval
                _lastCrawlTime = DateTime.UtcNow;
                _stateService.SetLastPicsCrawl(_lastCrawlTime);
                _logger.LogInformation("Reset last crawl time to now due to interval change");

                // Restart the timer with new interval if it's running
                if (_periodicTimer != null)
                {
                    _periodicTimer?.Dispose();
                    _periodicTimer = new Timer(OnPeriodicCrawlTimer, null, _crawlInterval, _crawlInterval);
                    _logger.LogInformation($"Updated crawl interval to {value} hour(s)");
                }
            }
        }
    }

    /// <summary>
    /// Get or set whether automatic scans should be incremental (true) or full (false)
    /// </summary>
    public bool CrawlIncrementalMode
    {
        get => _crawlIncrementalMode;
        set
        {
            _crawlIncrementalMode = value;

            // Save to state for persistence across restarts
            _stateService.SetCrawlIncrementalMode(value);
            _logger.LogInformation("Saved crawl mode to state: {Mode}", value ? "Incremental" : "Full");
        }
    }

    /// <summary>
    /// Check if incremental scan is viable or if change gap is too large (will trigger full scan)
    /// </summary>
    public async Task<object> CheckIncrementalViabilityAsync(CancellationToken ct)
    {
        try
        {
            // Always load the latest change number from JSON to ensure viability check matches what the scan will use
            // (scan always reloads from JSON, so viability check must too)
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            uint changeNumberToCheck = 0;
            if (picsData?.Metadata?.LastChangeNumber > 0)
            {
                changeNumberToCheck = picsData.Metadata.LastChangeNumber;
                _logger.LogInformation("Viability check will use change number {ChangeNumber} from JSON file", changeNumberToCheck);
            }

            // Need to be connected to check current change number
            bool wasConnected = _isLoggedOn && _steamClient?.IsConnected == true;

            if (!wasConnected)
            {
                await ConnectAndLoginAsync(ct);
            }

            try
            {
                // Get current change number from Steam
                var job = _steamApps!.PICSGetChangesSince(0, false, false);
                var changes = await WaitForCallbackAsync(job, ct);
                var currentChangeNumber = changes.CurrentChangeNumber;

                uint changeGap = changeNumberToCheck > 0
                    ? currentChangeNumber - changeNumberToCheck
                    : currentChangeNumber;

                // Actually check with Steam if it will accept incremental update
                bool willRequireFullScan = false;
                if (changeNumberToCheck > 0)
                {
                    _logger.LogInformation("Checking with Steam if incremental update is viable (last: {Last}, current: {Current}, gap: {Gap})",
                        changeNumberToCheck, currentChangeNumber, changeGap);

                    var incrementalJob = _steamApps!.PICSGetChangesSince(changeNumberToCheck, true, true);
                    var incrementalChanges = await WaitForCallbackAsync(incrementalJob, ct);

                    // Steam will tell us if it requires a full update
                    willRequireFullScan = incrementalChanges.RequiresFullUpdate || incrementalChanges.RequiresFullAppUpdate;

                    _logger.LogInformation("Steam RequiresFullUpdate: {Full}, RequiresFullAppUpdate: {App}",
                        incrementalChanges.RequiresFullUpdate, incrementalChanges.RequiresFullAppUpdate);
                }

                return new
                {
                    isViable = !willRequireFullScan,
                    lastChangeNumber = changeNumberToCheck,
                    currentChangeNumber = currentChangeNumber,
                    changeGap = changeGap,
                    isLargeGap = willRequireFullScan,
                    willTriggerFullScan = willRequireFullScan,
                    estimatedAppsToScan = willRequireFullScan ? 270000 : (int)Math.Min(changeGap * 2, 50000) // Rough estimate
                };
            }
            finally
            {
                // Keep connection alive for a short period if we just connected
                // This allows reuse if a crawl starts immediately after viability check
                if (!wasConnected && _steamClient?.IsConnected == true)
                {
                    _logger.LogDebug("Keeping Steam connection alive for {Seconds} seconds for potential reuse", ConnectionKeepAliveSeconds);
                    UpdateConnectionActivity();
                    StartIdleDisconnectTimer();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking incremental viability");

            // Try to get the change number from JSON for error reporting
            uint changeNumberForError = 0;
            try
            {
                var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
                changeNumberForError = picsData?.Metadata?.LastChangeNumber ?? 0;
            }
            catch { }

            // If we can't check viability, assume full scan is required for safety
            return new
            {
                isViable = false,
                lastChangeNumber = changeNumberForError,
                currentChangeNumber = (uint)0,
                changeGap = (uint)0,
                isLargeGap = true,
                willTriggerFullScan = true,
                estimatedAppsToScan = 270000,
                error = ex.Message
            };
        }
    }

    public object GetProgress()
    {
        // If never crawled, initialize to current time so next crawl is in the future
        if (_lastCrawlTime == DateTime.MinValue)
        {
            _lastCrawlTime = DateTime.UtcNow;
        }

        var timeSinceLastCrawl = DateTime.UtcNow - _lastCrawlTime;
        var nextCrawlIn = TimeSpan.FromTicks(Math.Max(0, (_crawlInterval - timeSinceLastCrawl).Ticks)); // Clamp to zero if negative

        var totalMappings = _depotToAppMappings.Count;
        var newMappingsInSession = Math.Max(0, totalMappings - _sessionStartDepotCount);

        return new
        {
            IsRunning = IsRebuildRunning,
            Status = _currentStatus,
            TotalApps = _totalAppsToProcess,
            ProcessedApps = _processedApps,
            TotalBatches = _totalBatches,
            ProcessedBatches = _processedBatches,
            ProgressPercent = _totalBatches > 0 ? (_processedBatches * 100.0 / _totalBatches) : 0,
            DepotMappingsFound = totalMappings,
            DepotMappingsFoundInSession = newMappingsInSession,
            IsReady = IsReady,
            LastCrawlTime = _lastCrawlTime == DateTime.MinValue ? (DateTime?)null : _lastCrawlTime,
            NextCrawlIn = nextCrawlIn.TotalSeconds, // Return as a number (total seconds) instead of TimeSpan object
            CrawlIntervalHours = _crawlInterval.TotalHours,
            CrawlIncrementalMode = _crawlIncrementalMode,
            LastScanWasForced = _lastScanWasForced,
            IsConnected = _steamClient?.IsConnected == true,
            IsLoggedOn = _isLoggedOn
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isRunning = false;

        // Clean up timers
        _periodicTimer?.Dispose();
        _periodicTimer = null;
        _idleDisconnectTimer?.Dispose();
        _idleDisconnectTimer = null;

        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    _cancellationTokenSource.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The token source was already disposed by the host.
                }
            }

            _steamClient?.Disconnect();
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    public bool TryStartRebuild(CancellationToken cancellationToken = default, bool incrementalOnly = false)
    {
        if (Interlocked.CompareExchange(ref _rebuildActive, 1, 0) != 0)
        {
            _logger.LogDebug("Steam PICS depot crawl already running; skipping trigger");
            return false;
        }

        _logger.LogInformation("Starting Steam PICS depot crawl");
        _lastScanWasForced = false; // Reset flag at start of new scan

        // Dispose previous cancellation token source if it exists
        _currentRebuildCts?.Dispose();

        try
        {
            _currentRebuildCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            // Main cancellation token source was disposed, use provided token or none
            _currentRebuildCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
        }

        async Task RunAsync()
        {
            try
            {
                await ConnectAndBuildIndexAsync(_currentRebuildCts.Token, incrementalOnly).ConfigureAwait(false);
                _logger.LogInformation("PICS crawl completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Steam PICS depot crawl cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Steam PICS depot crawl failed");
            }
            finally
            {
                // Clear rebuild flag BEFORE disconnecting to prevent reconnection attempts
                Interlocked.Exchange(ref _rebuildActive, 0);

                // Explicitly disconnect after crawl completion to prevent reconnection loops
                if (_steamClient?.IsConnected == true)
                {
                    _logger.LogDebug("Disconnecting from Steam after PICS crawl completion");
                    _intentionalDisconnect = true;
                    await DisconnectFromSteamAsync();
                }

                // Dispose the cancellation token source
                _currentRebuildCts?.Dispose();
                _currentRebuildCts = null;
            }
        }

        _currentBuildTask = Task.Run(RunAsync, CancellationToken.None);
        return true;
    }

    public bool IsRebuildRunning => Interlocked.CompareExchange(ref _rebuildActive, 0, 0) == 1;

    /// <summary>
    /// Merge JSON-backed depot mappings into the in-memory dictionaries.
    /// </summary>
    private (int mappingsMerged, bool changeNumberUpdated) MergeDepotMappingsFromJson(PicsJsonData? jsonData)
    {
        if (jsonData?.DepotMappings == null || jsonData.DepotMappings.Count == 0)
        {
            return (0, false);
        }

        int mappingsMerged = 0;

        foreach (var mappingEntry in jsonData.DepotMappings)
        {
            if (!uint.TryParse(mappingEntry.Key, out var depotId))
            {
                continue;
            }

            var mapping = mappingEntry.Value;
            if (mapping?.AppIds == null)
            {
                continue;
            }

            var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());

            foreach (var appId in mapping.AppIds)
            {
                if (set.Add(appId))
                {
                    mappingsMerged++;
                }
            }

            // Use explicit OwnerId if available, otherwise fallback to first app in array
            if (mapping.OwnerId.HasValue)
            {
                _depotOwners.TryAdd(depotId, mapping.OwnerId.Value);
            }
            else if (mapping.AppIds.Count > 0)
            {
                _depotOwners.TryAdd(depotId, mapping.AppIds[0]);
            }

            if (mapping.AppNames?.Any() == true && mapping.AppIds.Count == mapping.AppNames.Count)
            {
                for (int i = 0; i < mapping.AppIds.Count; i++)
                {
                    _appNames.TryAdd(mapping.AppIds[i], mapping.AppNames[i]);
                }
            }
        }

        var changeNumberUpdated = false;
        if (jsonData.Metadata?.LastChangeNumber > 0 && jsonData.Metadata.LastChangeNumber > _lastChangeNumberSeen)
        {
            _lastChangeNumberSeen = jsonData.Metadata.LastChangeNumber;
            changeNumberUpdated = true;
        }

        return (mappingsMerged, changeNumberUpdated);
    }

    /// <summary>
    /// Load existing depot mappings from database on startup
    /// </summary>
    private async Task LoadExistingDepotMappings()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingMappings = await context.SteamDepotMappings.ToListAsync();

            foreach (var mapping in existingMappings)
            {
                var set = _depotToAppMappings.GetOrAdd(mapping.DepotId, _ => new HashSet<uint>());
                set.Add(mapping.AppId);

                // Track owner apps from database
                if (mapping.IsOwner)
                {
                    _depotOwners.TryAdd(mapping.DepotId, mapping.AppId);
                }

                if (!string.IsNullOrEmpty(mapping.AppName) && mapping.AppName != $"App {mapping.AppId}")
                {
                    _appNames[mapping.AppId] = mapping.AppName;
                }
            }

            _logger.LogInformation($"Loaded {existingMappings.Count} existing depot mappings from database. Total unique depots: {_depotToAppMappings.Count}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading existing depot mappings from database");
        }
    }

    /// <summary>
    /// Manually apply depot mappings to existing downloads (called from UI)
    /// </summary>
    public async Task ManuallyApplyDepotMappings()
    {
        _logger.LogInformation("Manually applying depot mappings to downloads");

        // Wait a moment to ensure database operations have completed
        _logger.LogInformation("Waiting 2 seconds to ensure database is fully synced...");
        await Task.Delay(2000);

        // Reload depot mappings from database to ensure we have latest data
        _logger.LogInformation("Reloading depot mappings from database...");
        await LoadExistingDepotMappings();

        await UpdateDownloadsWithDepotMappings();
    }

    /// <summary>
    /// Update downloads that have depot IDs but no game information
    /// </summary>
    private async Task UpdateDownloadsWithDepotMappings()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get downloads that have depot IDs but no game info (or missing image)
            var downloadsNeedingGameInfo = await context.Downloads
                .Where(d => d.DepotId.HasValue && (d.GameAppId == null || string.IsNullOrEmpty(d.GameImageUrl)))
                .ToListAsync();

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info after PICS completion");

            int updated = 0;
            int notFound = 0;

            foreach (var download in downloadsNeedingGameInfo)
            {
                try
                {
                    uint? appId = download.GameAppId; // Use existing appId if available

                    // If no AppId yet, use owner ID from PICS data
                    if (!appId.HasValue)
                    {
                        // First, check in-memory owner mapping from PICS scan
                        if (_depotOwners.TryGetValue(download.DepotId.Value, out var ownerId))
                        {
                            appId = ownerId;
                            _logger.LogTrace($"Using PICS owner app {appId} for depot {download.DepotId}");
                        }
                        else
                        {
                            // Fallback to database owner lookup
                            var ownerApp = await context.SteamDepotMappings
                                .Where(m => m.DepotId == download.DepotId.Value && m.IsOwner)
                                .Select(m => m.AppId)
                                .FirstOrDefaultAsync();

                            if (ownerApp != 0)
                            {
                                appId = ownerApp;
                                _logger.LogTrace($"Using database owner app {appId} for depot {download.DepotId}");
                            }
                            else
                            {
                                // Last resort fallback: Use depot ID as app ID if app exists
                                // This handles cases where depot appears in logs but has no PICS mapping (depot ID = app ID)
                                var potentialAppId = download.DepotId.Value;
                                if (_appNames.ContainsKey(potentialAppId))
                                {
                                    appId = potentialAppId;
                                    _logger.LogDebug($"Using log-based fallback: depot {download.DepotId} -> app {appId} (depot ID = app ID)");
                                }
                                else
                                {
                                    _logger.LogDebug($"No owner app found for depot {download.DepotId}");
                                }
                            }
                        }
                    }

                    if (appId.HasValue)
                    {
                        download.GameAppId = appId.Value;

                        // Get game info from Steam API
                        var gameInfo = await _steamService.GetGameInfoAsync(appId.Value);
                        if (gameInfo != null)
                        {
                            download.GameName = gameInfo.Name;
                            download.GameImageUrl = gameInfo.HeaderImage;
                            updated++;

                            _logger.LogDebug($"Updated download {download.Id}: depot {download.DepotId} -> {gameInfo.Name} (App {appId})");
                        }
                        else
                        {
                            download.GameName = $"Steam App {appId}";
                            updated++;
                        }
                    }
                    else
                    {
                        notFound++;
                        _logger.LogDebug($"No PICS mapping found for depot {download.DepotId}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"Failed to get game info for depot {download.DepotId}");
                    notFound++;
                }
            }

            if (updated > 0)
            {
                await context.SaveChangesAsync();
                _logger.LogInformation($"Updated {updated} downloads with game information, {notFound} not found");
            }
            else
            {
                _logger.LogInformation($"No downloads updated, {notFound} depots without mappings");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating downloads with depot mappings");
        }
    }

    /// <summary>
    /// Load PICS metadata (crawl time and change number) from JSON or state
    /// </summary>
    private async Task LoadPicsMetadataAsync()
    {
        try
        {
            // Try to load from JSON file first (contains both crawl time and change number)
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            if (picsData?.Metadata != null)
            {
                _lastCrawlTime = picsData.Metadata.LastUpdated;
                _lastChangeNumberSeen = picsData.Metadata.LastChangeNumber;
                _logger.LogInformation("Loaded PICS metadata from JSON: crawl time {LastCrawl}, change number {ChangeNumber}",
                    _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"), _lastChangeNumberSeen);
                return;
            }

            // Fallback to state service for crawl time only
            var lastCrawl = _stateService.GetLastPicsCrawl();
            if (lastCrawl.HasValue)
            {
                _lastCrawlTime = lastCrawl.Value;
                _logger.LogInformation("Loaded last PICS crawl time from state: {LastCrawl}", _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                _logger.LogInformation("No previous PICS metadata found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load PICS metadata, will use defaults");
        }
    }

    /// <summary>
    /// Save the last PICS crawl time to state
    /// </summary>
    private void SaveLastCrawlTime()
    {
        try
        {
            _stateService.SetLastPicsCrawl(_lastCrawlTime);
            _logger.LogDebug("Saved last PICS crawl time: {LastCrawl}", _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save last PICS crawl time to state");
        }
    }

    /// <summary>
    /// Helper method to disconnect from Steam properly
    /// </summary>
    private async Task DisconnectFromSteamAsync(int delayMs = 1000)
    {
        if (_steamClient?.IsConnected == true)
        {
            _steamUser?.LogOff();
            await Task.Delay(delayMs);
            _steamClient.Disconnect();
        }
    }

    /// <summary>
    /// Helper method to convert concurrent dictionaries to regular dictionaries
    /// </summary>
    private (Dictionary<uint, HashSet<uint>>, Dictionary<uint, string>, Dictionary<uint, uint>) ConvertMappingsDictionaries()
    {
        var depotMappingsDict = new Dictionary<uint, HashSet<uint>>();
        foreach (var kvp in _depotToAppMappings)
        {
            depotMappingsDict[kvp.Key] = kvp.Value;
        }

        var appNamesDict = new Dictionary<uint, string>();
        foreach (var kvp in _appNames)
        {
            appNamesDict[kvp.Key] = kvp.Value;
        }

        var depotOwnersDict = new Dictionary<uint, uint>();
        foreach (var kvp in _depotOwners)
        {
            depotOwnersDict[kvp.Key] = kvp.Value;
        }

        return (depotMappingsDict, appNamesDict, depotOwnersDict);
    }

    /// <summary>
    /// Update connection activity timestamp
    /// </summary>
    private void UpdateConnectionActivity()
    {
        _lastConnectionActivity = DateTime.UtcNow;
    }

    /// <summary>
    /// Start idle disconnect timer to close connection after inactivity
    /// </summary>
    private void StartIdleDisconnectTimer()
    {
        // Cancel existing timer if any
        _idleDisconnectTimer?.Dispose();

        // Create new timer that checks every 10 seconds
        _idleDisconnectTimer = new Timer(async _ =>
        {
            if (_steamClient?.IsConnected == true && !IsRebuildRunning)
            {
                var idleTime = DateTime.UtcNow - _lastConnectionActivity;
                if (idleTime.TotalSeconds >= ConnectionKeepAliveSeconds)
                {
                    _logger.LogDebug("Connection idle for {Seconds} seconds, disconnecting", (int)idleTime.TotalSeconds);
                    _intentionalDisconnect = true;
                    await DisconnectFromSteamAsync();
                    StopIdleDisconnectTimer();
                }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Stop idle disconnect timer
    /// </summary>
    private void StopIdleDisconnectTimer()
    {
        _idleDisconnectTimer?.Dispose();
        _idleDisconnectTimer = null;
    }

    /// <summary>
    /// Authenticate with Steam using username and password
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, string? twoFactorCode = null, string? emailCode = null, bool allowMobileConfirmation = false)
    {
        try
        {
            // Connect if not already connected
            if (_steamClient?.IsConnected != true)
            {
                _connectedTcs = new TaskCompletionSource();
                _steamClient!.Connect();
                await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            // Create authenticator that returns provided codes
            var authenticator = new WebAuthenticator(twoFactorCode, emailCode, allowMobileConfirmation);

            // Begin authentication session using CredentialsAuthSession
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = authenticator
            });

            // Poll for result - this may throw if 2FA/email/mobile confirmation is required
            var pollResponse = default(SteamKit2.Authentication.AuthPollResult);
            try
            {
                pollResponse = await authSession.PollingWaitForResultAsync();
            }
            catch (InvalidOperationException)
            {
                // Check if we need mobile confirmation
                if (authenticator.NeedsMobileConfirmation)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresMobileConfirmation = true,
                        Message = "Mobile confirmation required"
                    };
                }

                // Check if we need 2FA or email code
                if (authenticator.NeedsTwoFactor)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresTwoFactor = true,
                        Message = "Two-factor authentication code required"
                    };
                }

                if (authenticator.NeedsEmailCode)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresEmailCode = true,
                        Message = "Email verification code required"
                    };
                }

                // Re-throw if it's not a 2FA/email code/mobile confirmation request
                throw;
            }

            // Store guard data if provided
            if (pollResponse?.NewGuardData != null)
            {
                _stateService.SetSteamGuardData(pollResponse.NewGuardData);
                _logger.LogInformation("Saved Steam Guard data for future logins");
            }

            // Store refresh token
            _stateService.SetSteamRefreshToken(pollResponse.RefreshToken);
            _logger.LogInformation("Successfully authenticated and saved refresh token");

            // Now login with the refresh token
            _loggedOnTcs = new TaskCompletionSource();
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = true
            });

            await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(30), CancellationToken.None);

            return new AuthenticationResult
            {
                Success = true,
                Message = "Authentication successful"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return new AuthenticationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Logout from Steam and clear stored credentials
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            // Cancel any active PICS rebuild
            if (IsRebuildRunning && _currentRebuildCts != null)
            {
                _logger.LogInformation("Cancelling active PICS rebuild before logout");
                try
                {
                    _currentRebuildCts.Cancel();

                    // Wait briefly for cancellation to complete
                    if (_currentBuildTask != null)
                    {
                        await Task.WhenAny(_currentBuildTask, Task.Delay(3000));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cancelling rebuild during logout");
                }
            }

            // Clear stored tokens
            _stateService.SetSteamRefreshToken(null);
            _stateService.SetSteamGuardData(null);

            // Disconnect from Steam
            _intentionalDisconnect = true;
            await DisconnectFromSteamAsync();

            _logger.LogInformation("Logged out from Steam and cleared credentials");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
        }
    }

    /// <summary>
    /// Simple authenticator for web-based authentication
    /// </summary>
    private class WebAuthenticator : IAuthenticator
    {
        private readonly string? _twoFactorCode;
        private readonly string? _emailCode;
        private readonly bool _allowMobileConfirmation;

        public bool NeedsTwoFactor { get; private set; }
        public bool NeedsEmailCode { get; private set; }
        public bool NeedsMobileConfirmation { get; private set; }

        public WebAuthenticator(string? twoFactorCode, string? emailCode, bool allowMobileConfirmation = false)
        {
            _twoFactorCode = twoFactorCode;
            _emailCode = emailCode;
            _allowMobileConfirmation = allowMobileConfirmation;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            NeedsTwoFactor = true;
            if (!string.IsNullOrEmpty(_twoFactorCode))
            {
                return Task.FromResult(_twoFactorCode);
            }
            throw new InvalidOperationException("Two-factor code required");
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            NeedsEmailCode = true;
            if (!string.IsNullOrEmpty(_emailCode))
            {
                return Task.FromResult(_emailCode);
            }
            throw new InvalidOperationException("Email code required");
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            // If user provided a 2FA code, don't wait for mobile confirmation
            // Return false so SteamKit2 will call GetDeviceCodeAsync instead
            if (!string.IsNullOrEmpty(_twoFactorCode))
            {
                return Task.FromResult(false);
            }

            // If mobile confirmation is not allowed and no code provided, throw error
            if (!_allowMobileConfirmation)
            {
                NeedsMobileConfirmation = true;
                throw new InvalidOperationException("Mobile confirmation required");
            }

            // Return true to tell SteamKit2 to wait for the user to confirm via Steam Mobile App
            // This enables the mobile push notification flow
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Authentication result
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public bool RequiresTwoFactor { get; set; }
        public bool RequiresEmailCode { get; set; }
        public bool RequiresMobileConfirmation { get; set; }
        public string? Message { get; set; }
    }
}
