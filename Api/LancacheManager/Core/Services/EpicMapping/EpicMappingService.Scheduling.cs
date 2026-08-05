using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    public string ScheduleServiceKey => "epicMapping";

    protected override bool SupportsNotifications => true;
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    private bool _showNotification = true;

    /// <summary>Terminal stage key for a run that collected nothing because Epic is signed out.</summary>
    private const string EpicSignInSkipStageKey = "signalr.epicMapping.skippedNotSignedIn";

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || Volatile.Read(ref _isRunning) == 0)
        {
            return;
        }

        await WaitForAutoReconnectAsync(stoppingToken);
        if (!_isAuthenticated || _currentTokens is null)
        {
            await ReportSignInSkipAsync(stoppingToken);
            return;
        }

        _logger.LogInformation("Starting scheduled Epic catalog refresh");
        if (TryStartRefresh(stoppingToken, CurrentRunTrigger) && _currentRefreshTask is not null)
        {
            await _currentRefreshTask;
        }
    }

    public bool TryStartRefresh(
        CancellationToken cancellationToken = default,
        RunTrigger trigger = RunTrigger.Manual)
    {
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            return false;
        }

        if (!_isAuthenticated || _currentTokens is null)
        {
            Interlocked.Exchange(ref _isProcessingInt, 0);
            return false;
        }

        _showNotification = EffectiveNotificationMode.AllowsTrigger(trigger);
        CancellationTokenSource runCts;
        try
        {
            runCts = CancellationTokenSource.CreateLinkedTokenSource(
                _cancellationTokenSource.Token,
                cancellationToken);
        }
        catch (ObjectDisposedException)
        {
            runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        _currentRefreshCts = runCts;
        _currentProgressPercent = 0;
        _lastNewGames = 0;
        _lastUpdatedGames = 0;

        _currentRefreshTask = Task.Run(async () =>
        {
            await using var reporter = CreateEpicMappingReporter(
                runCts.Token,
                () =>
                {
                    if (ReferenceEquals(_currentRefreshCts, runCts))
                    {
                        _currentRefreshCts = null;
                        _currentMappingReporter = null;
                        _currentOperationId = null;
                    }

                    _currentProgressPercent = 0;
                    _currentStatus = EpicMappingStatus.Idle;
                    Interlocked.Exchange(ref _isProcessingInt, 0);
                });
            _currentMappingReporter = reporter;

            try
            {
                await reporter.StartAsync(CreateEpicContext());
                _currentOperationId = reporter.OperationId;
                _currentStatus = EpicMappingStatus.RefreshingCatalog;

                await RefreshCatalogAsync(reporter, reporter.Token);
                _lastRefreshTime = DateTime.UtcNow;
                _authStorage.UpdateAuthData(data => data.LastAuthenticated = _lastRefreshTime);

                await reporter.CompleteAsync(
                    success: true,
                    context: CreateEpicContext());
                _logger.LogInformation("Epic catalog refresh completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Epic catalog refresh cancelled");
                await reporter.CompleteAsync(
                    success: false,
                    error: "Cancelled by user",
                    cancelled: true,
                    context: CreateEpicContext());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Epic catalog refresh failed");
                await reporter.CompleteAsync(
                    success: false,
                    error: ex.Message,
                    context: CreateEpicContext(ex.Message));
            }
            finally
            {
                runCts.Dispose();
                if (ReferenceEquals(_currentRefreshCts, runCts))
                {
                    _currentRefreshCts = null;
                    _currentMappingReporter = null;
                    _currentOperationId = null;
                    _currentProgressPercent = 0;
                    _currentStatus = EpicMappingStatus.Idle;
                    Interlocked.Exchange(ref _isProcessingInt, 0);
                }
            }
        }, CancellationToken.None);

        return true;
    }

    /// <summary>
    /// Reports a run that did nothing because no Epic account is signed in. It goes through the same
    /// reporter and the same terminal stage-key override every other outcome uses, so the run's
    /// notification mode still decides whether it is shown (a silent service stays silent) and the
    /// card reads "skipped" instead of claiming the catalog was refreshed.
    /// </summary>
    private async Task ReportSignInSkipAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Epic catalog refresh skipped - no Epic account is signed in");
        _showNotification = EffectiveNotificationMode.AllowsTrigger(CurrentRunTrigger);
        await using var reporter = CreateEpicMappingReporter(stoppingToken);
        await reporter.StartAsync(CreateEpicContext());
        await reporter.CompleteAsync(
            success: true,
            stageKey: EpicSignInSkipStageKey,
            context: CreateEpicContext());
    }

    private MappingOperationReporter CreateEpicMappingReporter(
        CancellationToken token,
        Action? onTerminalCleanup = null) =>
        new(
            _notifications,
            _operationTracker,
            MappingOperations.Epic,
            _showNotification,
            token,
            _logger,
            onTerminalCleanup: onTerminalCleanup);

    private Dictionary<string, object?> CreateEpicContext(string? errorDetail = null) =>
        new()
        {
            ["gamesDiscovered"] = _gamesDiscovered,
            ["newGames"] = _lastNewGames,
            ["updatedGames"] = _lastUpdatedGames,
            ["errorDetail"] = errorDetail,
        };

    private async Task WaitForAutoReconnectAsync(CancellationToken stoppingToken)
    {
        if (!_startupAutoReconnectCompleted.Task.IsCompleted)
        {
            _logger.LogInformation("Waiting for Epic startup auto-reconnect to finish before scheduled refresh");
            await _startupAutoReconnectCompleted.Task.WaitAsync(stoppingToken);
        }
    }

    public Task<bool> CancelRefreshAsync()
    {
        if (_isProcessingInt == 0 || _currentRefreshCts is null)
        {
            return Task.FromResult(false);
        }

        _logger.LogInformation(
            "Cancelling active Epic catalog refresh (operationId: {OperationId})",
            _currentOperationId);
        _currentMappingReporter?.RequestCancellation();
        try
        {
            _currentRefreshCts.Cancel();
            return Task.FromResult(true);
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(false);
        }
    }

    private async Task RefreshCatalogAsync(
        MappingOperationReporter reporter,
        CancellationToken cancellationToken)
    {
        _lastNewGames = 0;
        _lastUpdatedGames = 0;
        _currentProgressPercent = 0;

        if (_currentTokens is not null && _currentTokens.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Access token expired, refreshing before catalog update...");
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(
                    _currentTokens.RefreshToken,
                    cancellationToken);
                _currentTokens = tokens;
                _authStorage.SaveAuthData(new EpicAuthData
                {
                    RefreshToken = tokens.RefreshToken,
                    DisplayName = tokens.DisplayName,
                    AccountId = tokens.AccountId,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = _gamesDiscovered
                });
                _displayName = tokens.DisplayName;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _authStorage.ClearAuthData();
                SetIsAuthenticated(false);
                _displayName = null;
                _gamesDiscovered = 0;
                _currentTokens = null;
                throw new InvalidOperationException("Epic access token refresh failed", ex);
            }
        }

        if (_currentTokens is null)
        {
            throw new InvalidOperationException("No valid Epic token is available for catalog refresh");
        }

        _currentProgressPercent = 15;
        await reporter.ReportAsync(
            15,
            "signalr.epicMapping.fetchingGames",
            CreateEpicContext());
        var games = await _epicApiClient.GetOwnedGamesAsync(
            _currentTokens.AccessToken,
            cancellationToken);
        if (games.Count > 0)
        {
            var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
            var result = await MergeOwnedGamesAsync(
                games,
                sessionHash,
                "scheduled-refresh",
                cancellationToken);
            _gamesDiscovered = result.TotalGames;
            _lastCollectionUtc = DateTime.UtcNow;
            _stateService.SetEpicMappingLastCollection(_lastCollectionUtc.Value);
            _lastNewGames += result.NewGames;
            _lastUpdatedGames += result.UpdatedGames;
        }

        _currentProgressPercent = 40;
        await reporter.ReportAsync(
            40,
            "signalr.epicMapping.refreshingCdn",
            CreateEpicContext());
        try
        {
            var cdnInfos = await _epicApiClient.GetCdnInfoAsync(
                _currentTokens.AccessToken,
                cancellationToken);
            if (cdnInfos.Count > 0)
            {
                await MergeCdnPatternsAsync(cdnInfos, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh Epic CDN patterns");
        }

        _currentProgressPercent = 60;
        await reporter.ReportAsync(
            60,
            "signalr.epicMapping.checkingFreeGames",
            CreateEpicContext());
        try
        {
            var freeGames = await _epicApiClient.GetFreeGamesAsync(cancellationToken);
            if (freeGames.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("free-games-discovery");
                var result = await MergeOwnedGamesAsync(
                    freeGames,
                    sessionHash,
                    "free-games",
                    cancellationToken);
                _lastNewGames += result.NewGames;
                _lastUpdatedGames += result.UpdatedGames;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Epic free-game discovery skipped");
        }

        _currentProgressPercent = 85;
        await reporter.ReportAsync(
            85,
            "signalr.epicMapping.applyingMappings",
            CreateEpicContext());
        try
        {
            await ResolveDownloadsAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Epic downloads");
        }

        _currentProgressPercent = 99;
        await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
        {
            totalGames = _gamesDiscovered,
            newGames = _lastNewGames,
            updatedGames = _lastUpdatedGames,
            lastUpdatedUtc = DateTime.UtcNow,
            source = "scheduled-refresh"
        });
    }
}
