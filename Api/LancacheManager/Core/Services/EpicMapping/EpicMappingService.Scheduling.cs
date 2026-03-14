using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Sets up periodic catalog refresh timer.
    /// Mirrors SteamKit2Service.SetupPeriodicCrawls() pattern.
    /// Does NOT trigger overdue scans on startup — let the periodic timer handle it.
    /// </summary>
    private void SetupPeriodicRefresh()
    {
        if (_refreshInterval.TotalHours == 0)
        {
            _logger.LogInformation("Periodic Epic catalog refresh is disabled (interval = 0)");
            return;
        }

        // Check if a refresh is overdue - log it but don't automatically trigger
        var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
        var isDue = timeSinceLastRefresh >= _refreshInterval;

        if (isDue && _lastRefreshTime != DateTime.MinValue)
        {
            _logger.LogInformation(
                "Epic refresh is overdue by {Minutes} minutes - scheduled refresh will run at next check (within 1 minute)",
                (int)(timeSinceLastRefresh - _refreshInterval).TotalMinutes);
        }

        // Check every minute if a refresh is due (same pattern as Steam)
        _periodicTimer = new Timer(OnPeriodicRefreshTimer, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        _logger.LogInformation("Scheduled Epic catalog refresh every {Hours} hour(s) (checking every minute)",
            _refreshInterval.TotalHours);
    }

    /// <summary>
    /// Timer callback that checks if a catalog refresh is due.
    /// Mirrors SteamKit2Service.OnPeriodicCrawlTimer() pattern.
    /// </summary>
    private void OnPeriodicRefreshTimer(object? state)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
            return;

        // Skip if not authenticated (need valid tokens to refresh)
        if (!_isAuthenticated || _currentTokens == null)
            return;

        // Skip if interval is 0 (disabled)
        if (_refreshInterval.TotalHours == 0)
            return;

        // Check if enough time has elapsed since last refresh
        var timeSinceLastRefresh = DateTime.UtcNow - _lastRefreshTime;
        if (timeSinceLastRefresh < _refreshInterval)
            return;

        _ = Task.Run(async () =>
        {
            if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0) return;

            var success = false;
            string? errorMessage = null;

            // Create per-operation CTS linked to service-level CTS (mirrors Steam's _currentRebuildCts)
            _currentRefreshCts?.Dispose();
            _currentRefreshCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);

            // Register with operation tracker (mirrors Steam's TryStartRebuild)
            _currentOperationId = _operationTracker.RegisterOperation(
                OperationType.EpicMapping,
                "Epic Catalog Refresh",
                _currentRefreshCts
            );

            try
            {
                _currentStatus = "Refreshing catalog";

                _logger.LogInformation(
                    "Starting scheduled Epic catalog refresh (last refresh was {Minutes} minutes ago)",
                    (int)timeSinceLastRefresh.TotalMinutes);

                // Send start progress event (mirrors Steam's DepotMappingStarted)
                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = "Starting Epic catalog refresh...",
                    percentComplete = 0.0,
                    gamesDiscovered = _gamesDiscovered,
                    message = "Starting Epic catalog refresh..."
                });

                await RefreshCatalogAsync(_currentRefreshCts.Token);

                _lastRefreshTime = DateTime.UtcNow;
                _currentStatus = "Idle";
                success = true;

                _logger.LogInformation("Scheduled Epic catalog refresh completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Epic catalog refresh cancelled");
                errorMessage = "Operation cancelled";

                // Send cancellation notification (mirrors Steam's DepotMappingComplete with cancelled: true)
                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = "completed",
                    percentComplete = 100.0,
                    gamesDiscovered = _gamesDiscovered,
                    message = "Epic catalog refresh cancelled",
                    cancelled = true
                });

                _currentStatus = "Idle";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled Epic catalog refresh failed - will retry on next interval");
                errorMessage = ex.Message;

                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = "failed",
                    percentComplete = 0.0,
                    gamesDiscovered = _gamesDiscovered,
                    message = $"Epic catalog refresh failed: {ex.Message}"
                });

                _currentStatus = "Idle";
            }
            finally
            {
                // Complete the operation in the tracker (mirrors Steam's finally block)
                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, success, errorMessage);
                }

                _currentRefreshCts?.Dispose();
                _currentRefreshCts = null;
                _currentOperationId = null;
                Interlocked.Exchange(ref _isProcessingInt, 0);
            }
        });
    }

    /// <summary>
    /// Cancels the current Epic catalog refresh if one is running.
    /// Mirrors SteamKit2Service.CancelRebuildAsync() pattern.
    /// </summary>
    public Task<bool> CancelRefreshAsync()
    {
        if (_isProcessingInt == 0 || _currentRefreshCts == null)
        {
            return Task.FromResult(false);
        }

        _logger.LogInformation("Cancelling active Epic catalog refresh (operationId: {OperationId})", _currentOperationId);

        try
        {
            _currentRefreshCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    /// <summary>
    /// Refreshes the Epic game catalog by re-fetching owned games and CDN patterns.
    /// Sends progress events at each stage for the notification bar.
    /// </summary>
    private async Task RefreshCatalogAsync(CancellationToken ct)
    {
        // First, try to refresh the token if it's expired
        if (_currentTokens != null && _currentTokens.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Access token expired, refreshing before catalog update...");
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(_currentTokens.RefreshToken, ct);
                _currentTokens = tokens;
                _logger.LogDebug("Epic token refreshed successfully, proceeding with catalog update");

                // Save updated refresh token
                var authData = new EpicAuthData
                {
                    RefreshToken = tokens.RefreshToken,
                    DisplayName = tokens.DisplayName,
                    AccountId = tokens.AccountId,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = _gamesDiscovered
                };
                _authStorage.SaveEpicAuthData(authData);

                _displayName = tokens.DisplayName;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Token refresh failed during catalog update, clearing credentials");
                _authStorage.ClearEpicAuthData();
                _isAuthenticated = false;
                _displayName = null;
                _gamesDiscovered = 0;
                _currentTokens = null;
                return;
            }
        }

        if (_currentTokens == null)
        {
            _logger.LogWarning("No valid tokens available for catalog refresh");
            return;
        }

        ct.ThrowIfCancellationRequested();

        // Fetch owned games
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Fetching owned games...",
            percentComplete = 20.0,
            gamesDiscovered = _gamesDiscovered,
            message = "Fetching owned games from Epic catalog..."
        });

        var games = await _epicApiClient.GetOwnedGamesAsync(_currentTokens.AccessToken, ct);
        if (games.Count > 0)
        {
            var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
            var result = await MergeOwnedGamesAsync(games, sessionHash, "scheduled-refresh", ct);

            _gamesDiscovered = result.TotalGames;
            _lastCollectionUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Catalog refresh: {New} new, {Updated} updated, {Total} total games",
                result.NewGames, result.UpdatedGames, result.TotalGames);
        }

        ct.ThrowIfCancellationRequested();

        // Refresh CDN patterns
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Refreshing CDN patterns...",
            percentComplete = 50.0,
            gamesDiscovered = _gamesDiscovered,
            message = $"Discovered {_gamesDiscovered} games, refreshing CDN patterns..."
        });

        try
        {
            var cdnInfos = await _epicApiClient.GetCdnInfoAsync(_currentTokens.AccessToken, ct);
            if (cdnInfos.Count > 0)
            {
                await MergeCdnPatternsAsync(cdnInfos, ct);
                _logger.LogInformation("Catalog refresh CDN patterns: {Count}", cdnInfos.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to refresh CDN patterns during catalog update");
        }

        ct.ThrowIfCancellationRequested();

        // Discover free games
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Discovering free games...",
            percentComplete = 75.0,
            gamesDiscovered = _gamesDiscovered,
            message = "Checking for free game promotions..."
        });

        try
        {
            var freeGames = await _epicApiClient.GetFreeGamesAsync(ct);
            if (freeGames.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("free-games-discovery");
                var freeResult = await MergeOwnedGamesAsync(freeGames, sessionHash, "free-games", ct);
                _logger.LogInformation(
                    "Free games discovery: {New} new, {Updated} updated from {Count} promotions",
                    freeResult.NewGames, freeResult.UpdatedGames, freeGames.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Free games discovery skipped or failed (non-critical)");
        }

        // Send completion progress event (mirrors Steam's DepotMappingComplete)
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "completed",
            percentComplete = 100.0,
            gamesDiscovered = _gamesDiscovered,
            message = $"Epic catalog refresh complete — {_gamesDiscovered} games"
        });

        // Notify frontend of data updates
        await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
        {
            totalGames = _gamesDiscovered,
            source = "scheduled-refresh"
        });
    }
}
