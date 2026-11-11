using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    private IntegrationLogin? _loginAttempt;
    private IntegrationLogin? _processingLogin;

    public IntegrationAccess GetIntegrationAccess(IntegrationCaller caller) => _authStorage.GetIntegrationAccess(caller);
    // A sign-in's mapping holds the session lock until it ends, so a prefill with the saved login is refused at once
    // instead of waiting on that lock while it holds the store's admission gate.
    public string? GetIntegrationLoginReason(IntegrationCaller caller) =>
        _processingLogin is not null ? "login-in-progress" : _authStorage.GetIntegrationLoginReason(caller);

    public Task<IntegrationLease> AcquireIntegrationLoginAsync(IntegrationCaller caller, CancellationToken cancellationToken = default)
    {
        if (_processingLogin is not null) IntegrationLease.Refuse("login-in-progress");
        return _authStorage.AcquireIntegrationLoginAsync(caller, cancellationToken);
    }

    public bool TryGetSavedLoginAccount(Guid? accountId, out string? account, IntegrationCaller? caller = null)
    {
        caller ??= new IntegrationCaller(accountId, Guid.Empty, true);
        account = null;
        if (GetIntegrationLoginReason(caller) is not null) return false;
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
        // A caller-passed lease skips the check in AcquireIntegrationLoginAsync.
        if (_processingLogin is not null) IntegrationLease.Refuse("login-in-progress");
        await _sessionLock.WaitAsync(cancellationToken);
        try
        {
            // A sign-in can start while this request waits for the lock; refusing inside the try releases the lock.
            if (_processingLogin is not null) IntegrationLease.Refuse("login-in-progress");
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
        // Refused before the store is touched, so the attempt id is not spent and the store's version does not move.
        if (_processingLogin is not null) throw AuthInProgress();
        var login = await _authStorage.BeginIntegrationLoginAsync(caller, attemptId, recover);
        var signInRunning = false;
        var started = _authStorage.RunIntegrationLogin(login, () =>
        {
            // A begin that passed the check above before a code submit marked its sign-in processing is undone here,
            // under the same lock, so its pending login never blocks refreshes and prefills.
            if (_processingLogin is not null)
            {
                signInRunning = true;
                _authStorage.FinishIntegrationLogin(login);
            }
            else
            {
                _loginAttempt = login;
            }
        });
        if (signInRunning) throw AuthInProgress();
        if (!started) throw new OperationCanceledException();
        _ = AnnounceLoginWindowEndAsync(login);
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
    /// Exchanges the one-time auth code and saves the sign-in, then returns. The owned game/CDN mapping
    /// runs afterwards in the background and enters the tracked lifecycle before the save, so its card exists
    /// when the browser gets its answer.
    /// </summary>
    public async Task OnAuthCodeReceivedAsync(string authorizationCode, Guid? ownerAccountId = null,
        IntegrationCaller? caller = null, Guid? attemptId = null)
    {
        caller ??= new(ownerAccountId, ownerAccountId, ownerAccountId is not null);
        var login = _authStorage.ContinueIntegrationLogin(caller, attemptId);
        if (Interlocked.CompareExchange(ref _isProcessingInt, 1, 0) != 0)
        {
            throw AuthInProgress();
        }

        CancellationTokenSource authCts;
        try
        {
            authCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        }
        catch (ObjectDisposedException)
        {
            authCts = new CancellationTokenSource();
        }

        if (!_authStorage.RunIntegrationLogin(login, () =>
        {
            _processingLogin = login;
            _currentRefreshCts = authCts;
        }))
        {
            authCts.Dispose();
            Interlocked.Exchange(ref _isProcessingInt, 0);
            // A cancel or the window's end that came between the continue check and this call ends the sign-in here.
            _authStorage.RecordIntegrationLoginEnding(login, new IntegrationLoginEnding(
                login.AttemptId, OperationStatus.Canceled, "errors.integration.attemptExpired"));
            await NotifyLoginEndedAsync(login);
            throw new OperationCanceledException();
        }

        await _sessionLock.WaitAsync();
        // The sign-in window has its own timer, counted from after the session lock is taken. It bounds the steps before the
        // save and the mapping after it; a person's cancel, a logout or a shutdown cancels the sign-in's own source.
        var window = new CancellationTokenSource();
        var remaining = login.ExpiresAtUtc - DateTime.UtcNow;
        window.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
        // Every step waits on this token (through steps once the card exists), so the window ends the code exchange and,
        // after the save, the background mapping.
        var attempt = CancellationTokenSource.CreateLinkedTokenSource(authCts.Token, window.Token);
        MappingOperationReporter? reporter = null;
        long releaseVersion = 0;
        var handedOff = false;
        try
        {
            _currentStatus = EpicMappingStatus.Authenticating;
            _logger.LogInformation("Exchanging Epic authorization code for tokens...");
            var tokens = await _epicApiClient.ExchangeAuthCodeAsync(
                authorizationCode,
                attempt.Token);
            attempt.Token.ThrowIfCancellationRequested();
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

            // The card's X before the save still cancels the sign-in, as it did when the card's source was part of every step.
            attempt.Token.ThrowIfCancellationRequested();
            reporter.Token.ThrowIfCancellationRequested();
            if (!_authStorage.CompleteIntegrationLogin(login, new EpicAuthData
            {
                OwnerAccountId = login.AccountId,
                RefreshToken = tokens.RefreshToken,
                DisplayName = tokens.DisplayName,
                AccountId = tokens.AccountId,
                LastAuthenticated = DateTime.UtcNow,
                GamesDiscovered = _gamesDiscovered
            }, () =>
            {
                // A collection time belongs to the Epic account whose mapping finished; a different account starts without one.
                if (_currentTokens?.AccountId != tokens.AccountId)
                {
                    _lastCollectionUtc = null;
                    _stateService.SetEpicMappingLastCollection(null);
                }
                // Read under the save's lock, so an account reset right after the save refuses this sign-in's count write.
                releaseVersion = _authStorage.IntegrationReleaseVersion;
                _currentTokens = tokens;
                SetIsAuthenticated(true);
                _displayName = tokens.DisplayName;
                // Recorded with the save, under the store's lock, so no status read finds the attempt over without
                // it and a later cancel of the banner pass cannot replace it.
                _authStorage.RecordIntegrationLoginEnding(login, new IntegrationLoginEnding(
                    login.AttemptId, OperationStatus.Completed, "signalr.epicMapping.completed"));
            })) throw new OperationCanceledException();
            _ = Task.Run(() => RunSignInMappingAsync(reporter, tokens, login, authCts, window, attempt, releaseVersion));
            handedOff = true;
        }
        // A person's cancel, a logout or a shutdown: the window's timer had not fired and the clock had not passed its end.
        // A check that reads the clock can find the window over a moment before the timer's callback runs.
        catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException &&
                                                    !window.IsCancellationRequested && DateTime.UtcNow < login.ExpiresAtUtc)
        {
            _logger.LogInformation("Epic mapping auth canceled");
            if (reporter is not null)
            {
                await reporter.CompleteAsync(
                    success: false,
                    canceled: true,
                    context: CreateEpicContext());
            }
            // The request this cancel ends answers 499, so a browser reads how its sign-in ended by its attempt id.
            _authStorage.RecordIntegrationLoginEnding(login, new IntegrationLoginEnding(
                login.AttemptId, OperationStatus.Canceled, "errors.integration.attemptExpired"));
            throw;
        }
        catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException)
        {
            // The sign-in window ran out before the account was saved: the app's limit, so the run ends red.
            _logger.LogInformation("Epic mapping sign-in window ran out before the account was saved");
            if (reporter is not null)
            {
                await reporter.CompleteAsync(
                    success: false,
                    error: "errors.integration.attemptExpired",
                    stageKey: "errors.integration.attemptExpired",
                    context: CreateEpicContext());
            }
            _authStorage.RecordIntegrationLoginEnding(login, new IntegrationLoginEnding(
                login.AttemptId, OperationStatus.Failed, "errors.integration.attemptExpired"));
            // The sign-in dialog shows this refusal; a rethrown cancel would reach it as a silent aborted request.
            IntegrationLease.Refuse("attempt-expired");
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

            _authStorage.RecordIntegrationLoginEnding(login, ex is ApiException { StageKey: { } failureKey } refusal
                ? new IntegrationLoginEnding(login.AttemptId, OperationStatus.Failed, failureKey, refusal.Context)
                : new IntegrationLoginEnding(login.AttemptId, OperationStatus.Failed, "modals.epicAuth.errors.loginFailed"));

            throw;
        }
        finally
        {
            if (!handedOff)
            {
                await ReleaseSignInAsync(login, reporter, authCts, window, attempt);
            }
        }
    }

    // The mapping after the save. It owns every release of the sign-in, so the session lock, the processing flag and the
    // sign-in's cancel source stay held until it ends.
    private async Task RunSignInMappingAsync(
        MappingOperationReporter reporter,
        EpicOAuthTokens tokens,
        IntegrationLogin login,
        CancellationTokenSource authCts,
        CancellationTokenSource window,
        CancellationTokenSource attempt,
        long releaseVersion)
    {
        var games = new List<OwnedGame>();
        var cdnPaths = new List<CdnInfo>();
        MergeResult? gamesMerge = null;
        var cdnMerged = false;
        var failedSteps = 0;
        try
        {
            _lastNewGames = 0;
            _lastUpdatedGames = 0;
            await reporter.ReportAsync(
                5,
                "signalr.epicMapping.codeAccepted",
                CreateEpicContext());
            // The sign-in's window still bounds the mapping after the save, so a catalog that stops answering cannot hold
            // the sign-in locks past it.
            using var steps = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, reporter.Token);

            await reporter.ReportAsync(
                15,
                "signalr.epicMapping.fetchingGames",
                CreateEpicContext());
            games = await _epicApiClient.GetOwnedGamesAsync(
                tokens.AccessToken,
                ProgressReport(reporter, "signalr.epicMapping.readingLibrary", 15, 55),
                steps.Token);
            steps.Token.ThrowIfCancellationRequested();

            if (games.Count > 0)
            {
                gamesMerge = await MergeOwnedGamesAsync(
                    games,
                    CryptoUtils.ComputeAnonymousHash("mapping-session"),
                    "mapping-login",
                    steps.Token);
                StoreMappedCount(gamesMerge, complete: false, releaseVersion);
            }

            await reporter.ReportAsync(
                55,
                "signalr.epicMapping.refreshingCdn",
                CreateEpicContext());
            try
            {
                // A game whose build moved to a new CDN path is not re-read at sign-in; the next scheduled refresh reads every game.
                HashSet<string> mappedAppIds;
                using (var db = _dbContextFactory.CreateDbContext())
                {
                    mappedAppIds = await db.EpicCdnPatterns.AsNoTracking().Select(p => p.AppId).ToHashSetAsync(steps.Token);
                }
                cdnPaths = await _epicApiClient.GetCdnInfoAsync(
                    tokens.AccessToken,
                    mappedAppIds,
                    ProgressReport(reporter, "signalr.epicMapping.resolvingPaths", 55, 85),
                    steps.Token);
                steps.Token.ThrowIfCancellationRequested();
                if (cdnPaths.Count > 0)
                {
                    await MergeCdnPatternsAsync(cdnPaths, steps.Token);
                    cdnMerged = true;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || ex.InnerException is TimeoutException)
            {
                failedSteps++;
                _logger.LogWarning(ex, "Failed to collect Epic CDN patterns from mapping login");
            }

            await reporter.ReportAsync(
                85,
                "signalr.epicMapping.applyingMappings",
                CreateEpicContext());
            try
            {
                await ResolveDownloadsAsync(steps.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failedSteps++;
                _logger.LogWarning(ex, "Failed to resolve Epic downloads after login");
            }

            // Only a finished mapping sets the collection time: it means "a mapping finished", and the wizard shows Done
            // after a restart from it. A canceled mapping keeps the sign-in and the count it reached, never the time.
            StoreMappedCount(null, complete: true, releaseVersion);
            await _notifications.NotifyAllAsync(SignalREvents.EpicGameMappingsUpdated, new
            {
                totalGames = _gamesDiscovered,
                newGames = _lastNewGames,
                updatedGames = _lastUpdatedGames,
                lastUpdatedUtc = DateTime.UtcNow,
                source = "mapping-login"
            });
            // A follow-up step that failed after the account was saved leaves the sign-in done with a warning, as the
            // scheduled refresh counts it.
            if (failedSteps > 0)
            {
                reporter.SetWarning(new RunWarning(
                    "common.notifications.warnings.epicStepsFailed",
                    new Dictionary<string, object?> { ["count"] = failedSteps }));
            }
            var done = CreateEpicContext();
            done["count"] = _gamesDiscovered;
            await reporter.CompleteAsync(success: true, stageKey: "signalr.epicMapping.mappingDone", context: done);

            _logger.LogInformation(
                "Epic mapping login complete: {DisplayName}, {Games} games",
                tokens.DisplayName,
                _gamesDiscovered);
        }
        // A person's cancel, a logout, a shutdown or the window's end. What was read before it is kept; the asset in flight
        // is left out.
        catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException)
        {
            _logger.LogInformation("Epic sign-in mapping canceled");
            try
            {
                if (gamesMerge is null && games.Count > 0)
                {
                    StoreMappedCount(
                        await MergeOwnedGamesAsync(
                            games,
                            CryptoUtils.ComputeAnonymousHash("mapping-session"),
                            "mapping-login",
                            CancellationToken.None),
                        complete: false,
                        releaseVersion);
                }
                if (!cdnMerged && cdnPaths.Count > 0)
                {
                    await MergeCdnPatternsAsync(cdnPaths, CancellationToken.None);
                }
            }
            catch (Exception mergeEx)
            {
                _logger.LogWarning(mergeEx, "Failed to keep the Epic games read before the sign-in mapping was canceled");
            }
            await reporter.CompleteAsync(
                success: false,
                canceled: true,
                context: CreateEpicContext());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Epic sign-in mapping failed after the sign-in was saved");
            await reporter.CompleteAsync(
                success: false,
                error: "signalr.epicMapping.failed",
                stageKey: "signalr.epicMapping.failed",
                context: CreateEpicContext(ex.Message));
        }
        finally
        {
            await ReleaseSignInAsync(login, reporter, authCts, window, attempt);
        }
    }

    // Writes the merged count to the stored sign-in; the tokens come from the stored record. A complete mapping also
    // sets the collection time. A release since the save (a logout or an account reset) refuses the write, and the
    // mapping goes on to its end.
    private void StoreMappedCount(MergeResult? merge, bool complete, long releaseVersion)
    {
        try
        {
            _authStorage.UpdateAuthData(stored =>
            {
                if (merge is not null)
                {
                    stored.GamesDiscovered = merge.TotalGames;
                    _gamesDiscovered = merge.TotalGames;
                    _lastNewGames = merge.NewGames;
                    _lastUpdatedGames = merge.UpdatedGames;
                }
                if (!complete) return;
                var now = DateTime.UtcNow;
                _lastCollectionUtc = now;
                _lastRefreshTime = now;
                _stateService.SetEpicMappingLastCollection(now);
            }, expectedReleaseVersion: releaseVersion);
        }
        catch (ConflictException)
        {
            _logger.LogInformation("Epic sign-in was released during its mapping; the stored count was not written");
        }
    }

    private async Task ReleaseSignInAsync(
        IntegrationLogin login,
        MappingOperationReporter? reporter,
        CancellationTokenSource authCts,
        CancellationTokenSource window,
        CancellationTokenSource attempt)
    {
        if (reporter is not null)
        {
            await reporter.DisposeAsync();
        }

        authCts.Dispose();
        window.Dispose();
        attempt.Dispose();
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
        // The flag a begin reads first goes last: with the other order a begin between the two lines passes both checks
        // and its code submit then loses the compare-exchange.
        Interlocked.Exchange(ref _isProcessingInt, 0);
        if (_processingLogin == login) _processingLogin = null;
        _sessionLock.Release();
        await NotifyLoginEndedAsync(login);
    }

    private static ConflictException AuthInProgress() =>
        new("Epic auth is already in progress") { StageKey = "errors.epic.authInProgress" };

    // A browser that lost this sign-in's answer reads its ending when this arrives.
    private async Task NotifyLoginEndedAsync(IntegrationLogin login)
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.IntegrationLoginEnded, new IntegrationLoginEnded(login.AttemptId));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to send the Epic sign-in ending for {AttemptId}", login.AttemptId);
        }
    }

    // A code that never reached the server leaves no submit to end its attempt, so the attempt's window end is announced
    // here: a dialog waiting on it reads once and finds the attempt gone. The delay runs on a monotonic timer while the
    // attempt's window is judged by the wall clock, so a host clock that stepped back meanwhile is waited out: the push
    // goes only once the wall clock is past the window's end. The added second keeps the loop from waking just short of it.
    private async Task AnnounceLoginWindowEndAsync(IntegrationLogin login)
    {
        TimeSpan remaining;
        // Task.Delay refuses more than about 49.7 days, so a clock stepped back further than that is waited out a day at a time.
        while ((remaining = login.ExpiresAtUtc - DateTime.UtcNow) >= TimeSpan.Zero)
            await Task.Delay(TimeSpan.FromTicks(Math.Min((remaining + TimeSpan.FromSeconds(1)).Ticks, TimeSpan.FromDays(1).Ticks)));
        await NotifyLoginEndedAsync(login);
    }

    public async Task LogoutAsync(IntegrationCaller? caller = null)
    {
        // Read before the access check: a sign-in is pending before it sets this field, so a source read ahead of a
        // check that saw no pending sign-in is never a later sign-in's.
        var runningRefresh = _currentRefreshCts;
        // Canceled before the release waits, so the release does not wait out a running refresh; a refused logout
        // stops nothing.
        if (caller is null || _authStorage.GetIntegrationAccess(caller).CanLogout)
        {
            try
            {
                runningRefresh?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A sign-in that ended between the read and the cancel has nothing left to stop.
            }
        }
        await using var release = await _authStorage.BeginIntegrationReleaseAsync(caller);
        await _sessionLock.WaitAsync();
        try
        {
            _authStorage.CompleteIntegrationRelease(release);
            SetIsAuthenticated(false);
            _displayName = null;
            _lastCollectionUtc = null;
            _stateService.SetEpicMappingLastCollection(null);
            _gamesDiscovered = 0;
            _currentTokens = null;
            _logger.LogInformation("Epic mapping session logged out and credentials cleared");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task TryAutoReconnectAsync(CancellationToken cancellationToken)
    {
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
                    _lastCollectionUtc = _stateService.GetEpicMappingCollectedAt();
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
