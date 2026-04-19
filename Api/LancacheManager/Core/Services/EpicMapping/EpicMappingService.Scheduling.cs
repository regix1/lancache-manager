using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    public string ScheduleServiceKey => "epicMapping";

    /// <summary>
    /// Called by the ConfigurableScheduledService base class on each interval tick.
    /// Checks preconditions and triggers a catalog refresh if appropriate.
    /// Awaits the background refresh task so the base class sets LastRunUtc and fires
    /// ServiceWorkCompleted only after the actual catalog refresh finishes.
    /// </summary>
    protected override async Task ExecuteScheduledWorkAsync(CancellationToken stoppingToken)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || !_isRunning)
            return;

        // Skip if not authenticated (need valid tokens to refresh)
        if (!_isAuthenticated || _currentTokens == null)
            return;

        _logger.LogInformation("Starting scheduled Epic catalog refresh");

        if (TryStartRefresh())
        {
            // Await the background task so the base class sets LastRunUtc and fires
            // ServiceWorkCompleted only after the actual catalog refresh finishes — not
            // immediately after TryStartRefresh returns.
            if (_currentRefreshTask is not null)
            {
                await _currentRefreshTask;
            }
        }
    }

    /// <summary>
    /// Starts a catalog refresh in the background. Returns true if started, false if already running.
    /// Mirrors SteamKit2Service.TryStartRebuild() — used by both periodic timer and manual "Apply Now".
    /// </summary>
    public bool TryStartRefresh(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            return false;
        }

        if (!_isAuthenticated || _currentTokens == null)
        {
            Interlocked.Exchange(ref _isProcessingInt, 0);
            return false;
        }

        // Create per-operation CTS linked to service-level CTS (mirrors Steam's _currentRebuildCts)
        _currentRefreshCts?.Dispose();
        try
        {
            _currentRefreshCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            _currentRefreshCts = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
        }

        // Register with operation tracker (mirrors Steam's TryStartRebuild)
        _currentOperationId = _operationTracker.RegisterOperation(
            OperationType.EpicMapping,
            "Epic Catalog Refresh",
            _currentRefreshCts
        );

        _currentProgressPercent = 0;
        _lastNewGames = 0;
        _lastUpdatedGames = 0;

        // Send start progress event (mirrors Steam's DepotMappingStarted)
        _notifications.NotifyAllFireAndForget(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "starting",
            percentComplete = 0.0,
            gamesDiscovered = _gamesDiscovered,
            stageKey = "signalr.epicMapping.starting"
        });

        _currentRefreshTask = Task.Run(async () =>
        {
            var success = false;
            string? errorMessage = null;

            try
            {
                _currentStatus = EpicMappingStatus.RefreshingCatalog;

                await RefreshCatalogAsync(_currentRefreshCts.Token);

                _lastRefreshTime = DateTime.UtcNow;
                _currentStatus = EpicMappingStatus.Idle;
                success = true;

                // Persist last refresh time so it survives restarts (mirrors Steam's SaveLastCrawlTime)
                _authStorage.UpdateEpicAuthData(data => data.LastAuthenticated = _lastRefreshTime);

                _logger.LogInformation("Epic catalog refresh completed successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Epic catalog refresh cancelled");
                errorMessage = "Operation cancelled";

                // Send cancellation notification (mirrors Steam's DepotMappingComplete with cancelled: true)
                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = OperationStatus.Completed,
                    percentComplete = 100.0,
                    gamesDiscovered = _gamesDiscovered,
                    stageKey = "signalr.epicMapping.cancelled",
                    cancelled = true
                });

                _currentProgressPercent = 0;
                _currentStatus = EpicMappingStatus.Idle;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Epic catalog refresh failed");
                errorMessage = ex.Message;

                await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
                {
                    operationId = _currentOperationId,
                    status = OperationStatus.Failed,
                    percentComplete = 0.0,
                    gamesDiscovered = _gamesDiscovered,
                    stageKey = "signalr.epicMapping.failed",
                    context = new Dictionary<string, object?> { ["errorDetail"] = ex.Message }
                });

                _currentProgressPercent = 0;
                _currentStatus = EpicMappingStatus.Idle;
            }
            finally
            {
                // Complete the operation in the tracker (mirrors Steam's finally block)
                if (_currentOperationId.HasValue)
                {
                    _operationTracker.CompleteOperation(_currentOperationId.Value, success, errorMessage);
                }

                _currentProgressPercent = 0;
                _currentRefreshCts?.Dispose();
                _currentRefreshCts = null;
                _currentOperationId = null;
                Interlocked.Exchange(ref _isProcessingInt, 0);
            }
        });

        return true;
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
    /// Refreshes the Epic game catalog by re-fetching owned games, CDN patterns,
    /// then resolving downloads. Sends progress events at each stage.
    /// </summary>
    private async Task RefreshCatalogAsync(CancellationToken ct)
    {
        _lastNewGames = 0;
        _lastUpdatedGames = 0;
        _currentProgressPercent = 0;

        // First, try to refresh the token if it's expired
        if (_currentTokens != null && _currentTokens.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Access token expired, refreshing before catalog update...");
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(_currentTokens.RefreshToken, ct);
                _currentTokens = tokens;
                _logger.LogDebug("Epic token refreshed successfully, proceeding with catalog update");

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
        _currentProgressPercent = 15;
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Fetching owned games...",
            percentComplete = 15.0,
            gamesDiscovered = _gamesDiscovered,
            stageKey = "signalr.epicMapping.fetchingGames",
            context = new Dictionary<string, object?>()
        });

        var games = await _epicApiClient.GetOwnedGamesAsync(_currentTokens.AccessToken, ct);
        if (games.Count > 0)
        {
            var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
            var result = await MergeOwnedGamesAsync(games, sessionHash, "scheduled-refresh", ct);

            _gamesDiscovered = result.TotalGames;
            _lastCollectionUtc = DateTime.UtcNow;
            _lastNewGames += result.NewGames;
            _lastUpdatedGames += result.UpdatedGames;

            _logger.LogInformation(
                "Catalog refresh: {New} new, {Updated} updated, {Total} total games",
                result.NewGames, result.UpdatedGames, result.TotalGames);
        }

        ct.ThrowIfCancellationRequested();

        // Refresh CDN patterns
        _currentProgressPercent = 40;
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Refreshing CDN patterns...",
            percentComplete = 40.0,
            gamesDiscovered = _gamesDiscovered,
            stageKey = "signalr.epicMapping.refreshingCdn",
            context = new Dictionary<string, object?> { ["gamesDiscovered"] = _gamesDiscovered }
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
        _currentProgressPercent = 60;
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Discovering free games...",
            percentComplete = 60.0,
            gamesDiscovered = _gamesDiscovered,
            stageKey = "signalr.epicMapping.checkingFreeGames",
            context = new Dictionary<string, object?>()
        });

        try
        {
            var freeGames = await _epicApiClient.GetFreeGamesAsync(ct);
            if (freeGames.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("free-games-discovery");
                var freeResult = await MergeOwnedGamesAsync(freeGames, sessionHash, "free-games", ct);
                _lastNewGames += freeResult.NewGames;
                _lastUpdatedGames += freeResult.UpdatedGames;
                _logger.LogInformation(
                    "Free games discovery: {New} new, {Updated} updated from {Count} promotions",
                    freeResult.NewGames, freeResult.UpdatedGames, freeGames.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Free games discovery skipped or failed (non-critical)");
        }

        ct.ThrowIfCancellationRequested();

        // Resolve downloads against CDN patterns (mirrors Steam applying depot mappings after scan)
        _currentProgressPercent = 85;
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = "Applying mappings to downloads...",
            percentComplete = 85.0,
            gamesDiscovered = _gamesDiscovered,
            stageKey = "signalr.epicMapping.applyingMappings",
            context = new Dictionary<string, object?>()
        });

        try
        {
            var resolved = await ResolveEpicDownloadsAsync(ct);
            if (resolved > 0)
            {
                _logger.LogInformation("Resolved {Count} Epic downloads to game names", resolved);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Epic downloads (non-fatal)");
        }

        // Send completion progress event
        _currentProgressPercent = 100;
        await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
        {
            operationId = _currentOperationId,
            status = OperationStatus.Completed,
            percentComplete = 100.0,
            gamesDiscovered = _gamesDiscovered,
            message = $"Epic catalog refresh completed — {_gamesDiscovered} games"
        });

        // Notify frontend of data updates
        await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
        {
            totalGames = _gamesDiscovered,
            newGames = _lastNewGames,
            updatedGames = _lastUpdatedGames,
            lastUpdatedUtc = DateTime.UtcNow,
            source = "scheduled-refresh"
        });

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var detectionService = scope.ServiceProvider.GetRequiredService<GameCacheDetectionService>();
            await detectionService.StartDetectionAsync(incremental: true);
            _logger.LogInformation("Triggered incremental game cache detection after successful Epic game mapping refresh");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger game cache detection after Epic mapping refresh");
        }
    }
}
