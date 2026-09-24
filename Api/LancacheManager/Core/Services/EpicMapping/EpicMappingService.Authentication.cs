using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    private IntegrationLogin? _loginAttempt;
    private IntegrationLogin? _processingLogin;

    public IntegrationAccess GetIntegrationAccess(IntegrationCaller caller) => _authStorage.GetIntegrationAccess(caller);
    public string? GetIntegrationLoginReason(IntegrationCaller caller) => _authStorage.GetIntegrationLoginReason(caller);
    public Task<IntegrationLease> AcquireIntegrationLoginAsync(IntegrationCaller caller, CancellationToken cancellationToken = default)
        => _authStorage.AcquireIntegrationLoginAsync(caller, cancellationToken);

    public bool TryGetSavedLoginAccount(Guid? accountId, out string? account, IntegrationCaller? caller = null)
    {
        caller ??= new IntegrationCaller(accountId, Guid.Empty, true);
        account = null;
        if (_authStorage.GetIntegrationLoginReason(caller) is not null) return false;
        var auth = _authStorage.GetIntegrationLogin(caller);
        account = string.IsNullOrWhiteSpace(auth.RefreshToken) ? null : auth.DisplayName;
        return !string.IsNullOrWhiteSpace(auth.RefreshToken);
    }

    public async Task<string> CreatePrefillRefreshTokenAsync(
        Guid? accountId,
        CancellationToken cancellationToken = default,
        IntegrationLease? lease = null)
    {
        await using var acquired = lease is null
            ? await AcquireIntegrationLoginAsync(new(accountId, Guid.Empty, true), cancellationToken)
            : null;
        lease ??= acquired!;
        _authStorage.ValidateIntegrationLease(lease);
        if (lease.Caller?.AccountId != accountId) IntegrationLease.Refuse("owned-by-another-account");
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            var savedAuth = _authStorage.GetIntegrationLogin(lease);
            var version = _authStorage.GetIntegrationSnapshot().Version;
            if (string.IsNullOrWhiteSpace(savedAuth.RefreshToken)) IntegrationLease.Refuse("no-saved-login");
            EpicOAuthTokens tokens;
            try
            {
                tokens = await _epicApiClient.RefreshTokenAsync(savedAuth.RefreshToken!, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var updated = _authStorage.TryUpdateAuth(version, auth =>
                {
                    auth.RefreshToken = tokens.RefreshToken;
                    auth.DisplayName = tokens.DisplayName;
                    auth.AccountId = tokens.AccountId;
                    auth.LastAuthenticated = DateTime.UtcNow;
                }, () =>
                {
                    _currentTokens = tokens;
                    _displayName = tokens.DisplayName;
                }, lease);
                if (updated is null) throw new OperationCanceledException();
            }
            catch (ValidationException)
            {
                _authStorage.TryInvalidateAuth(version, () =>
                {
                    SetIsAuthenticated(false);
                    _displayName = null;
                    _gamesDiscovered = 0;
                    _currentTokens = null;
                }, lease);
                throw;
            }
            var exchangeCode = await _epicApiClient.GetExchangeCodeAsync(tokens.AccessToken, cancellationToken);
            lease.Validate();
            var tokensForPrefill = await _epicApiClient.ExchangeCodeAsync(exchangeCode, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            lease.Validate();
            return tokensForPrefill.RefreshToken;
        }
        finally { _sessionLock.Release(); }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "The existing public method name is retained for API compatibility.")]
    public async Task<EpicLoginUrlResponse> GetAuthorizationUrl(
        IntegrationCaller caller, Guid? attemptId = null, bool recover = false)
    {
        var login = await _authStorage.BeginIntegrationLoginAsync(caller, attemptId, recover);
        if (!_authStorage.RunIntegrationLogin(login, () => _loginAttempt = login))
            throw new OperationCanceledException();
        return new EpicLoginUrlResponse
        {
            AuthorizationUrl = _epicApiClient.GetAuthorizationUrl(),
            AttemptId = login.AttemptId,
            ExpiresAtUtc = login.ExpiresAtUtc
        };
    }

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
    public async Task OnAuthCodeReceivedAsync(string authorizationCode, Guid? ownerAccountId = null,
        IntegrationCaller? caller = null, Guid? attemptId = null)
    {
        caller ??= new(ownerAccountId, ownerAccountId, ownerAccountId is not null);
        var login = _authStorage.ContinueIntegrationLogin(caller, attemptId);
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            throw new ConflictException("Epic auth is already in progress")
            {
                StageKey = "errors.epic.authInProgress"
            };
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

        var remaining = login.ExpiresAtUtc - DateTime.UtcNow;
        authCts.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        if (!_authStorage.RunIntegrationLogin(login, () =>
        {
            _processingLogin = login;
            _currentRefreshCts = authCts;
        }))
        {
            authCts.Dispose();
            _sessionLock.Release();
            Interlocked.Exchange(ref _isProcessingInt, 0);
            throw new OperationCanceledException();
        }
        MappingOperationReporter? reporter = null;
        var gamesDiscovered = 0;
        var newGames = 0;
        var updatedGames = 0;
        try
        {
            _currentStatus = EpicMappingStatus.Authenticating;
            _logger.LogInformation("Exchanging Epic authorization code for tokens...");
            var tokens = await _epicApiClient.ExchangeAuthCodeAsync(
                authorizationCode,
                authCts.Token);
            authCts.Token.ThrowIfCancellationRequested();
            if (!_authStorage.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();

            // Authentication is an explicit user action, so it gets a fresh manual notice rather than
            // the last scheduled refresh's (which may have been silent under Manual mode).
            reporter = CreateEpicMappingReporter(
                authCts.Token,
                new RunNotice(EffectiveNotificationMode, RunTrigger.Manual),
                () =>
                {
                    if (ReferenceEquals(_currentRefreshCts, authCts))
                    {
                        _currentOperationId = null;
                        _currentMappingReporter = null;
                        _currentStatus = EpicMappingStatus.Idle;
                    }
                });
            _currentMappingReporter = reporter;
            await reporter.StartAsync(CreateEpicContext(), login: login);
            _currentOperationId = reporter.OperationId;
            _currentStatus = EpicMappingStatus.RefreshingCatalog;

            await reporter.ReportAsync(
                15,
                "signalr.epicMapping.fetchingGames",
                CreateEpicContext());
            var games = await _epicApiClient.GetOwnedGamesAsync(
                tokens.AccessToken,
                reporter.Token);
            authCts.Token.ThrowIfCancellationRequested();
            if (!_authStorage.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
            gamesDiscovered = games.Count;

            if (games.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
                var result = await MergeOwnedGamesAsync(
                    games,
                    sessionHash,
                    "mapping-login",
                    reporter.Token);
                gamesDiscovered = result.TotalGames;
                newGames = result.NewGames;
                updatedGames = result.UpdatedGames;
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
                authCts.Token.ThrowIfCancellationRequested();
                if (!_authStorage.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
                if (cdnInfos.Count > 0)
                {
                    await MergeCdnPatternsAsync(cdnInfos, reporter.Token);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to collect Epic CDN patterns from mapping login");
            }

            authCts.Token.ThrowIfCancellationRequested();
            if (!_authStorage.CompleteIntegrationLogin(login, new EpicAuthData
            {
                OwnerAccountId = login.AccountId,
                RefreshToken = tokens.RefreshToken,
                DisplayName = tokens.DisplayName,
                AccountId = tokens.AccountId,
                LastAuthenticated = DateTime.UtcNow,
                GamesDiscovered = gamesDiscovered
            }, () =>
            {
                _gamesDiscovered = gamesDiscovered;
                _lastNewGames = newGames;
                _lastUpdatedGames = updatedGames;
                _currentTokens = tokens;
                SetIsAuthenticated(true);
                _displayName = tokens.DisplayName;
                _lastCollectionUtc = DateTime.UtcNow;
                _lastRefreshTime = DateTime.UtcNow;
                _stateService.SetEpicMappingLastCollection(_lastCollectionUtc.Value);
            })) throw new OperationCanceledException();

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
                    cancelled: true,
                    context: CreateEpicContext());
            }
            throw;
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

            if (ReferenceEquals(_currentMappingReporter, reporter))
            {
                _currentMappingReporter = null;
                _currentOperationId = null;
                _currentStatus = EpicMappingStatus.Idle;
            }
            _authStorage.FinishIntegrationLogin(login);
            if (_loginAttempt == login) _loginAttempt = null;
            if (_processingLogin == login) _processingLogin = null;
            _sessionLock.Release();
            Interlocked.Exchange(ref _isProcessingInt, 0);
        }
    }

    public async Task LogoutAsync(IntegrationCaller? caller = null)
    {
        await using var release = await _authStorage.BeginIntegrationReleaseAsync(caller);
        _currentRefreshCts?.Cancel();
        await _sessionLock.WaitAsync();
        try
        {
            _authStorage.CompleteIntegrationRelease(release);
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
        var snapshot = _authStorage.GetIntegrationSnapshot();
        try
        {
            if (string.IsNullOrEmpty(snapshot.Auth.RefreshToken) || !_authStorage.IsIntegrationCurrent(snapshot.Version)) return;
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(snapshot.Auth.RefreshToken, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _authStorage.TryUpdateAuth(snapshot.Version, auth =>
                {
                    auth.RefreshToken = tokens.RefreshToken;
                    auth.DisplayName = tokens.DisplayName;
                    auth.AccountId = tokens.AccountId;
                    auth.LastAuthenticated = DateTime.UtcNow;
                }, () =>
                {
                    _currentTokens = tokens;
                    SetIsAuthenticated(true);
                    _displayName = tokens.DisplayName;
                    _gamesDiscovered = snapshot.Auth.GamesDiscovered;
                    _lastCollectionUtc = _stateService.GetEpicMappingCollectedAt() ?? snapshot.Auth.LastAuthenticated;
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (ValidationException ex)
            {
                _logger.LogWarning(ex, "Epic refresh token was rejected");
                _authStorage.TryInvalidateAuth(snapshot.Version, () =>
                {
                    SetIsAuthenticated(false);
                    _displayName = null;
                    _gamesDiscovered = 0;
                    _currentTokens = null;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh Epic mapping session");
                _authStorage.RunIfCurrent(snapshot.Version, () =>
                {
                    SetIsAuthenticated(false);
                    _currentTokens = null;
                });
            }
        }
        finally { _sessionLock.Release(); }
    }
}
