using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Middleware;

namespace LancacheManager.Core.Services.EpicMapping;

public partial class EpicMappingService
{
    public string ScheduleServiceKey => "epicMapping";

    protected override bool SupportsNotifications => true;
    protected override NotificationMode DefaultNotificationMode => NotificationMode.Manual;

    /// <summary>Terminal stage key for a run that collected nothing because Epic is signed out.</summary>
    private const string EpicSignInSkipStageKey = "signalr.epicMapping.skippedNotSignedIn";

    /// <summary>
    /// Terminal stage key for a signed-out run that still named downloads from the stored patterns.
    /// It cannot use the ordinary completed terminal, which says the catalog was refreshed.
    /// </summary>
    private const string EpicResolvedWithoutSignInStageKey = "signalr.epicMapping.resolvedWithoutSignIn";

    protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
    {
        if (_cancellationTokenSource.Token.IsCancellationRequested || Volatile.Read(ref _isRunning) == 0)
        {
            return;
        }

        await WaitForAutoReconnectAsync(stoppingToken);
        // A saved login that failed to renew at startup or after a network drop gets one more try before the run
        // settles for the signed-out path.
        if ((!_isAuthenticated || _currentTokens is null) && _processingLogin is null)
        {
            try
            {
                // A cancel of this run, as well as a shutdown, stops the renew.
                using var renew = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token, stoppingToken);
                await TryAutoReconnectAsync(renew.Token);
            }
            // A token request that hits the client timeout surfaces as a canceled task that is not a shutdown; the run
            // still ends with the signed-out result instead of failing.
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Epic saved login could not be renewed before the scheduled run");
            }
        }
        if (!_isAuthenticated || _currentTokens is null)
        {
            await RunWithoutSignInAsync(stoppingToken, CurrentRunNotice);
            return;
        }

        _logger.LogInformation("Starting scheduled Epic catalog refresh");
        if (TryStartRefresh(stoppingToken, CurrentRunTrigger, CurrentRunNotice) && _currentRefreshTask is not null)
        {
            await _currentRefreshTask;
        }
    }

    public bool TryStartRefresh(
        CancellationToken cancellationToken = default,
        RunTrigger trigger = RunTrigger.Manual,
        RunNotice? notice = null)
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

        var runNotice = notice ?? new RunNotice(EffectiveNotificationMode, trigger);
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
                runNotice,
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


                await reporter.CompleteAsync(
                    success: true,
                    context: CreateEpicContext());
                _logger.LogInformation("Epic catalog refresh completed successfully");
            }
            catch (OperationCanceledException ex) when (ex.InnerException is not TimeoutException)
            {
                _logger.LogInformation("Epic catalog refresh canceled");
                await reporter.CompleteAsync(
                    success: false,
                    canceled: true,
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
    /// Runs the part of a refresh that needs no Epic account. Being signed in only decides whether a
    /// NEW catalog can be fetched; matching already-ingested downloads against the patterns already
    /// stored is pure database work, so it runs either way and rows stop waiting for the next log
    /// pass to get their names. The run reports itself skipped only when it also resolved nothing,
    /// and goes through the same reporter and terminal stage-key override as every other outcome, so
    /// the notification mode still decides whether it is shown.
    /// </summary>
    private async Task RunWithoutSignInAsync(CancellationToken stoppingToken, RunNotice? notice = null)
    {
        _logger.LogInformation(
            "Epic catalog refresh: no Epic account is signed in, so no new catalog is collected - resolving downloads against the stored patterns instead");
        // No catalog is collected on this path, so the counts a signed-in run left behind are not
        // this run's. The two signed-in paths clear them at the top of their own run for the same
        // reason; without this the terminal carries the previous run's numbers.
        _lastNewGames = 0;
        _lastUpdatedGames = 0;
        await using var reporter = CreateEpicMappingReporter(stoppingToken, notice ?? CurrentRunNotice);
        // This pass may finish having changed nothing, so it stays quiet either way rather than
        // claiming progress it might then report as skipped.
        reporter.SuppressProgress();
        await reporter.StartAsync(CreateEpicContext());

        var resolved = 0;
        try
        {
            resolved = await ResolveDownloadsAsync(reporter.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to resolve Epic downloads while signed out");
        }

        var changedNothing = resolved == 0;
        // Neither outcome refreshed a catalog, so neither may fall back to the completed terminal,
        // which says it did. One says the run found nothing to name, the other says what it named
        // from the patterns already stored.
        await reporter.CompleteAsync(
            success: true,
            stageKey: changedNothing ? EpicSignInSkipStageKey : EpicResolvedWithoutSignInStageKey,
            context: CreateEpicContext(),
            skipped: changedNothing);
    }

    private MappingOperationReporter CreateEpicMappingReporter(
        CancellationToken token,
        RunNotice notice,
        Action? onTerminalCleanup = null) =>
        new(
            _notifications,
            _operationTracker,
            MappingOperations.Epic,
            notice,
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

    // Reports at most 20 times per loop, spread from fromPercent to toPercent, and always on the last asset.
    private Func<int, int, Task> ProgressReport(MappingOperationReporter reporter, string stageKey, double fromPercent, double toPercent)
    {
        var lastReportedBucket = -1;
        return async (done, total) =>
        {
            var bucket = done * 20 / total;
            if (bucket <= lastReportedBucket && done != total) return;

            lastReportedBucket = bucket;
            var context = CreateEpicContext();
            context["done"] = done;
            context["total"] = total;
            await reporter.ReportAsync(fromPercent + (toPercent - fromPercent) * done / total, stageKey, context);
        };
    }

    private async Task WaitForAutoReconnectAsync(CancellationToken stoppingToken)
    {
        if (!_startupAutoReconnectCompleted.Task.IsCompleted)
        {
            _logger.LogInformation("Waiting for Epic startup auto-reconnect to finish before scheduled refresh");
            await _startupAutoReconnectCompleted.Task.WaitAsync(stoppingToken);
        }
    }

    public Task<bool> CancelRefreshAsync(IntegrationCaller? caller = null, Guid? attemptId = null)
    {
        // The save clears the pending login, so the storage call below refuses a cancel after it with attempt-expired;
        // a cancel naming the attempt whose mapping is running stops that mapping instead.
        if (caller is not null && attemptId is { } id && _processingLogin is { } processing &&
            processing.AttemptId == id && !_authStorage.IsIntegrationLoginCurrent(processing))
        {
            IntegrationLease.ValidateCaller(processing, caller);
            _currentMappingReporter?.RequestCancellation();
            try
            {
                _currentRefreshCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A sign-in that ended between the read and the cancel has nothing left to stop.
            }
            return Task.FromResult(true);
        }
        if (caller is not null && (_loginAttempt is not null || attemptId is not null))
        {
            _authStorage.CancelIntegrationLogin(caller, attemptId, () =>
            {
                if (_processingLogin?.AttemptId == attemptId)
                {
                    _currentMappingReporter?.RequestCancellation();
                    try
                    {
                        _currentRefreshCts?.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // A sign-in that ended between the read and the cancel has nothing left to stop.
                    }
                }
                _loginAttempt = null;
            });
            return Task.FromResult(true);
        }
        // The source and the card's reporter are read as one pair. A pair whose source is no longer the current refresh
        // belongs to a refresh that ended, and a successor started since is never touched.
        var refresh = _currentRefreshCts;
        var reporter = _currentMappingReporter;
        if (_isProcessingInt == 0 || refresh is null || reporter is null || !ReferenceEquals(_currentRefreshCts, refresh))
        {
            return Task.FromResult(false);
        }

        // A refresh whose card has not registered yet has no run for the tracker to cancel; its reporter cancels its own
        // source, and the run it registers next starts canceled.
        if (reporter.OperationId == Guid.Empty)
        {
            return Task.FromResult(reporter.RequestCancellation());
        }

        _logger.LogInformation(
            "Canceling active Epic catalog refresh (operationId: {OperationId})",
            reporter.OperationId);
        // The tracker answers whether the run was still going, so a refresh that completed during this call is not
        // reported canceled.
        return Task.FromResult(_operationTracker.CancelOperation(reporter.OperationId)
            is not (OperationCancelResult.NotFound or OperationCancelResult.AlreadyFinished));
    }

    private async Task RefreshCatalogAsync(
        MappingOperationReporter reporter,
        CancellationToken cancellationToken)
    {
        var snapshot = _authStorage.GetIntegrationSnapshot();
        var version = snapshot.Version;
        var releaseVersion = _authStorage.IntegrationReleaseVersion;
        var activeTokens = _currentTokens;
        void EnsureCurrent()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_authStorage.IsIntegrationCurrent(version)) return;
            // Every saved-login write moves the version, so a token rotation of this same login keeps the run going;
            // a logout, a pending sign-in or a different account still stops it.
            var current = _authStorage.GetIntegrationSnapshot();
            if (_authStorage.IntegrationReleaseVersion != releaseVersion
                || current.Auth.AccountId != snapshot.Auth.AccountId
                || !_authStorage.IsIntegrationCurrent(current.Version))
            {
                throw new OperationCanceledException();
            }
            version = current.Version;
        }
        EnsureCurrent();
        _lastNewGames = 0;
        _lastUpdatedGames = 0;
        _currentProgressPercent = 0;

        if (activeTokens is not null && activeTokens.ExpiresAt <= DateTime.UtcNow)
        {
            _logger.LogInformation("Access token expired, refreshing before catalog update...");
            try
            {
                var tokens = await _epicApiClient.RefreshTokenAsync(
                    snapshot.Auth.RefreshToken!,
                    cancellationToken);
                EnsureCurrent();
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
                });
                if (updated is null) throw new OperationCanceledException();
                version = updated.Value;
                activeTokens = tokens;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (ValidationException ex)
            {
                _authStorage.TryInvalidateAuth(version, () =>
                {
                    SetIsAuthenticated(false);
                    _displayName = null;
                    _gamesDiscovered = 0;
                    _currentTokens = null;
                });
                throw new InvalidOperationException("Epic access token refresh failed", ex);
            }
        }

        if (activeTokens is null)
        {
            throw new InvalidOperationException("No valid Epic token is available for catalog refresh");
        }

        _currentProgressPercent = 15;
        await reporter.ReportAsync(
            15,
            "signalr.epicMapping.fetchingGames",
            CreateEpicContext());
        var games = await _epicApiClient.GetOwnedGamesAsync(
            activeTokens.AccessToken,
            ProgressReport(reporter, "signalr.epicMapping.readingLibrary", 15, 40),
            cancellationToken);
        EnsureCurrent();
        if (games.Count > 0)
        {
            var sessionHash = CryptoUtils.ComputeAnonymousHash("mapping-session");
            var result = await MergeOwnedGamesAsync(
                games,
                sessionHash,
                "scheduled-refresh",
                cancellationToken);
            EnsureCurrent();
            // The release check refuses the write once a logout began, so a cleared record is never re-created and the
            // in-memory values a logout reset are never set again. saveSavedLogin: false moves no version, so the later
            // EnsureCurrent calls and a prefill's lease-checked token write still pass; the per-account saved copy keeps
            // its older count until the next credential save, and a restart reads the main record.
            try
            {
                _authStorage.UpdateAuthData(auth =>
                {
                    auth.GamesDiscovered = result.TotalGames;
                    _gamesDiscovered = result.TotalGames;
                    _lastCollectionUtc = DateTime.UtcNow;
                    _stateService.SetEpicMappingLastCollection(_lastCollectionUtc.Value);
                    _lastNewGames += result.NewGames;
                    _lastUpdatedGames += result.UpdatedGames;
                }, saveSavedLogin: false, expectedReleaseVersion: releaseVersion);
            }
            catch (ConflictException)
            {
                throw new OperationCanceledException();
            }
        }

        var failedSteps = 0;

        _currentProgressPercent = 40;
        await reporter.ReportAsync(
            40,
            "signalr.epicMapping.refreshingCdn",
            CreateEpicContext());
        try
        {
            var cdnInfos = await _epicApiClient.GetCdnInfoAsync(
                activeTokens.AccessToken,
                new HashSet<string>(),
                ProgressReport(reporter, "signalr.epicMapping.resolvingPaths", 40, 60),
                cancellationToken);
            EnsureCurrent();
            if (cdnInfos.Count > 0)
            {
                await MergeCdnPatternsAsync(cdnInfos, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex.InnerException is TimeoutException)
        {
            failedSteps++;
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
            EnsureCurrent();
            if (freeGames.Count > 0)
            {
                var sessionHash = CryptoUtils.ComputeAnonymousHash("free-games-discovery");
                var result = await MergeOwnedGamesAsync(
                    freeGames,
                    sessionHash,
                    "free-games",
                    cancellationToken);
                EnsureCurrent();
                _lastNewGames += result.NewGames;
                _lastUpdatedGames += result.UpdatedGames;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || ex.InnerException is TimeoutException)
        {
            failedSteps++;
            _logger.LogWarning(ex, "Epic free-game discovery skipped");
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
        catch (Exception ex) when (ex is not OperationCanceledException || ex.InnerException is TimeoutException)
        {
            failedSteps++;
            _logger.LogWarning(ex, "Failed to resolve Epic downloads");
        }

        if (failedSteps > 0)
        {
            reporter.SetWarning(new RunWarning(
                "common.notifications.warnings.epicStepsFailed",
                new Dictionary<string, object?> { ["count"] = failedSteps }));
        }

        EnsureCurrent();
        // Release-checked like the count write: a prefill holding its lease must not end this run as canceled, and a
        // logout must not get a login or a refresh time back.
        try
        {
            _authStorage.UpdateAuthData(auth =>
            {
                var now = DateTime.UtcNow;
                auth.LastAuthenticated = now;
                _lastRefreshTime = now;
            }, saveSavedLogin: false, expectedReleaseVersion: releaseVersion);
        }
        catch (ConflictException)
        {
            throw new OperationCanceledException();
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
