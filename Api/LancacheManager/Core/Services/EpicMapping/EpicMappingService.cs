using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Core.Interfaces;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;

namespace LancacheManager.Core.Services.EpicMapping;

/// <summary>
/// Unified Epic Games mapping service for game discovery and download resolution.
/// Extends ConfigurableScheduledService for runtime-adjustable periodic catalog refresh.
/// </summary>
public partial class EpicMappingService : ConfigurableScheduledService, IDisposable
{
    private readonly EpicApiDirectClient _epicApiClient;
    private readonly EpicAuthStorageService _authStorage;
    private readonly ISignalRNotificationService _notifications;
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly IUnifiedOperationTracker _operationTracker;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IStateService _stateService;
    private readonly TaskCompletionSource<bool> _startupAutoReconnectCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EpicPrefillDaemonService? _epicDaemonService;
    private Guid? _currentOperationId;

    // Auth state
    private bool _isAuthenticated;
    private string? _displayName;
    private DateTime? _lastCollectionUtc;
    private int _gamesDiscovered;
    private EpicOAuthTokens? _currentTokens;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    // Scheduling state
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private Task? _currentRefreshTask;
    private CancellationTokenSource? _currentRefreshCts;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private int _isRunning;
    private bool _disposed;

    // Progress tracking
    private EpicMappingStatus _currentStatus = EpicMappingStatus.Idle;
    private int _isProcessingInt;
    private double _currentProgressPercent;
    private int _lastNewGames;
    private int _lastUpdatedGames;

    // Public properties
    public bool IsAuthenticated => _isAuthenticated;
    public string? DisplayName => _displayName;
    public DateTime? LastCollectionUtc => _lastCollectionUtc;
    public int GamesDiscovered => _gamesDiscovered;

    protected override string ServiceName => "EpicMappingService";
    protected override TimeSpan StartupDelay => TimeSpan.Zero; // We handle our own initialization in InitializeAsync

    public EpicMappingService(
        ILogger<EpicMappingService> logger,
        EpicApiDirectClient epicApiClient,
        EpicAuthStorageService authStorage,
        ISignalRNotificationService notifications,
        IDbContextFactory<AppDbContext> dbContextFactory,
        IUnifiedOperationTracker operationTracker,
        IServiceScopeFactory scopeFactory,
        IStateService stateService)
        : base(logger, TimeSpan.FromHours(12)) // Default: 12 hour refresh interval
    {
        _epicApiClient = epicApiClient;
        _authStorage = authStorage;
        _notifications = notifications;
        _dbContextFactory = dbContextFactory;
        _operationTracker = operationTracker;
        _scopeFactory = scopeFactory;
        _stateService = stateService;

        // Apply user-saved interval and run-on-startup overrides before the loop starts.
        LoadStateOverrides(stateService, ScheduleServiceKey);
    }

    protected override Task InitializeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EpicMappingService starting...");
        Interlocked.Exchange(ref _isRunning, 1);

        // Load last refresh time from saved auth data (mirrors Steam loading from state.json)
        var savedAuth = _authStorage.GetEpicAuthData();
        var hasSavedCredentials = _authStorage.HasSavedCredentials();

        if (hasSavedCredentials)
        {
            _logger.LogInformation("Epic auth is configured — saved credentials found. Auto-reconnect will be attempted");
        }
        else
        {
            _logger.LogInformation("Epic auth is not configured — no saved credentials found. Epic game mapping will be unavailable until authentication is completed");
        }

        if (savedAuth.LastAuthenticated.HasValue)
        {
            _lastRefreshTime = savedAuth.LastAuthenticated.Value;
        }

        // Try auto-reconnect if we have saved credentials (auth only, no scanning)
        if (hasSavedCredentials)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
                    await TryAutoReconnectAsync();
                }
                finally
                {
                    _startupAutoReconnectCompleted.TrySetResult(true);
                }
            }, CancellationToken.None);
        }
        else
        {
            _startupAutoReconnectCompleted.TrySetResult(true);
        }

        // Subscribe to Epic prefill daemon auth state change events
        SubscribeToDaemonEvents();

        return Task.CompletedTask;
    }

    protected override Task CleanupAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("EpicMappingService stopping...");
        Volatile.Write(ref _isRunning, 0);

        // Unsubscribe from Epic daemon events before cleanup
        UnsubscribeFromDaemonEvents();

        try
        {
            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
            }
        }
        catch (ObjectDisposedException) { }

        _startupAutoReconnectCompleted.TrySetResult(true);

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
    /// Updates the base class interval so the scheduling loop adjusts immediately.
    /// </summary>
    public double RefreshIntervalHours
    {
        get => ConfiguredInterval.TotalHours;
        set
        {
            // Update the base class interval (wakes the loop so new interval takes effect)
            UpdateInterval(TimeSpan.FromHours(value));

            if (value == 0)
            {
                _logger.LogInformation("Disabled periodic Epic catalog refresh");
            }
            else
            {
                _logger.LogInformation("Updated Epic refresh interval to {Hours} hour(s)", value);
            }
        }
    }

    /// <summary>
    /// Returns schedule status for the REST endpoint.
    /// </summary>
    public EpicScheduleStatus GetScheduleStatus()
    {
        var refreshInterval = ConfiguredInterval;
        var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
        var nextRefreshIn = refreshInterval.TotalHours > 0 && _lastRefreshTime != DateTime.MinValue
            ? Math.Max(0, (refreshInterval - timeSinceLastRefresh).TotalSeconds)
            : 0;

        return new EpicScheduleStatus
        {
            RefreshIntervalHours = refreshInterval.TotalHours,
            IsProcessing = _isProcessingInt != 0,
            LastRefreshTime = _lastRefreshTime == DateTime.MinValue ? null : _lastRefreshTime,
            NextRefreshIn = nextRefreshIn,
            IsAuthenticated = _isAuthenticated,
            OperationId = _currentOperationId,
            Status = _currentStatus,
            ProgressPercent = _isProcessingInt != 0 ? _currentProgressPercent : 0,
            StatusMessage = _isProcessingInt != 0 ? FormatStatusMessage(_currentStatus) : null
        };
    }

    private static string FormatStatusMessage(EpicMappingStatus status)
    {
        return status switch
        {
            EpicMappingStatus.RefreshingCatalog => "Refreshing Epic game catalog...",
            EpicMappingStatus.Authenticating => "Authenticating with Epic Games...",
            EpicMappingStatus.Idle => "Idle",
            _ => status.ToDisplayString()
        };
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Volatile.Write(ref _isRunning, 0);

        // Unsubscribe from Epic daemon events
        UnsubscribeFromDaemonEvents();

        _currentRefreshCts?.Dispose();
        _currentRefreshCts = null;

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

        base.Dispose();
    }

    /// <summary>
    /// Subscribes to OnDaemonAuthenticated and OnAllDaemonsLoggedOut events
    /// from EpicPrefillDaemonService so we know when daemon auth state changes.
    /// </summary>
    private void SubscribeToDaemonEvents()
    {
        try
        {
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

    private Task HandleDaemonAuthenticated()
    {
        _logger.LogInformation("Epic daemon authenticated - daemon session is now active");
        return Task.CompletedTask;
    }

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
    public Guid? OperationId { get; set; }
    public EpicMappingStatus Status { get; set; } = EpicMappingStatus.Idle;
    public double ProgressPercent { get; set; }
    public string? StatusMessage { get; set; }
}
