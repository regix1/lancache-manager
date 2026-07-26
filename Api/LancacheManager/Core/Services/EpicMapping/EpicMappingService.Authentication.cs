using LancacheManager.Core.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    public string GetAuthorizationUrl()
    {
        var url = _epicApiClient.GetAuthorizationUrl();
        _logger.LogInformation("Generated Epic authorization URL for mapping login");
        return url;
    }

    /// <summary>
    /// Exchanges the one-time auth code first. Only after that prerequisite succeeds does the owned
    /// game/CDN mapping operation enter the tracked lifecycle.
    /// </summary>
    public async Task OnAuthCodeReceivedAsync(string authorizationCode)
    {
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            throw new InvalidOperationException("Epic auth is already in progress");
        }

        await _sessionLock.WaitAsync();
        CancellationTokenSource authCts;
        try
        {
            authCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            authCts = new CancellationTokenSource();
        }

        _currentRefreshCts = authCts;
        MappingOperationReporter? reporter = null;
        try
        {
            _currentStatus = EpicMappingStatus.Authenticating;
            _logger.LogInformation("Exchanging Epic authorization code for tokens...");
            var tokens = await _epicApiClient.ExchangeAuthCodeAsync(
                authorizationCode,
                authCts.Token);
            _currentTokens = tokens;

            // Authentication is an explicit user action. Do not inherit the visibility decision
            // from the last scheduled refresh (which may have been silent under Manual mode).
            _showNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);
            reporter = CreateEpicMappingReporter(
                authCts.Token,
                () =>
                {
                    _currentOperationId = null;
                    _currentMappingReporter = null;
                    _currentStatus = EpicMappingStatus.Idle;
                });
            _currentMappingReporter = reporter;
            await reporter.StartAsync(CreateEpicContext());
            _currentOperationId = reporter.OperationId;
            _currentStatus = EpicMappingStatus.RefreshingCatalog;

            await reporter.ReportAsync(
                15,
                "signalr.epicMapping.fetchingGames",
                CreateEpicContext());
            var games = await _epicApiClient.GetOwnedGamesAsync(
                tokens.AccessToken,
                reporter.Token);
            _gamesDiscovered = games.Count;
            _lastNewGames = 0;
            _lastUpdatedGames = 0;

            if (games.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
                var result = await MergeOwnedGamesAsync(
                    games,
                    sessionHash,
                    "mapping-login",
                    reporter.Token);
                _gamesDiscovered = result.TotalGames;
                _lastNewGames = result.NewGames;
                _lastUpdatedGames = result.UpdatedGames;
            }

            await reporter.ReportAsync(
                55,
                "signalr.epicMapping.refreshingCdn",
                CreateEpicContext());
            try
            {
                var cdnInfos = await _epicApiClient.GetCdnInfoAsync(
                    tokens.AccessToken,
                    reporter.Token);
                if (cdnInfos.Count > 0)
                {
                    await MergeCdnPatternsAsync(cdnInfos, reporter.Token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to collect Epic CDN patterns from mapping login");
            }

            _authStorage.SaveAuthData(new EpicAuthData
            {
                RefreshToken = tokens.RefreshToken,
                DisplayName = tokens.DisplayName,
                AccountId = tokens.AccountId,
                LastAuthenticated = DateTime.UtcNow,
                GamesDiscovered = _gamesDiscovered
            });
            SetIsAuthenticated(true);
            _displayName = tokens.DisplayName;
            _lastCollectionUtc = DateTime.UtcNow;
            _lastRefreshTime = DateTime.UtcNow;
            _stateService.SetEpicMappingLastCollection(_lastCollectionUtc.Value);

            await reporter.ReportAsync(
                85,
                "signalr.epicMapping.applyingMappings",
                CreateEpicContext());
            try
            {
                await ResolveDownloadsAsync(reporter.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to resolve Epic downloads after login");
            }

            await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
            {
                totalGames = _gamesDiscovered,
                newGames = _lastNewGames,
                updatedGames = _lastUpdatedGames,
                lastUpdatedUtc = DateTime.UtcNow,
                source = "mapping-login"
            });
            await reporter.CompleteAsync(success: true, context: CreateEpicContext());

            _logger.LogInformation(
                "Epic mapping login complete: {DisplayName}, {Games} games",
                tokens.DisplayName,
                _gamesDiscovered);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Epic mapping auth or collection cancelled");
            if (reporter is not null)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: "Cancelled by user",
                    cancelled: true,
                    context: CreateEpicContext());
            }
        }
        catch (Exception ex)
        {
            if (reporter is not null)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: ex.Message,
                    context: CreateEpicContext(ex.Message));
            }

            throw;
        }
        finally
        {
            if (reporter is not null)
            {
                await reporter.DisposeAsync();
            }

            authCts.Dispose();
            if (ReferenceEquals(_currentRefreshCts, authCts))
            {
                _currentRefreshCts = null;
            }

            _currentMappingReporter = null;
            _currentOperationId = null;
            _currentStatus = EpicMappingStatus.Idle;
            _sessionLock.Release();
            Interlocked.Exchange(ref _isProcessingInt, 0);
        }
    }

    public async Task LogoutAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            _authStorage.ClearAuthData();
            SetIsAuthenticated(false);
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

    private async Task TryAutoReconnectAsync()
    {
        var cancellationToken = _cancellationTokenSource.Token;
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var authData = _authStorage.GetAuthData();
            if (string.IsNullOrEmpty(authData.RefreshToken))
            {
                _logger.LogInformation("No saved Epic refresh token, skipping auto-reconnect");
                return;
            }

            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(
                    authData.RefreshToken,
                    cancellationToken);
                _currentTokens = tokens;
                _authStorage.SaveAuthData(new EpicAuthData
                {
                    RefreshToken = tokens.RefreshToken,
                    DisplayName = tokens.DisplayName,
                    AccountId = tokens.AccountId,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = authData.GamesDiscovered
                });
                SetIsAuthenticated(true);
                _displayName = tokens.DisplayName;
                _gamesDiscovered = authData.GamesDiscovered;
                _lastCollectionUtc =
                    _stateService.GetEpicMappingCollectedAt() ?? authData.LastAuthenticated;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Epic refresh token expired or invalid, clearing credentials");
                _authStorage.ClearAuthData();
                SetIsAuthenticated(false);
                _displayName = null;
                _gamesDiscovered = 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-reconnect Epic mapping session");
            SetIsAuthenticated(false);
        }
        finally
        {
            _sessionLock.Release();
        }
    }
}
