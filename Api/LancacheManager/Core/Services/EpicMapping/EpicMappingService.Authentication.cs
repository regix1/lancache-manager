using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    /// <summary>
    /// Returns the Epic OAuth authorization URL for the user to visit.
    /// No Docker container is created - the URL points directly to Epic's login page.
    /// </summary>
    public string GetAuthorizationUrl()
    {
        var url = _epicApiClient.GetAuthorizationUrl();
        _logger.LogInformation("Generated Epic authorization URL for mapping login");
        return url;
    }

    /// <summary>
    /// Called when the user submits an authorization code from the Epic login page.
    /// Exchanges the code for tokens, fetches games, and saves credentials.
    /// </summary>
    public async Task OnAuthCodeReceivedAsync(string authorizationCode)
    {
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            _logger.LogWarning("Epic auth login skipped - another operation is already in progress");
            return;
        }

        await _sessionLock.WaitAsync();
        try
        {
            _logger.LogInformation("Exchanging Epic authorization code for tokens...");

            var authCts = new CancellationTokenSource();
            _currentOperationId = _operationTracker.RegisterOperation(
                OperationType.EpicMapping,
                "Epic Auth Login",
                authCts
            );
            _currentStatus = "Authenticating";

            // Exchange authorization code for tokens
            var tokens = await _epicApiClient.ExchangeAuthCodeAsync(authorizationCode, authCts.Token);
            _currentTokens = tokens;
            _logger.LogDebug("Epic OAuth tokens received, expires at {ExpiresAt}", tokens.ExpiresAt);

            // Fetch owned games with metadata
            var games = await _epicApiClient.GetOwnedGamesAsync(tokens.AccessToken, authCts.Token);

            if (games.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
                var result = await MergeOwnedGamesAsync(games, sessionHash, "mapping-login", authCts.Token);

                _logger.LogInformation(
                    "Mapping login game collection: {New} new, {Updated} updated, {Total} total",
                    result.NewGames, result.UpdatedGames, result.TotalGames);

                _gamesDiscovered = result.TotalGames;
            }

            await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
            {
                operationId = _currentOperationId,
                status = "Collecting games",
                percentComplete = 60.0,
                gamesDiscovered = _gamesDiscovered,
                message = $"Discovered {_gamesDiscovered} games"
            });

            // Collect CDN patterns
            try
            {
                var cdnInfos = await _epicApiClient.GetCdnInfoAsync(tokens.AccessToken, authCts.Token);
                if (cdnInfos.Count > 0)
                {
                    await MergeCdnPatternsAsync(cdnInfos, authCts.Token);
                    _logger.LogInformation("Mapping login CDN patterns: {Count}", cdnInfos.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to collect CDN patterns from mapping login");
            }

            // Save credentials for auto-reconnect
            var authData = new EpicAuthData
            {
                RefreshToken = tokens.RefreshToken,
                DisplayName = tokens.DisplayName,
                AccountId = tokens.AccountId,
                LastAuthenticated = DateTime.UtcNow,
                GamesDiscovered = _gamesDiscovered
            };
            _authStorage.SaveEpicAuthData(authData);

            _isAuthenticated = true;
            _displayName = tokens.DisplayName;
            _lastCollectionUtc = DateTime.UtcNow;

            // Resolve existing Epic downloads against the freshly collected CDN patterns
            try
            {
                var resolved = await ResolveEpicDownloadsAsync(authCts.Token);
                if (resolved > 0)
                {
                    _logger.LogInformation("Resolved {Count} Epic downloads to game names after login", resolved);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to resolve Epic downloads after login (non-fatal)");
            }

            // Notify frontend
            await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
            {
                totalGames = _gamesDiscovered,
                source = "mapping-login"
            });

            if (_currentOperationId != null)
            {
                _operationTracker.CompleteOperation(_currentOperationId, true);
                _currentOperationId = null;
            }
            _currentStatus = "Idle";

            _logger.LogInformation("Epic mapping login complete: {DisplayName}, {Games} games",
                tokens.DisplayName, _gamesDiscovered);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Epic mapping auth login cancelled");

            await _notifications.NotifyAllAsync(SignalREvents.EpicMappingProgress, new
            {
                operationId = _currentOperationId,
                status = "completed",
                percentComplete = 100.0,
                gamesDiscovered = _gamesDiscovered,
                message = "Epic game mapping cancelled",
                cancelled = true
            });

            if (_currentOperationId != null)
            {
                _operationTracker.CompleteOperation(_currentOperationId, false, "Cancelled");
                _currentOperationId = null;
            }
            _currentStatus = "Idle";
        }
        finally
        {
            _sessionLock.Release();
            Interlocked.Exchange(ref _isProcessingInt, 0);
        }
    }

    /// <summary>
    /// Logs out and clears saved credentials. No Docker container to terminate.
    /// </summary>
    public async Task LogoutAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            _authStorage.ClearEpicAuthData();

            _isAuthenticated = false;
            _displayName = null;
            _lastCollectionUtc = null;
            _gamesDiscovered = 0;
            _currentTokens = null;

            _logger.LogInformation("Epic mapping session logged out and credentials cleared");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>
    /// Attempts to reconnect using saved refresh token on startup.
    /// Auth only - no scanning. Mirrors Steam's startup behavior.
    /// </summary>
    private async Task TryAutoReconnectAsync()
    {
        var ct = _cancellationTokenSource.Token;
        await _sessionLock.WaitAsync(ct);
        try
        {
            var authData = _authStorage.GetEpicAuthData();
            if (string.IsNullOrEmpty(authData.RefreshToken))
            {
                _logger.LogInformation("No saved Epic refresh token, skipping auto-reconnect");
                return;
            }

            _logger.LogInformation("Attempting Epic mapping auto-reconnect with saved refresh token...");

            try
            {
                // Refresh the token directly via HTTP
                var tokens = await _epicApiClient.RefreshTokenAsync(authData.RefreshToken, ct);
                _currentTokens = tokens;

                // Update saved credentials with new refresh token
                var updatedAuthData = new EpicAuthData
                {
                    RefreshToken = tokens.RefreshToken,
                    DisplayName = tokens.DisplayName,
                    AccountId = tokens.AccountId,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = authData.GamesDiscovered
                };
                _authStorage.SaveEpicAuthData(updatedAuthData);

                _isAuthenticated = true;
                _displayName = tokens.DisplayName;
                _gamesDiscovered = authData.GamesDiscovered;
                _lastCollectionUtc = authData.LastAuthenticated;

                _logger.LogInformation("Epic auto-reconnect authenticated: {DisplayName}, {Games} cached games",
                    tokens.DisplayName, _gamesDiscovered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Epic refresh token expired or invalid, clearing credentials");
                _authStorage.ClearEpicAuthData();

                _isAuthenticated = false;
                _displayName = null;
                _gamesDiscovered = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-reconnect Epic mapping session");
            _isAuthenticated = false;
        }
        finally
        {
            _sessionLock.Release();
        }
    }
}
