using SteamKit2;
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
    private int _rebuildActive;
    private bool _disposed;

    // Scheduling for periodic PICS crawls
    private Timer? _periodicTimer;
    private DateTime _lastCrawlTime = DateTime.MinValue;
    private static readonly TimeSpan CrawlInterval = TimeSpan.FromHours(1); // Run incremental updates every hour
    private readonly PicsDataService _picsDataService;
    private uint _lastChangeNumberSeen;

    // depotId -> set of appIds (can be multiple for shared depots)
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
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

            // DISABLED: No automatic import - users must choose initialization method via UI
            // await ImportJsonOnStartupIfNeeded();

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

        if (_steamClient?.IsConnected == true)
        {
            _steamUser?.LogOff();
            await Task.Delay(1000);
            _steamClient.Disconnect();
        }

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
        // Load last crawl time from JSON file or fallback to text file
        _ = Task.Run(async () => await LoadLastCrawlTimeAsync());

        // Start the initial crawl only if needed (with a small delay to allow startup to complete)
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // Wait 5 seconds for startup to complete

            if (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                // Check if JSON data needs updating
                var needsUpdate = await _picsDataService.NeedsUpdateAsync();

                if (needsUpdate)
                {
                    _logger.LogInformation("Starting initial PICS crawl - JSON data needs updating or doesn't exist");
                    TryStartRebuild(_cancellationTokenSource.Token);
                }
                else
                {
                    // Always start with an incremental update to catch any recent changes
                    _logger.LogInformation("Starting incremental PICS update to catch recent changes");
                    TryStartRebuild(_cancellationTokenSource.Token, incrementalOnly: true);
                }
            }
        });

        // Set up the periodic timer for subsequent crawls
        _periodicTimer = new Timer(OnPeriodicCrawlTimer, null, CrawlInterval, CrawlInterval);
        _logger.LogInformation($"Scheduled incremental PICS updates every {CrawlInterval.TotalHours} hour(s)");
    }

    private void OnPeriodicCrawlTimer(object? state)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
        {
            return;
        }

        // Always run incremental updates on schedule
        _ = Task.Run(async () =>
        {
            if (!IsRebuildRunning)
            {
                _logger.LogInformation("Starting scheduled incremental PICS update");
                if (TryStartRebuild(_cancellationTokenSource.Token, incrementalOnly: true))
                {
                    _lastCrawlTime = DateTime.UtcNow;
                }
            }
        });
    }

    private async Task ConnectAndBuildIndexAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        try
        {
            await ConnectAndLoginAsync(ct);
            await BuildDepotIndexAsync(ct, incrementalOnly);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect and build depot index");
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

        _logger.LogInformation("Connected to Steam, logging in anonymously...");
        _steamUser!.LogOnAnonymous();

        // Wait for logged on
        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(30), ct);

        _logger.LogInformation("Successfully logged onto Steam!");
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

    private bool _isResumingGeneration = false; // Track if we're resuming an incomplete generation

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

            if (hasExistingData && !incrementalOnly)
            {
                // We're doing a "full rebuild" but have existing data - this means we're RESUMING
                _isResumingGeneration = true;

                // First, load all existing mappings from database
                await LoadExistingDepotMappings();
                _logger.LogInformation("Loaded {Count} existing depot mappings from database for resume", _depotToAppMappings.Count);

                // Then, if we have JSON data, load from there too (might have newer data)
                if (hasExistingJsonData)
                {
                    var previousChangeNumber = _lastChangeNumberSeen;
                    var (jsonMappingsMerged, changeNumberUpdated) = MergeDepotMappingsFromJson(existingData);

                    _logger.LogInformation("Loading {Count} existing depot mappings from JSON to resume generation",
                        existingData?.Metadata?.TotalMappings ?? 0);

                    if (jsonMappingsMerged > 0)
                    {
                        _logger.LogDebug("Merged {Count} app-to-depot relationships from JSON for resume", jsonMappingsMerged);
                    }

                    if (changeNumberUpdated || _lastChangeNumberSeen > 0 && _lastChangeNumberSeen != previousChangeNumber)
                    {
                        _logger.LogInformation("Resuming from change number: {ChangeNumber}", _lastChangeNumberSeen);
                    }
                }
            }
            else if (!incrementalOnly && !hasExistingData)
            {
                // Only clear if we're doing a full rebuild AND have no existing data (JSON or database)
                _isResumingGeneration = false;
                _logger.LogInformation("Starting fresh PICS generation - no existing data found in JSON or database");
                _depotToAppMappings.Clear();
                _appNames.Clear();
            }
            else if (incrementalOnly)
            {
                // For incremental updates, we always preserve existing mappings (no clearing)
                _isResumingGeneration = false;

                // Log the current state before loading
                _logger.LogInformation("Incremental update starting. Current depot mappings in memory: {Count}", _depotToAppMappings.Count);

                // Reload existing depot mappings to ensure we have all current data in memory
                await LoadExistingDepotMappings();

                if (hasExistingJsonData)
                {
                    var previousChangeNumber = _lastChangeNumberSeen;
                    var (jsonMappingsMerged, changeNumberUpdated) = MergeDepotMappingsFromJson(existingData);

                    _logger.LogInformation("Merged persisted JSON depot mappings into memory for incremental scan. JSON reported {TotalMappings} total mappings",
                        existingData?.Metadata?.TotalMappings ?? 0);

                    if (jsonMappingsMerged > 0)
                    {
                        _logger.LogDebug("Applied {Count} app-to-depot relationships from JSON while preparing incremental scan", jsonMappingsMerged);
                    }

                    if (changeNumberUpdated || _lastChangeNumberSeen > 0 && _lastChangeNumberSeen != previousChangeNumber)
                    {
                        _logger.LogInformation("Using persisted change number {ChangeNumber} for incremental crawl baseline", _lastChangeNumberSeen);
                    }
                }

                if (_sessionStartDepotCount < _depotToAppMappings.Count)
                {
                    _sessionStartDepotCount = _depotToAppMappings.Count;
                    _logger.LogInformation("Updated session start count to {Count} after loading persisted depot mappings", _sessionStartDepotCount);
                }

                // Log the state after loading
                _logger.LogInformation("After loading from database, depot mappings in memory: {Count}", _depotToAppMappings.Count);
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

            // Fallback / merge to ensure completeness (skip in incremental mode)
            if (!incrementalOnly && appIds.Count < 200_000) // heuristic: too few => seed
            {
                _logger.LogWarning("PICS enumeration returned {Count} apps; seeding with Web API applist", appIds.Count);
                try
                {
                    var webIds = await TryGetAllAppIdsFromWebApiAsync();
                    if (webIds.Count > 0)
                    {
                        var merged = new HashSet<uint>(appIds);
                        foreach (var id in webIds) merged.Add(id);
                        appIds = merged.ToList();
                        appIds.Sort();
                        _logger.LogInformation("After Web API seeding: {Count} unique app IDs", appIds.Count);
                    }
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to fetch Web API applist");
                }
            }

            // Dynamic app discovery is now handled within EnumerateAllAppIdsViaPicsChangesAsync

            var allBatches = appIds
                .Where(id => !_scannedApps.Contains(id))
                .Chunk(AppBatchSize)
                .ToList();

            _totalAppsToProcess = appIds.Count;
            _processedApps = 0;
            _totalBatches = allBatches.Count;
            _processedBatches = 0;
            _currentStatus = "Processing app data";

            // Only reset session start count for fresh scans, not incremental updates
            if (!incrementalOnly)
            {
                _sessionStartDepotCount = _depotToAppMappings.Count;  // Capture starting depot count
                _logger.LogInformation("Reset session start count to {Count} for fresh scan", _sessionStartDepotCount);
            }
            else
            {
                _logger.LogInformation("Preserving session start count {StartCount}, current count {CurrentCount} for incremental scan",
                    _sessionStartDepotCount, _depotToAppMappings.Count);
            }

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
                    foreach (var cb in productCallbacks)
                    {
                        appsInThisBatch += cb.Apps.Count;
                        unknownInThisBatch += cb.UnknownApps.Count;

                        foreach (var app in cb.Apps.Values)
                        {
                            ProcessAppDepots(app);
                            _processedApps++;
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

                        // When resuming, use merge to preserve existing data
                        await SaveAllMappingsToJsonAsync(incrementalOnly || _isResumingGeneration);
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
            // Final save - use merge if resuming to preserve all data
            await SaveAllMappingsToJsonAsync(incrementalOnly || _isResumingGeneration);

            _currentStatus = "Starting background database import";
            // Run database import on background thread to prevent blocking HTTP server
            _ = Task.Run(async () =>
            {
                try
                {
                    await ImportJsonToDatabase();
                    _logger.LogInformation("Background database import completed successfully");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Background database import failed");
                }
            });

            _currentStatus = "Updating downloads";
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

    private void ProcessAppDepots(SteamApps.PICSProductInfoCallback.PICSProductInfo app)
    {
        try
        {
            var appId = app.ID;
            var kv = app.KeyValues;

            // Prefer "appinfo" block if present; otherwise use direct children
            var appinfo = kv["appinfo"];
            var common  = appinfo != KeyValue.Invalid ? appinfo["common"]  : kv["common"];
            var depots  = appinfo != KeyValue.Invalid ? appinfo["depots"]  : kv["depots"];

            var appName = common?["name"]?.AsString() ?? $"App {appId}";
            _appNames[appId] = appName;

            if (depots == KeyValue.Invalid)
            {
                _logger.LogDebug("App {AppId} ({Name}): no depots", appId, appName);
                _scannedApps.Add(appId);
                return;
            }

            int mapped = 0;

            foreach (var child in depots.Children)
            {
                // Skip non-numeric keys (e.g., "branches", "overrides", etc.)
                if (!uint.TryParse(child.Name, out var depotId))
                    continue;

                // If this depot is shared, the *owner* is in depotfromapp.
                // Map the depot to its owner app, not the current one.
                var ownerFromPics = AsUInt(child["depotfromapp"]);
                var ownerAppId = ownerFromPics ?? appId;

                // (Optional) sanity: depots almost never equal their owner appid.
                if (depotId == ownerAppId)
                {
                    _logger.LogTrace("Skipping suspicious self-mapping depot {DepotId} -> app {Owner}", depotId, ownerAppId);
                    continue;
                }

                var set = _depotToAppMappings.GetOrAdd(depotId, _ => new HashSet<uint>());
                set.Add(ownerAppId);
                mapped++;

                var ownerName = _appNames.TryGetValue(ownerAppId, out var n) ? n : $"App {ownerAppId}";
                _logger.LogInformation("Mapped depot {DepotId} -> app {OwnerAppId} ({OwnerName}) [via {Via}]",
                    depotId, ownerAppId, ownerName, ownerFromPics.HasValue ? "depotfromapp" : "current-app");
            }

            _logger.LogDebug("App {AppId} ({Name}): mapped {Count} depots", appId, appName, mapped);
            _scannedApps.Add(appId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing app {AppId}: {Message}", app.ID, ex.Message);
        }
    }

    private async Task<List<uint>> EnumerateAllAppIdsViaPicsChangesAsync(CancellationToken ct, bool incrementalOnly = false)
    {
        if (_steamApps is null)
        {
            throw new InvalidOperationException("SteamApps handler not ready.");
        }

        var allApps = new HashSet<uint>();
        uint since = 0;
        uint maxChangesToCheck = 50000; // Reduced from 200k to 50k to avoid full update requests

        _logger.LogInformation("Enumerating app IDs via Steam PICS changelists...");

        // First, get the current changelist number
        var initialJob = _steamApps.PICSGetChangesSince(0, false, false);
        var initialChanges = await WaitForCallbackAsync(initialJob, ct);
        var currentChangeNumber = initialChanges.CurrentChangeNumber;

        // Use saved change number if available, otherwise start from recent point
        if (_lastChangeNumberSeen > 0 && _lastChangeNumberSeen < currentChangeNumber)
        {
            since = _lastChangeNumberSeen;
            _logger.LogInformation("Resuming PICS enumeration from saved change number: {FromChange} to {CurrentChange}",
                since, currentChangeNumber);
        }
        else
        {
            // Fallback: Start from a more recent point to avoid triggering full updates
            since = Math.Max(0, currentChangeNumber - maxChangesToCheck);
            _logger.LogInformation("Starting PICS enumeration from recent point: {FromChange} to {CurrentChange}",
                since, currentChangeNumber);
        }

        _logger.LogInformation($"Starting PICS crawl from change {since} to {currentChangeNumber}");

        int consecutiveFullUpdates = 0;
        int maxFullUpdates = 3;

        while (since < currentChangeNumber && consecutiveFullUpdates < maxFullUpdates)
        {
            ct.ThrowIfCancellationRequested();

            var job = _steamApps.PICSGetChangesSince(since, true, true);
            var changes = await WaitForCallbackAsync(job, ct);
            var pageAppCount = changes.AppChanges.Count;
            _lastChangeNumberSeen = changes.CurrentChangeNumber; // Track the latest change number

            if (changes.RequiresFullUpdate || changes.RequiresFullAppUpdate)
            {
                consecutiveFullUpdates++;
                _logger.LogWarning(
                    "PICS signaled a full update (since={Since}, current={Current}, attempt={Attempt}); skipping to more recent changes.",
                    since,
                    changes.CurrentChangeNumber,
                    consecutiveFullUpdates);

                // Skip ahead more aggressively to find a recent enough starting point
                var skipAmount = Math.Min(maxChangesToCheck / 4, currentChangeNumber - since);
                since += skipAmount;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            consecutiveFullUpdates = 0; // Reset counter on successful request

            foreach (var change in changes.AppChanges)
            {
                allApps.Add(change.Key);
            }

            var last = changes.LastChangeNumber;
            var current = changes.CurrentChangeNumber;

            _logger.LogDebug(
                "PICS changes page fetched: last={Last}, current={Current}, pageApps={PageApps}, collectedApps={Count}",
                last,
                current,
                pageAppCount,
                allApps.Count);

            if (last <= since)
            {
                if (pageAppCount == 0)
                {
                    // If we get no apps for several iterations, move forward more aggressively
                    since += 500; // Reduced step size
                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                    continue;
                }

                last = (uint)Math.Min((long)current, (long)since + Math.Max(1, pageAppCount));
            }

            since = last;

            // Maximum possible limit for complete Steam catalog coverage
            if (allApps.Count >= 500000)
            {
                _logger.LogInformation("Collected {Count} apps, stopping enumeration to prevent excessive API calls", allApps.Count);
                break;
            }

            await Task.Delay(100, ct); // Slightly longer delay to be more respectful
        }

        // Add dynamic app discovery only when not in incremental mode
        var initialAppCount = allApps.Count;
        if (!incrementalOnly)
        {
            await AddDynamicAppDiscoveryAsync(allApps, ct);

            var discoveredApps = allApps.Count - initialAppCount;
            if (discoveredApps > 0)
            {
                _logger.LogInformation($"PICS enumeration found {initialAppCount} apps via changes, plus {discoveredApps} apps via dynamic discovery.");
            }
            else
            {
                _logger.LogInformation($"PICS enumeration found {initialAppCount} apps via changes.");
            }
        }
        else
        {
            _logger.LogInformation($"Incremental PICS enumeration found {initialAppCount} changed apps (skipping dynamic discovery).");
        }

        var list = allApps.ToList();
        list.Sort();

        _logger.LogInformation("PICS enumeration complete. Found {Count} total apps", list.Count);
        return list;
    }

    /// <summary>
    /// Add dynamic app discovery using various algorithms when PICS enumeration yields too few results
    /// </summary>
    private async Task AddDynamicAppDiscoveryAsync(HashSet<uint> allApps, CancellationToken ct)
    {
        var initialCount = allApps.Count;
        _logger.LogInformation("Starting dynamic app discovery to supplement PICS enumeration");

        // Strategy 1: Add apps from existing database depot mappings
        await AddAppsFromExistingMappingsAsync(allApps);

        // Strategy 2: Use mathematical ranges based on Steam's app ID patterns
        AddAppsFromMathematicalRanges(allApps);

        // Strategy 3: Add apps from recent downloads that we've seen before
        await AddAppsFromDownloadHistoryAsync(allApps);

        var discoveredCount = allApps.Count - initialCount;
        _logger.LogInformation("Dynamic app discovery added {NewCount} apps", discoveredCount);
    }

    /// <summary>
    /// Add app IDs from existing depot mappings in database
    /// </summary>
    private async Task AddAppsFromExistingMappingsAsync(HashSet<uint> allApps)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var existingAppIds = await context.SteamDepotMappings
                .Select(m => m.AppId)
                .Distinct()
                .ToListAsync();

            foreach (var appId in existingAppIds)
            {
                allApps.Add(appId);
            }

            _logger.LogDebug("Added {Count} apps from existing database mappings", existingAppIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add apps from existing mappings");
        }
    }

    /// <summary>
    /// Add app IDs using mathematical ranges based on Steam's historical patterns
    /// </summary>
    private void AddAppsFromMathematicalRanges(HashSet<uint> allApps)
    {
        // Dynamic ranges based on Steam's app ID allocation patterns
        var currentYear = DateTime.UtcNow.Year;
        var steamLaunchYear = 2003;
        var yearsActive = currentYear - steamLaunchYear;

        // Complete Steam catalog coverage - no app should be missed
        var ranges = new List<(uint start, uint end, uint step)>
        {
            // Classic Steam era (2003-2007) - complete coverage
            (1U, 1000U, 1U),

            // Early growth era (2007-2010) - tighter coverage
            (1000U, 50000U, 5U),

            // Expansion era (2010-2015) - much tighter coverage
            (50000U, 400000U, 25U),

            // Modern era (2015-2020) - comprehensive coverage
            (400000U, 800000U, 50U),

            // Recent era (2020-2022) - tight coverage
            (800000U, 1500000U, 100U),

            // Current era (2022-2024) - comprehensive coverage for new games
            (1500000U, 2500000U, 250U),

            // Extended coverage for newest games like Marvel Rivals
            (2500000U, 5000000U, 500U),

            // Maximum range for any possible Steam app
            (5000000U, 10000000U, 2000U)
        };

        // Remove limits to allow maximum discovery
        foreach (var (start, end, step) in ranges)
        {
            for (uint appId = start; appId <= end && allApps.Count < 300000; appId += step)
            {
                allApps.Add(appId);
            }
        }

        // Complete problematic app ranges with guaranteed coverage for all eras
        var problematicRanges = new List<(uint start, uint end, uint step)>
        {
            // Rainbow Six Siege area - complete coverage
            (350000U, 370000U, 1U),

            // Classic high-value ranges with fine-grained coverage
            (1000U, 10000U, 1U),     // Classic Steam games
            (20000U, 30000U, 1U),    // Early Source games
            (40000U, 60000U, 5U),    // Popular indie range
            (100000U, 110000U, 1U),  // Popular Steam range
            (200000U, 250000U, 5U),  // Early modern Steam
            (300000U, 320000U, 1U),  // Around common game ranges
            (380000U, 420000U, 10U), // Extended modern range
            (500000U, 600000U, 25U), // Newer Steam apps
            (700000U, 750000U, 25U), // Recent apps

            // 2020+ era games - potential Marvel Rivals territory
            (1000000U, 1100000U, 10U), // Early 2020s games
            (1500000U, 1600000U, 10U), // Mid 2020s games
            (2000000U, 2100000U, 25U), // Recent major releases
            (2500000U, 2600000U, 10U), // Very recent games
            (3000000U, 3200000U, 50U), // Newest game range

            // Extended coverage for potential edge cases
            (4000000U, 4100000U, 100U), // Extreme high range
            (5000000U, 5100000U, 250U), // Maximum coverage
        };

        foreach (var (start, end, step) in problematicRanges)
        {
            for (uint appId = start; appId <= end; appId += step)
            {
                allApps.Add(appId);
            }
        }

        _logger.LogDebug("Added apps from {RangeCount} mathematical ranges and {ProblematicCount} problematic ranges",
            ranges.Count, problematicRanges.Count);
    }

    /// <summary>
    /// Add app IDs from recent download history that we've successfully resolved
    /// </summary>
    private async Task AddAppsFromDownloadHistoryAsync(HashSet<uint> allApps)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var recentAppIds = await context.Downloads
                .Where(d => d.GameAppId.HasValue && d.StartTime > DateTime.UtcNow.AddDays(-30))
                .Select(d => d.GameAppId!.Value)
                .Distinct()
                .Take(500)
                .ToListAsync();

            foreach (var appId in recentAppIds)
            {
                allApps.Add(appId);
            }

            _logger.LogDebug("Added {Count} apps from recent download history", recentAppIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add apps from download history");
        }
    }

    /// <summary>
    /// Combine sources to show discovery method hierarchy
    /// </summary>
    private string GetCombinedSource(string existingSource, string newSource)
    {
        // If sources are the same, just return one
        if (existingSource == newSource)
            return existingSource;

        // If existing is PatternMatching and new is PICS, show both
        if (existingSource == "PatternMatching" && newSource == "SteamKit2-PICS")
            return "PatternMatching+PICS";

        // If existing is any other source and new is PICS, upgrade to PICS
        if (newSource == "SteamKit2-PICS")
            return "SteamKit2-PICS";

        // Otherwise keep the existing source (preserve manual/higher confidence sources)
        return existingSource;
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

            if (incrementalOnly)
            {
                // Pass validateExisting=true to clean up corrupted entries during incremental updates
                await _picsDataService.MergePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen, validateExisting: true);
                _logger.LogInformation(
                    "Merged {DepotCount} unique depot mappings to JSON (incremental); JSON metadata totals will list depot/app pairs when depots are shared",
                    depotMappingsDict.Count);
            }
            else
            {
                await _picsDataService.SavePicsDataToJsonAsync(depotMappingsDict, appNamesDict, _lastChangeNumberSeen);
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
    /// Import JSON data on startup if database is empty but JSON exists
    /// </summary>
    private async Task ImportJsonOnStartupIfNeeded()
    {
        try
        {
            // Check if we have any depot mappings in memory (loaded from database)
            if (_depotToAppMappings.Count == 0)
            {
                // Check if JSON file exists and has data
                var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
                if (picsData?.DepotMappings?.Any() == true)
                {
                    _logger.LogInformation("Database is empty but JSON file contains {Count} depot mappings. Importing...",
                        picsData.Metadata?.TotalMappings ?? 0);

                    _currentStatus = "Importing JSON to database (startup)";
                    await _picsDataService.ImportJsonDataToDatabaseAsync();

                    // Reload depot mappings into memory after import
                    await LoadExistingDepotMappings();

                    _logger.LogInformation("Successfully imported {Count} depot mappings from JSON on startup",
                        _depotToAppMappings.Count);
                    _currentStatus = "Idle";
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to import JSON data on startup - will proceed with normal PICS crawl");
        }
    }

    /// <summary>
    /// Get app IDs from depot ID (always from database for accuracy)
    /// </summary>
    public IReadOnlyCollection<uint> GetAppIdsForDepot(uint depotId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var dbAppIds = context.SteamDepotMappings
                .Where(m => m.DepotId == depotId)
                .Select(m => m.AppId)
                .ToList();

            return dbAppIds;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database for depot {DepotId}", depotId);
            return Array.Empty<uint>();
        }
    }

    /// <summary>
    /// Get single app ID from depot (returns first if multiple)
    /// </summary>
    public uint? GetAppIdFromDepot(uint depotId)
    {
        var appIds = GetAppIdsForDepot(depotId);
        return appIds.Any() ? appIds.First() : null;
    }

    /// <summary>
    /// Get app ID from depot async
    /// </summary>
    public Task<uint?> GetAppIdFromDepotAsync(uint depotId)
    {
        return Task.FromResult(GetAppIdFromDepot(depotId));
    }

    /// <summary>
    /// Get app name from depot ID (always from database for accuracy)
    /// </summary>
    public string? GetAppNameFromDepot(uint depotId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var appName = context.SteamDepotMappings
                .Where(m => m.DepotId == depotId)
                .Select(m => m.AppName)
                .FirstOrDefault();

            return appName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database for depot {DepotId} name", depotId);
            return null;
        }
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
    /// Get sample depot mappings for testing
    /// </summary>
    public IEnumerable<KeyValuePair<uint, uint>> GetSampleDepotMappings(int count = 10)
    {
        return _depotToAppMappings
            .SelectMany(kvp => kvp.Value.Select(appId => new KeyValuePair<uint, uint>(kvp.Key, appId)))
            .Take(count);
    }

    /// <summary>
    /// Check if service is ready
    /// </summary>
    public bool IsReady => _isLoggedOn && _steamClient?.IsConnected == true && _depotToAppMappings.Count > 0 && !IsRebuildRunning;

    /// <summary>
    /// Check if PICS crawl should run based on timing and current state
    /// </summary>
    public async Task<bool> ShouldRunPicsCrawlAsync()
    {
        // Don't run if already running
        if (IsRebuildRunning) return false;

        // Check if JSON data needs updating
        return await _picsDataService.NeedsUpdateAsync();
    }

    /// <summary>
    /// Legacy synchronous method for backward compatibility
    /// </summary>
    public bool ShouldRunPicsCrawl()
    {
        // Don't run if already running
        if (IsRebuildRunning) return false;

        // Fallback to time-based check
        var timeSinceLastCrawl = DateTime.UtcNow - _lastCrawlTime;
        return _lastCrawlTime == DateTime.MinValue || timeSinceLastCrawl >= CrawlInterval;
    }

    /// <summary>
    /// Get current PICS crawl progress
    /// </summary>
    public object GetProgress()
    {
        var timeSinceLastCrawl = DateTime.UtcNow - _lastCrawlTime;
        var nextCrawlIn = _lastCrawlTime == DateTime.MinValue ? TimeSpan.Zero : CrawlInterval - timeSinceLastCrawl;

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
            NextCrawlIn = nextCrawlIn.TotalSeconds > 0 ? nextCrawlIn : TimeSpan.Zero,
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

        // Clean up the periodic timer
        _periodicTimer?.Dispose();
        _periodicTimer = null;

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

        CancellationTokenSource linkedCts;
        try
        {
            linkedCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            // Main cancellation token source was disposed, use provided token or none
            linkedCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
        }

        async Task RunAsync()
        {
            try
            {
                await ConnectAndBuildIndexAsync(linkedCts.Token, incrementalOnly).ConfigureAwait(false);
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
                // Explicitly disconnect after crawl completion to prevent reconnection loops
                if (_steamClient?.IsConnected == true)
                {
                    _logger.LogDebug("Disconnecting from Steam after PICS crawl completion");
                    _intentionalDisconnect = true;
                    _steamUser?.LogOff();
                    await Task.Delay(1000); // Give LogOff time to complete
                    _steamClient.Disconnect();
                }

                linkedCts.Dispose();
                Interlocked.Exchange(ref _rebuildActive, 0);
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
    /// Update downloads that have depot IDs but no game information
    /// </summary>
    private async Task UpdateDownloadsWithDepotMappings()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Get downloads that have depot IDs but no game info
            var downloadsNeedingGameInfo = await context.Downloads
                .Where(d => d.DepotId.HasValue && d.GameAppId == null)
                .ToListAsync();

            _logger.LogInformation($"Found {downloadsNeedingGameInfo.Count} downloads needing game info after PICS completion");

            int updated = 0;
            int notFound = 0;

            foreach (var download in downloadsNeedingGameInfo)
            {
                try
                {
                    uint? appId = null;

                    // Use PICS mappings to find app ID
                    var appIds = GetAppIdsForDepot(download.DepotId.Value);
                    if (appIds.Any())
                    {
                        appId = appIds.First(); // Take the first app ID if multiple exist
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
    /// Load the last PICS crawl time from JSON file or fallback to text file
    /// </summary>
    private async Task LoadLastCrawlTimeAsync()
    {
        try
        {
            // First try to get the time from JSON file
            var picsData = await _picsDataService.LoadPicsDataFromJsonAsync();
            if (picsData?.Metadata != null)
            {
                _lastCrawlTime = picsData.Metadata.LastUpdated;
                _lastChangeNumberSeen = picsData.Metadata.LastChangeNumber;
                _logger.LogInformation("Loaded last PICS crawl time from JSON: {LastCrawl}, change number: {ChangeNumber}",
                    _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"), _lastChangeNumberSeen);
                return;
            }

            // Fallback to text file
            LoadLastCrawlTime();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load last PICS crawl time from JSON, falling back to text file");
            LoadLastCrawlTime();
        }
    }

    /// <summary>
    /// Load the last PICS crawl time from state
    /// </summary>
    private void LoadLastCrawlTime()
    {
        try
        {
            var lastCrawl = _stateService.GetLastPicsCrawl();
            if (lastCrawl.HasValue)
            {
                _lastCrawlTime = lastCrawl.Value;
                _logger.LogInformation("Loaded last PICS crawl time: {LastCrawl}", _lastCrawlTime.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else
            {
                _logger.LogInformation("No previous PICS crawl time found");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load last PICS crawl time from state");
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

}
