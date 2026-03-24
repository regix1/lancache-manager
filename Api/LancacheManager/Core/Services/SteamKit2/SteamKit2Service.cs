using System.Collections.Concurrent;
using System.Text.Json;
using LancacheManager.Extensions;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using SteamKit2;


namespace LancacheManager.Core.Services.SteamKit2;

/// <summary>
/// SteamKit2 service for real-time Steam depot mapping using PICS
/// Based on DepotDownloader approach - properly queries Steam for depot-to-app mappings
/// </summary>
public partial class SteamKit2Service : IHostedService, IDisposable
{
    private readonly ILogger<SteamKit2Service> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SteamService _steamService;
    private readonly IPathResolver _pathResolver;
    private readonly StateService _stateService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISignalRNotificationService _notifications;
    private readonly SteamWebApiService _steamWebApiService;
    private readonly SteamAuthStorageService _steamAuthRepository;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly uint _steamLoginId;
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
    private string? _currentPicsOperationId;
    private int _rebuildActive;
    private bool _disposed;

    // Exponential backoff for reconnection attempts
    private int _reconnectAttempt = 0;
    private const int MaxReconnectAttempts = 2; // Give up after 2 attempts
    private const int MaxReconnectDelaySeconds = 60; // Cap at 60 seconds

    // Scheduling for periodic PICS crawls
    private Timer? _periodicTimer;
    private DateTime _lastCrawlTime = DateTime.MinValue;
    private TimeSpan _crawlInterval = TimeSpan.FromHours(1); // Default: Run incremental updates every hour
    private object _crawlIncrementalMode = true; // Default: Run incremental scans (true/false/"github")
    private readonly PicsDataService _picsDataService;
    private uint _lastChangeNumberSeen;
    private bool _lastScanWasForced = false; // Track if the last scan was forced to be full due to Steam requirements
    private bool _automaticScanSkipped = false; // Track if an automatic scan was skipped due to requiring full scan

    // depotId -> set of appIds (can be multiple for shared depots)
    private readonly ConcurrentDictionary<uint, HashSet<uint>> _depotToAppMappings = new();
    // depotId -> owner appId (from depotfromapp PICS field)
    private readonly ConcurrentDictionary<uint, uint> _depotOwners = new();
    private readonly HashSet<uint> _scannedApps = new();

    // Cache for app names
    private readonly ConcurrentDictionary<uint, string> _appNames = new();

    // Cache for depot names (from PICS depot "name" field)
    private readonly ConcurrentDictionary<uint, string> _depotNames = new();

    // Reference to the Steam daemon service for event subscription
    private SteamDaemonService? _steamDaemonService;

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
    private string? _lastErrorMessage = null;
    private int _sessionStartDepotCount = 0;  // Track depot count at start of session

    /// <summary>
    /// Check if we're truly using Steam authenticated mode (not just connected)
    /// </summary>
    private bool IsSteamAuthenticated
    {
        get
        {
            var authMode = _stateService.GetSteamAuthMode();
            var isAuthenticated = authMode == "authenticated" && !string.IsNullOrEmpty(_stateService.GetSteamRefreshToken());
            return isAuthenticated;
        }
    }

    public SteamKit2Service(
        ILogger<SteamKit2Service> logger,
        IServiceScopeFactory scopeFactory,
        SteamService steamService,
        PicsDataService picsDataService,
        IPathResolver pathResolver,
        StateService stateService,
        IHttpClientFactory httpClientFactory,
        ISignalRNotificationService notifications,
        SteamWebApiService steamWebApiService,
        SteamAuthStorageService steamAuthRepository,
        IUnifiedOperationTracker operationTracker)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _steamService = steamService;
        _picsDataService = picsDataService;
        _pathResolver = pathResolver;
        _stateService = stateService;
        _httpClientFactory = httpClientFactory;
        _notifications = notifications;
        _steamWebApiService = steamWebApiService;
        _steamAuthRepository = steamAuthRepository;
        _operationTracker = operationTracker;

        // Use range 16384-65535 to avoid collision with steam-prefill-daemon (0-16383)
        _steamLoginId = (uint)new Random().Next(16384, 65536);
        _logger.LogInformation("Generated unique Steam LoginID: {LoginID} (0x{LoginIDHex:X8})", _steamLoginId, _steamLoginId);

    }

    /// <summary>
    /// Clears all Steam authentication data (PICS login + Web API key)
    /// Used during security operations like API key regeneration or session clearing
    /// </summary>
    public async Task ClearAllSteamAuthAsync()
    {
        // Capture API key status BEFORE clearing
        var hadWebApiKey = !string.IsNullOrWhiteSpace(_steamAuthRepository.GetSteamAuthData().SteamApiKey);

        await LogoutAsync();
        _steamAuthRepository.ClearSteamAuthData();
        _logger.LogInformation("Cleared Steam PICS auth data");

        if (hadWebApiKey)
        {
            _steamWebApiService.RemoveApiKey();
            _logger.LogInformation("Cleared Steam Web API key");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting SteamKit2Service with PICS depot mapping");

        try
        {
            // Load existing depot mappings from database first
            await LoadExistingDepotMappingsAsync();

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
            var modeStr = GetCrawlModeString(_crawlIncrementalMode);
            _logger.LogInformation("Loaded crawl mode from state: {Mode}", modeStr);

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

            // Subscribe to prefill daemon auth state change events
            SubscribeToDaemonEvents();

            _isRunning = true;

            // Start callback handling loop
            _ = Task.Run(() => HandleCallbacksAsync(_cancellationTokenSource.Token), CancellationToken.None);

            // Enable periodic crawls if interval is configured (not 0)
            if (_crawlInterval.TotalHours > 0)
            {
                SetupPeriodicCrawls();
                _logger.LogInformation("Enabled automatic PICS crawls every {Hours} hour(s)", _crawlInterval.TotalHours);
            }
            else
            {
                _logger.LogInformation("Automatic PICS crawls are disabled (interval = 0)");
            }
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

        // Unsubscribe from prefill daemon events before disconnecting
        UnsubscribeFromDaemonEvents();

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
                _logger.LogDebug(ex, "Exception during Steam depot crawl shutdown - expected during cancellation");
            }
        }

        _logger.LogInformation("SteamKit2Service stopped");
    }

    private async Task HandleCallbacksAsync(CancellationToken cancellationToken)
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _isRunning = false;

        // Unsubscribe from prefill daemon events
        UnsubscribeFromDaemonEvents();

        // Clean up timers
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

    // ===== Consolidated Helper Methods =====

    /// <summary>
    /// Clears stored Steam credentials and resets to anonymous mode.
    /// Used during logout, session replacement, and credential invalidation.
    /// </summary>
    private void ClearSteamCredentials()
    {
        _stateService.SetSteamRefreshToken(null);
        _stateService.SetSteamUsername(null);
        _stateService.SetSteamAuthMode("anonymous");
    }

    /// <summary>
    /// Clears cached viability check state so the next check queries Steam for fresh data.
    /// Called after completing a PICS scan or importing GitHub data.
    /// </summary>
    private void ClearViabilityCache()
    {
        var state = _stateService.GetState();
        state.RequiresFullScan = false;
        state.LastViabilityCheck = null;
        state.LastViabilityCheckChangeNumber = 0;
        state.ViabilityChangeGap = 0;
        _stateService.SaveState(state);
    }

    /// <summary>
    /// Sends a SteamAutoLogout notification to the frontend.
    /// Used when credentials are invalidated or session is replaced.
    /// </summary>
    private void SendAutoLogoutNotification(string message, string reason)
    {
        _notifications.NotifyAllFireAndForget(SignalREvents.SteamAutoLogout, new
        {
            message,
            reason,
            replacementCount = 0,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Fails any pending connection/login TaskCompletionSources with the given exception.
    /// Used during disconnection, cancellation, and error handling to unblock waiting code.
    /// </summary>
    private void FailPendingConnectionTasks(Exception exception)
    {
        _connectedTcs?.TrySetException(exception);
        _loggedOnTcs?.TrySetException(exception);
    }

    /// <summary>
    /// Sends a DepotMappingComplete failure notification via SignalR.
    /// Used in error paths when a rebuild fails due to connection, login, or session errors.
    /// </summary>
    private void SendDepotMappingFailure(string errorMessage, string errorType)
    {
        _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingComplete, new
        {
            success = false,
            error = errorMessage,
            errorType,
            depotMappingsFound = _depotToAppMappings.Count,
            timestamp = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Fetches product info for a batch of app IDs from Steam PICS.
    /// Handles token acquisition, request building, and product info retrieval.
    /// Returns the list of product info callbacks for the caller to process.
    /// </summary>
    private async Task<IReadOnlyList<SteamApps.PICSProductInfoCallback>> FetchProductInfoBatchAsync(
        uint[] appIds, CancellationToken ct)
    {
        var tokensJob = _steamApps!.PICSGetAccessTokens(appIds, Enumerable.Empty<uint>());
        var tokens = await WaitForCallbackAsync(tokensJob, ct);

        var appRequests = new List<SteamApps.PICSRequest>(appIds.Length);
        foreach (var appId in appIds)
        {
            var request = new SteamApps.PICSRequest(appId);
            if (tokens.AppTokens.TryGetValue(appId, out var token))
                request.AccessToken = token;
            appRequests.Add(request);
        }

        var productJob = _steamApps.PICSGetProductInfo(appRequests, Enumerable.Empty<SteamApps.PICSRequest>());
        return await WaitForAllProductInfoAsync(productJob, ct);
    }

    /// <summary>
    /// Gets the current PICS change number from Steam.
    /// Requires an active connection (_steamApps must be non-null).
    /// </summary>
    private async Task<uint> GetCurrentPicsChangeNumberAsync(CancellationToken ct)
    {
        var job = _steamApps!.PICSGetChangesSince(0, false, false);
        var changes = await WaitForCallbackAsync(job, ct);
        return changes.CurrentChangeNumber;
    }

    /// <summary>
    /// Subscribes to OnDaemonAuthenticated and OnAllDaemonsLoggedOut events
    /// from SteamDaemonService so we know when daemon auth state changes.
    /// </summary>
    private void SubscribeToDaemonEvents()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            _steamDaemonService = scope.ServiceProvider.GetService<SteamDaemonService>();
            if (_steamDaemonService != null)
            {
                _steamDaemonService.OnDaemonAuthenticated += HandleDaemonAuthenticated;
                _steamDaemonService.OnAllDaemonsLoggedOut += HandleAllDaemonsLoggedOut;
                _logger.LogInformation("Subscribed to Steam daemon auth state change events");
            }
            else
            {
                _logger.LogWarning("SteamDaemonService not available - daemon event subscriptions skipped");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Steam daemon events");
        }
    }

    /// <summary>
    /// Unsubscribes from Steam daemon events to prevent memory leaks.
    /// </summary>
    private void UnsubscribeFromDaemonEvents()
    {
        if (_steamDaemonService != null)
        {
            _steamDaemonService.OnDaemonAuthenticated -= HandleDaemonAuthenticated;
            _steamDaemonService.OnAllDaemonsLoggedOut -= HandleAllDaemonsLoggedOut;
            _steamDaemonService = null;
            _logger.LogDebug("Unsubscribed from Steam daemon auth state change events");
        }
    }

    /// <summary>
    /// Handler for when a Steam daemon becomes authenticated.
    /// Logs the state change so Connection.cs logic can react accordingly.
    /// </summary>
    private Task HandleDaemonAuthenticated()
    {
        _logger.LogInformation("Steam daemon authenticated - daemon is now active");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handler for when all Steam daemons have logged out.
    /// Logs the state change so Connection.cs logic can react accordingly.
    /// </summary>
    private Task HandleAllDaemonsLoggedOut()
    {
        _logger.LogInformation("All Steam daemons logged out - no daemons are active");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Check if service is ready
    /// </summary>
    public bool IsReady => _isLoggedOn && _steamClient?.IsConnected == true && _depotToAppMappings.Count > 0 && !IsRebuildRunning;

    public bool IsRebuildRunning => Interlocked.CompareExchange(ref _rebuildActive, 0, 0) == 1;

    /// <summary>
    /// Get or set the crawl interval in hours
    /// </summary>
    public double CrawlIntervalHours
    {
        get => _crawlInterval.TotalHours;
        set
        {
            var oldInterval = _crawlInterval;
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

                // DON'T reset the last crawl time - let the existing schedule continue with the new interval
                // The timer will check if enough time has elapsed based on the new interval

                // Restart the timer if it's already running (or create it if it doesn't exist)
                if (_periodicTimer != null)
                {
                    _periodicTimer?.Dispose();
                    _periodicTimer = new Timer(OnPeriodicCrawlTimer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                    _logger.LogInformation($"Updated crawl interval to {value} hour(s) - next scan will occur based on existing schedule");
                }
                else
                {
                    // Timer doesn't exist yet, create it
                    SetupPeriodicCrawls();
                }
            }
        }
    }

    /// <summary>
    /// Get or set the automatic scan mode: true (incremental), false (full), or "github" (PICS updates only)
    /// </summary>
    public object CrawlIncrementalMode
    {
        get => _crawlIncrementalMode;
        set
        {
            _crawlIncrementalMode = value;

            // Save to state for persistence across restarts
            _stateService.SetCrawlIncrementalMode(value);

            var modeStr = GetCrawlModeString(value);
            _logger.LogInformation("Saved crawl mode to state: {Mode}", modeStr);
        }
    }

    /// <summary>
    /// Convert crawl mode object to string representation
    /// </summary>
    private string GetCrawlModeString(object mode)
    {
        if (mode is bool b)
        {
            return b ? "Incremental" : "Full";
        }

        // Handle JsonElement (from JSON deserialization when target type is object)
        if (mode is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.True)
            {
                return "Incremental";
            }
            if (jsonElement.ValueKind == JsonValueKind.False)
            {
                return "Full";
            }
            if (jsonElement.ValueKind == JsonValueKind.String && jsonElement.GetString() == "github")
            {
                return "GitHub";
            }
        }

        if (mode?.ToString() == "github")
        {
            return "GitHub";
        }

        // Default to Incremental if mode is not recognized
        _logger.LogWarning("Unrecognized crawl mode: {Mode}, defaulting to Incremental", mode);
        return "Incremental";
    }

    /// <summary>
    /// Check if crawl mode is incremental (true or not github)
    /// </summary>
    private bool IsIncrementalMode(object mode)
    {
        if (mode is bool b)
        {
            return b;
        }

        // Handle JsonElement (from JSON deserialization when target type is object)
        if (mode is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.True)
            {
                return true;
            }
            if (jsonElement.ValueKind == JsonValueKind.False)
            {
                return false;
            }
            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return jsonElement.GetString() != "github";
            }
        }

        return mode?.ToString() != "github";
    }

    /// <summary>
    /// Check if crawl mode is GitHub (PICS updates only)
    /// </summary>
    private bool IsGithubMode(object mode)
    {
        // Handle JsonElement (from JSON deserialization when target type is object)
        if (mode is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.String)
            {
                return jsonElement.GetString() == "github";
            }
            return false;
        }

        return mode?.ToString() == "github";
    }

    /// <summary>
    /// Get current depot mapping count (always from database for accuracy)
    /// </summary>
    public async Task<int> GetDepotMappingCountAsync()
    {
        try
        {
            using var scopedDb = _scopeFactory.CreateScopedDbContext();

            return await scopedDb.DbContext.SteamDepotMappings.CountAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query database for depot mapping count");
            return 0;
        }
    }

    public SteamPicsProgress GetProgress()
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

        // Check if we're using authenticated mode (refresh token saved) or anonymous mode
        var authMode = _stateService.GetSteamAuthMode();
        var isAuthenticated = authMode == "authenticated" && !string.IsNullOrEmpty(_stateService.GetSteamRefreshToken());

        // Check if Web API is available (V2 or V1 with key) for Full/Incremental scans
        var isWebApiAvailable = _steamWebApiService.IsWebApiAvailableCached();

        return new SteamPicsProgress
        {
            IsProcessing = IsRebuildRunning,
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
            AutomaticScanSkipped = _automaticScanSkipped,
            IsConnected = _steamClient?.IsConnected == true,
            IsLoggedOn = _isLoggedOn && isAuthenticated, // Only true if both connected AND using authenticated mode
            ErrorMessage = _lastErrorMessage,
            IsWebApiAvailable = isWebApiAvailable, // True if V2 is active OR V1 is configured with API key
            OperationId = _currentPicsOperationId
        };
    }

    /// <summary>
    /// Check if Steam Web API is available (V2 or V1 with key configured)
    /// Required for full PICS scans to enumerate all app IDs
    /// </summary>
    public bool IsWebApiAvailable()
    {
        return _steamWebApiService.IsWebApiAvailableCached();
    }
}
