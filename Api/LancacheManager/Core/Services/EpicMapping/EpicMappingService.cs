using LancacheManager.Infrastructure.Data;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Models;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.EpicMapping;

/// <summary>
/// Unified Epic Games mapping service for game discovery and download resolution.
/// Mirrors SteamKit2Service architecture with partial classes for different concerns.
/// </summary>
public partial class EpicMappingService : IHostedService, IDisposable
{
    private readonly ILogger<EpicMappingService> _logger;
    private readonly EpicApiDirectClient _epicApiClient;
    private readonly EpicAuthStorageService _authStorage;
    private readonly ISignalRNotificationService _notifications;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private EpicPrefillDaemonService? _epicDaemonService;
    private string? _currentOperationId;

    // Auth state
    private bool _isAuthenticated;
    private string? _displayName;
    private DateTime? _lastCollectionUtc;
    private int _gamesDiscovered;
    private EpicOAuthTokens? _currentTokens;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    // Scheduling state
    private Timer? _periodicTimer;
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private TimeSpan _refreshInterval = TimeSpan.FromHours(12);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private bool _isRunning;
    private bool _disposed;

    // Progress tracking
    private string _currentStatus = "Idle";
    private int _isProcessingInt;

    // Public properties
    public bool IsAuthenticated => _isAuthenticated;
    public string? DisplayName => _displayName;
    public DateTime? LastCollectionUtc => _lastCollectionUtc;
    public int GamesDiscovered => _gamesDiscovered;

    public EpicMappingService(
        ILogger<EpicMappingService> logger,
        EpicApiDirectClient epicApiClient,
        EpicAuthStorageService authStorage,
        ISignalRNotificationService notifications,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IUnifiedOperationTracker operationTracker,
        IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        _epicApiClient = epicApiClient;
        _authStorage = authStorage;
        _notifications = notifications;
        _dbContextFactory = dbContextFactory;
        _operationTracker = operationTracker;
        _scopeFactory = scopeFactory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EpicMappingService starting...");
        _isRunning = true;

        // Try auto-reconnect if we have saved credentials
        if (_authStorage.HasSavedCredentials())
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                await TryAutoReconnectAsync();
            }, CancellationToken.None);
        }

        // Setup periodic catalog refresh
        SetupPeriodicRefresh();

        // Subscribe to Epic prefill daemon auth state change events
        SubscribeToDaemonEvents();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EpicMappingService stopping...");
        _isRunning = false;

        // Unsubscribe from Epic daemon events before cleanup
        UnsubscribeFromDaemonEvents();

        _periodicTimer?.Dispose();
        _periodicTimer = null;

        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }
        catch (ObjectDisposedException) { }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns current auth and progress status for REST endpoints.
    /// </summary>
    public EpicMappingAuthStatus GetAuthStatus()
    {
        return new EpicMappingAuthStatus
        {
            IsAuthenticated = _isAuthenticated,
            DisplayName = _displayName,
            LastCollectionUtc = _lastCollectionUtc,
            GamesDiscovered = _gamesDiscovered
        };
    }

    /// <summary>
    /// Get or set the refresh interval in hours. 0 = disabled.
    /// </summary>
    public double RefreshIntervalHours
    {
        get => _refreshInterval.TotalHours;
        set
        {
            _refreshInterval = TimeSpan.FromHours(value);
            _logger.LogInformation("Updated Epic refresh interval to {Hours} hour(s)", value);

            if (value == 0)
            {
                var oldTimer = Interlocked.Exchange(ref _periodicTimer, null);
                oldTimer?.Dispose();
                _logger.LogInformation("Disabled periodic Epic catalog refresh");
            }
            else
            {
                // Restart timer with new interval
                var newTimer = new Timer(OnPeriodicRefreshTimer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
                var oldTimer = Interlocked.Exchange(ref _periodicTimer, newTimer);
                oldTimer?.Dispose();
                _logger.LogInformation("Restarted periodic Epic catalog refresh timer");
            }
        }
    }

    /// <summary>
    /// Returns schedule status for the REST endpoint.
    /// </summary>
    public EpicScheduleStatus GetScheduleStatus()
    {
        var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
        var nextRefreshIn = _refreshInterval.TotalHours > 0 && _lastRefreshTime != DateTime.MinValue
            ? Math.Max(0, (_refreshInterval - timeSinceLastRefresh).TotalSeconds)
            : 0;

        return new EpicScheduleStatus
        {
            RefreshIntervalHours = _refreshInterval.TotalHours,
            IsProcessing = _isProcessingInt != 0,
            LastRefreshTime = _lastRefreshTime == DateTime.MinValue ? null : _lastRefreshTime,
            NextRefreshIn = nextRefreshIn,
            IsAuthenticated = _isAuthenticated,
            OperationId = _currentOperationId,
            Status = _currentStatus,
            ProgressPercent = _isProcessingInt != 0 ? 50 : 0
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _isRunning = false;

        // Unsubscribe from Epic daemon events
        UnsubscribeFromDaemonEvents();

        _periodicTimer?.Dispose();
        _periodicTimer = null;

        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                try { _cancellationTokenSource.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        }
        finally
        {
            _cancellationTokenSource.Dispose();
        }
    }

    /// <summary>
    /// Subscribes to OnDaemonAuthenticated and OnAllDaemonsLoggedOut events
    /// from EpicPrefillDaemonService so we know when daemon auth state changes.
    /// </summary>
    private void SubscribeToDaemonEvents()
    {
        try
        {
            // EpicPrefillDaemonService is a singleton, so we don't need a scoped scope.
            // Using a scope and disposing it is fragile since the singleton outlives the scope.
            var scope = _scopeFactory.CreateScope();
            _epicDaemonService = scope.ServiceProvider.GetService<EpicPrefillDaemonService>();
            if (_epicDaemonService != null)
            {
                _epicDaemonService.OnDaemonAuthenticated += HandleDaemonAuthenticated;
                _epicDaemonService.OnAllDaemonsLoggedOut += HandleAllDaemonsLoggedOut;
                _logger.LogInformation("Subscribed to Epic daemon auth state change events");
            }
            else
            {
                _logger.LogDebug("EpicPrefillDaemonService not available - daemon event subscriptions skipped");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to subscribe to Epic daemon events");
        }
    }

    /// <summary>
    /// Unsubscribes from Epic daemon events to prevent memory leaks.
    /// </summary>
    private void UnsubscribeFromDaemonEvents()
    {
        if (_epicDaemonService != null)
        {
            _epicDaemonService.OnDaemonAuthenticated -= HandleDaemonAuthenticated;
            _epicDaemonService.OnAllDaemonsLoggedOut -= HandleAllDaemonsLoggedOut;
            _epicDaemonService = null;
            _logger.LogDebug("Unsubscribed from Epic daemon auth state change events");
        }
    }

    /// <summary>
    /// Handler for when an Epic daemon becomes authenticated.
    /// Logs the state change for awareness.
    /// </summary>
    private Task HandleDaemonAuthenticated()
    {
        _logger.LogInformation("Epic daemon authenticated - daemon session is now active");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handler for when all Epic daemons have logged out.
    /// Logs the state change for awareness.
    /// </summary>
    private Task HandleAllDaemonsLoggedOut()
    {
        _logger.LogInformation("All Epic daemons logged out - no daemons are active");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Auth status response for the REST endpoint.
/// </summary>
public class EpicMappingAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastCollectionUtc { get; set; }
    public int GamesDiscovered { get; set; }
}

/// <summary>
/// Schedule status response for the REST endpoint.
/// </summary>
public class EpicScheduleStatus
{
    public double RefreshIntervalHours { get; set; }
    public bool IsProcessing { get; set; }
    public DateTime? LastRefreshTime { get; set; }
    public double NextRefreshIn { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? OperationId { get; set; }
    public string Status { get; set; } = "Idle";
    public double ProgressPercent { get; set; }
}
