using LancacheManager.Core.Models;
using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Sets up periodic catalog refresh timer.
    /// Mirrors SteamKit2Service.SetupPeriodicCrawls() pattern.
    /// </summary>
    private void SetupPeriodicRefresh()
    {
        if (_refreshInterval.TotalHours == 0)
        {
            _logger.LogInformation("Periodic Epic catalog refresh is disabled (interval = 0)");
            return;
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

        _logger.LogTrace("Epic periodic refresh timer tick - time since last: {Minutes}m, interval: {Interval}h", (int)(DateTime.UtcNow - _lastRefreshTime).TotalMinutes, _refreshInterval.TotalHours);

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

            try
            {
                _currentStatus = "Refreshing catalog";

                // Check if a cancel was requested for the current operation
                if (_currentOperationId != null)
                {
                    var op = _operationTracker.GetOperation(_currentOperationId);
                    if (op is { IsCancelling: true })
                    {
                        _logger.LogInformation("Epic catalog refresh was cancelled");
                        _operationTracker.CompleteOperation(_currentOperationId, false, "Cancelled");
                        _currentOperationId = null;
                        _currentStatus = "Idle";
                        return;
                    }
                }

                _logger.LogInformation(
                    "Starting scheduled Epic catalog refresh (last refresh was {Minutes} minutes ago)",
                    (int)timeSinceLastRefresh.TotalMinutes);

                await RefreshCatalogAsync(_cancellationTokenSource.Token);

                _lastRefreshTime = DateTime.UtcNow;
                _currentStatus = "Idle";

                _logger.LogInformation("Scheduled Epic catalog refresh completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Epic catalog refresh cancelled");

                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = "completed",
                    percentComplete = 100.0,
                    gamesDiscovered = _gamesDiscovered,
                    message = "Epic catalog refresh cancelled",
                    cancelled = true
                });

                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, false, "Cancelled");
                    _currentOperationId = null;
                }
                _currentStatus = "Idle";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled Epic catalog refresh failed - will retry on next interval");

                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = "failed",
                    percentComplete = 0.0,
                    message = $"Epic catalog refresh failed: {ex.Message}"
                });

                if (_currentOperationId != null)
                {
                    _operationTracker.CompleteOperation(_currentOperationId, false, ex.Message);
                }

                _currentStatus = "Idle";
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessingInt, 0);
            }
        });
    }

    /// <summary>
    /// Refreshes the Epic game catalog by re-fetching owned games and CDN patterns.
    /// Requires a valid access token - will attempt token refresh if needed.
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

        var refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _currentOperationId = _operationTracker.RegisterOperation(
            OperationType.EpicMapping,
            "Epic Catalog Refresh",
            refreshCts
        );

        // Fetch owned games
        var games = await _epicApiClient.GetOwnedGamesAsync(_currentTokens.AccessToken, ct);
        if (games.Count > 0)
        {
            var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
            var result = await MergeOwnedGamesAsync(games, sessionHash, "mapping-login", ct);

            _gamesDiscovered = result.TotalGames;
            _lastCollectionUtc = DateTime.UtcNow;

            _logger.LogInformation(
                "Catalog refresh: {New} new, {Updated} updated, {Total} total games",
                result.NewGames, result.UpdatedGames, result.TotalGames);
        }

        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Collecting games and images",
            percentComplete = 50.0,
            gamesDiscovered = _gamesDiscovered,
            message = $"Discovered {_gamesDiscovered} games"
        });

        // Refresh CDN patterns
        try
        {
            var cdnInfos = await _epicApiClient.GetCdnInfoAsync(_currentTokens.AccessToken, ct);
            if (cdnInfos.Count > 0)
            {
                await MergeCdnPatternsAsync(cdnInfos, ct);
                _logger.LogInformation("Catalog refresh CDN patterns: {Count}", cdnInfos.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh CDN patterns during catalog update");
        }

        // Update download image URLs from refreshed game mappings
        try
        {
            var imageUpdates = await RefreshGameImagesAsync(_currentTokens.AccessToken, ct);
            if (imageUpdates > 0)
            {
                _logger.LogInformation("Updated {Count} download image URLs during catalog refresh", imageUpdates);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh download images during catalog update");
        }

        // Discover free games from Epic's public promotions API (no auth required)
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Free games discovery skipped or failed (non-critical)");
        }

        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "completed",
            percentComplete = 100.0,
            gamesDiscovered = _gamesDiscovered,
            message = $"Catalog refresh complete - {_gamesDiscovered} games"
        });

        // Note: ResolveEpicDownloadsAsync() is not called here because it already runs
        // continuously via LiveLogMonitorService -> RustLogProcessorService after every log processing.
        // The continuous loop will pick up newly-fetched CDN patterns automatically.

        // Notify frontend of updates
        await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
        {
            totalGames = _gamesDiscovered,
            source = "scheduled-refresh"
        });

        if (_currentOperationId != null)
        {
            _operationTracker.CompleteOperation(_currentOperationId, true);
            _currentOperationId = null;
        }
    }

}
