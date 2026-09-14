using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Middleware;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Core.Services.Xbox;

/// <summary>
/// Manager-side, daemon-free Xbox MSA device-code login partial for
/// <see cref="XboxCatalogMappingService"/>. Mirrors
/// <c>EpicMappingService.Authentication.cs</c> in shape (auth state + login + logout +
/// startup auto-reconnect, feeding the EXISTING <c>MergeDaemonCatalogAsync</c> + <c>ResolveDownloadsAsync</c>),
/// but adapted for the MSA device-code grant: the backend POLLS the token endpoint in the background
/// instead of accepting a pasted code, so StartLoginAsync returns a device-code challenge
/// and authentication state is surfaced separately from the tracked mapping lifecycle.
/// </summary>
public partial class XboxCatalogMappingService
{
    // Auth state (shared across the partial via the single class instance).
    private bool _isAuthenticated;
    private string? _displayName;
    private DateTime? _lastCollectionUtc;
    private int _gamesDiscovered;
    private string? _xuid;

    // MSA device-code refresh tokens carry no returned expiry; ~90 days is Microsoft's documented
    // inactivity lifetime. The login auto-renews on startup reconnect and the 12h schedule refresh,
    // so this expiry slides forward while the server is running. Public so the scheduled-prefill
    // auth surfaces reuse the SAME validity window as the Integrations card (single source of truth).
    public static readonly TimeSpan XboxLoginValidity = TimeSpan.FromDays(90);

    /// <summary>Stage key the sign-in card and the auth-state event both carry during the approval wait.</summary>
    private const string XboxAwaitingSignInStageKey = "signalr.xbox.mapping.authenticating";

    // Serializes auth-state mutations so a completing login and a logout cannot interleave.
    private readonly SemaphoreSlim _authSessionLock = new(1, 1);
    // Serializes device requests after admission; an incumbent attempt must be cancelled explicitly.
    private readonly SemaphoreSlim _loginStartLock = new(1, 1);

    // The in-flight sign-in's tracked operation, so a logout and the modal's cancel can stop it. Held
    // apart from _currentMappingReporter because a scheduled refresh writes that field too
    // (Scheduling.cs:50), and cancelling a sign-in must never stop a catalog refresh. Non-null for the
    // WHOLE attempt (approval wait plus the catalog stretch after it), which is what GetAuthStatus
    // reports. Volatile because the poll task clears it and a request thread reads it.
    private volatile MappingOperationReporter? _loginReporter;
    private IntegrationLogin? _loginAttempt;

    // True only while the device-code poll waits for the person to approve, not for the whole login:
    // _loginReporter stays set through the catalog harvest that follows approval, so a Schedules row
    // driven off that field would claim a sign-in is pending during an ordinary refresh. Volatile
    // because the poll runs on its own task and the schedule registry reads this on a request thread.
    private volatile bool _awaitingSignIn;

    /// <summary>True once a saved/just-completed MSA session is active. Drives the auth-status surface.</summary>
    public bool IsAuthenticated => _isAuthenticated;
    public string? DisplayName => _displayName;
    public DateTime? LastCollectionUtc => _lastCollectionUtc;
    public int GamesDiscovered => _gamesDiscovered;

    /// <summary>The authenticated account's Xbox user id (xuid), captured for diagnostics.</summary>
    public string? Xuid => _xuid;

    /// <summary>
    /// True while a device-code login is waiting for the user to approve it in their browser.
    /// <c>ServiceScheduleRegistry</c> reads this by reflection to tell the xboxMapping row why Run Now
    /// is disabled, so it has to stay a property named exactly this and typed exactly <c>bool</c> -
    /// a field, a method or a <c>bool?</c> reads back as absent and the row silently says nothing.
    /// </summary>
    public bool AwaitingSignIn => _awaitingSignIn;

    /// <summary>Returns the current auth snapshot for the REST <c>auth-status</c> endpoint.</summary>
    public XboxMappingAuthStatus GetAuthStatus(IntegrationCaller? caller = null)
    {
        var access = caller is null ? null : _authStorage.GetIntegrationAccess(caller);
        return new XboxMappingAuthStatus
        {
            IsAuthenticated = _isAuthenticated,
            CanManage = access?.CanManage ?? false,
            CanSignIn = access?.CanSignIn ?? false,
            CanLogout = access?.CanLogout ?? false,
            CanCancel = access?.CanCancel ?? false,
            CanRecover = access?.CanRecover ?? false,
            OwnershipReason = access?.OwnershipReason,
            AttemptId = access?.AttemptId,
            LoginExpiresAtUtc = access?.LoginExpiresAtUtc,
            DisplayName = caller is null || access!.CanManage ? _displayName : null,
            LastCollectionUtc = _lastCollectionUtc,
            GamesDiscovered = _gamesDiscovered,
            LoginInProgress = _loginReporter is not null,
            ExpiresAtUtc = _isAuthenticated && _lastCollectionUtc.HasValue
                ? _lastCollectionUtc.Value.Add(XboxLoginValidity)
                : null
        };
    }

    /// <summary>
    /// Starts the device-code login: requests a device code from MSA, kicks a background poll loop, and
    /// returns the <c>userCode</c>/<c>verificationUri</c> for the user to approve in their own browser.
    /// No Docker container and no prefill daemon are involved. Authentication state is emitted over
    /// <see cref="SignalREvents.XboxMappingAuthStateChanged"/>; catalog mapping starts only after approval.
    /// </summary>
    public Task<XboxDeviceCodeChallenge> StartLoginAsync(CancellationToken ct = default)
        => StartLoginAsync(null, ct);

    public async Task<XboxDeviceCodeChallenge> StartLoginAsync(
        Guid? ownerAccountId,
        CancellationToken ct = default,
        IntegrationCaller? caller = null,
        Guid? attemptId = null,
        bool recover = false)
    {
        caller ??= new(ownerAccountId, ownerAccountId, ownerAccountId is not null);
        var login = await _authStorage.BeginIntegrationLoginAsync(caller, attemptId, recover, ct);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        lifetime.CancelAfter(login.ExpiresAtUtc - DateTime.UtcNow);
        MappingOperationReporter? reporter = null;
        var startHeld = false;
        try
        {
            await _loginStartLock.WaitAsync(ct);
            startHeld = true;
            if (!_authStorage.RunIntegrationLogin(login, () =>
            {
                _refreshShowNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);
                reporter = new MappingOperationReporter(_notifications, _operationTracker, MappingOperations.Xbox,
                    _refreshShowNotification, lifetime.Token, _logger);
                _loginAttempt = login;
                _loginReporter = reporter;
            })) throw new OperationCanceledException();

            XboxDeviceCodeResponse deviceCode;
            using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(reporter!.Token, ct))
            {
                deviceCode = await _authClient.RequestDeviceCodeAsync(requestCts.Token);
                requestCts.Token.ThrowIfCancellationRequested();
            }
            if (deviceCode.ExpiresIn <= 0)
            {
                throw new InvalidOperationException("Xbox device code response did not contain a positive expiry");
            }
            login = _authStorage.SetIntegrationLoginExpiry(login,
                DateTime.UtcNow.AddSeconds(deviceCode.ExpiresIn));
            lifetime.CancelAfter(login.ExpiresAtUtc - DateTime.UtcNow);
            XblRequestSigner? signer = null;
            if (!_authStorage.RunIntegrationLogin(login, () =>
            {
                _loginAttempt = login;
                var credentials = login.AccountId is { } accountId
                    ? _authStorage.GetSavedLogin(accountId)
                    : new XboxAuthData();
                signer = !string.IsNullOrEmpty(credentials.DeviceKeyPkcs8)
                    ? XblRequestSigner.FromPkcs8Base64(credentials.DeviceKeyPkcs8)
                    : XblRequestSigner.CreateNew();
            })) throw new OperationCanceledException();

            var admitted = reporter!;
            _ = Task.Run(() => RunLoginPollAsync(deviceCode, signer!, admitted, login, lifetime), CancellationToken.None);
            return new XboxDeviceCodeChallenge
            {
                UserCode = deviceCode.UserCode ?? string.Empty,
                VerificationUri = deviceCode.VerificationUri ?? string.Empty,
                Interval = deviceCode.Interval,
                OperationId = reporter!.IsStarted ? reporter.OperationId : null,
                AttemptId = login.AttemptId,
                ExpiresAtUtc = login.ExpiresAtUtc
            };
        }
        catch
        {
            _authStorage.FinishIntegrationLogin(login);
            if (ReferenceEquals(_loginReporter, reporter))
            {
                _loginReporter = null;
                if (_loginAttempt == login) _loginAttempt = null;
            }
            if (reporter is not null) await reporter.DisposeAsync();
            lifetime.Dispose();
            throw;
        }
        finally
        {
            if (startHeld) _loginStartLock.Release();
        }
    }

    /// <summary>
    /// Background poll loop for a started device-code login. It holds the catalog-mapping gate and the
    /// login's tracked operation for the whole wait; on approval it runs the full token chain + catalog
    /// harvest, merges into the shared catalog, resolves downloads, persists credentials, and emits a
    /// terminal auth-state event.
    /// </summary>
    private async Task RunLoginPollAsync(
        XboxDeviceCodeResponse deviceCode,
        XblRequestSigner signer,
        MappingOperationReporter reporter,
        IntegrationLogin login,
        CancellationTokenSource lifetime)
    {
        var refreshGateHeld = false;
        try
        {
            // The sign-in holds the gate for the whole approval wait, because it registers its XboxMapping
            // operation before the wait and a scheduled tick registering a second one beside it would break
            // the one-card/one-operation contract the gate exists to hold. The wait is bounded: the device
            // code carries its own expiry and PollForTokenAsync stops at that deadline.
            await _refreshGate.WaitAsync(reporter.Token);
            refreshGateHeld = true;

            if (!_authStorage.RunIntegrationLogin(login, () => _currentMappingReporter = reporter))
                throw new OperationCanceledException();

            // The card the user watches while approving is the one this started event creates, and it
            // shows the started stage key, so that key is the waiting one rather than the generic
            // starting one. The reporter's own progress events take the message over after approval.
            await reporter.StartAsync(CreateXboxMappingContext(), XboxAwaitingSignInStageKey, login);

            XboxMsaTokenResponse msaToken;
            _awaitingSignIn = true;
            try
            {
                await EmitAuthStateAsync(
                    reporter.OperationId,
                    OperationStatus.Waiting,
                    XboxAwaitingSignInStageKey,
                    "Waiting for Microsoft sign-in...");

                msaToken = await _authClient.PollForTokenAsync(deviceCode, login.ExpiresAtUtc, reporter.Token);
            }
            finally
            {
                _awaitingSignIn = false;
            }

            reporter.Token.ThrowIfCancellationRequested();
            if (!_authStorage.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
            await reporter.ReportAsync(
                25,
                "signalr.xboxMapping.collecting",
                CreateXboxMappingContext());

            var harvest = await _authClient.HarvestCatalogAsync(
                msaToken.AccessToken!,
                signer,
                reporter.Token);

            reporter.Token.ThrowIfCancellationRequested();
            if (!_authStorage.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
            if (harvest.CdnInfos.Count > 0)
            {
                await _mappingService.MergeDaemonCatalogAsync(harvest.CdnInfos, reporter.Token);
            }
            await reporter.ReportAsync(
                70,
                "signalr.xboxMapping.resolving",
                CreateXboxMappingContext());
            var resolved = await _mappingService.ResolveDownloadsAsync(reporter.Token);
            _logger.LogInformation("Xbox mapping login resolved {Resolved} existing download(s)", resolved);

            await _authSessionLock.WaitAsync(CancellationToken.None);
            try
            {
                // A concurrent logout cancels this login's CTS and clears credentials while holding the
                // same lock. Re-check under the lock so we never persist or keep credentials a logout just
                // cleared (which would leave the session in-memory-authenticated with no stored creds).
                if (!ReferenceEquals(_loginReporter, reporter))
                {
                    throw new OperationCanceledException();
                }
                reporter.Token.ThrowIfCancellationRequested();

                // Persist credentials (refresh token + device key) for auto-reconnect, atomically with the
                // in-memory state under the lock so logout and login-success are mutually exclusive.
                if (!_authStorage.CompleteIntegrationLogin(login, new XboxAuthData
                {
                    OwnerAccountId = login.AccountId,
                    RefreshToken = msaToken.RefreshToken,
                    DeviceKeyPkcs8 = signer.ExportPkcs8Base64(),
                    DisplayName = harvest.DisplayName,
                    Xuid = harvest.Xuid,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = harvest.CdnInfos.Count
                }, () =>
                {
                    SetIsAuthenticated(true);
                    _displayName = harvest.DisplayName;
                    _xuid = harvest.Xuid;
                    _gamesDiscovered = harvest.CdnInfos.Count;
                    _lastCollectionUtc = DateTime.UtcNow;
                })) throw new OperationCanceledException();
            }
            finally
            {
                _authSessionLock.Release();
            }

            await reporter.ReportAsync(
                90,
                "signalr.xboxMapping.backfilling",
                CreateXboxMappingContext(resolved: resolved));
            try
            {
                await _mappingService.BackfillMissingBannerArtAsync(reporter.Token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Xbox banner-art backfill failed after login");
            }

            await reporter.CompleteAsync(
                success: true,
                context: CreateXboxMappingContext(resolved: resolved));
            await EmitAuthStateAsync(
                reporter.OperationId,
                OperationStatus.Completed,
                "signalr.xbox.mapping.completed",
                $"Xbox login complete - {harvest.CdnInfos.Count} games");

            _logger.LogInformation("Xbox mapping login complete: {DisplayName}, {Games} games",
                harvest.DisplayName, harvest.CdnInfos.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Xbox mapping login cancelled");
            await reporter.CompleteAsync(
                success: false,
                cancelled: true,
                context: CreateXboxMappingContext());

            await EmitAuthStateAsync(
                reporter.OperationId,
                OperationStatus.Cancelled,
                "signalr.xbox.mapping.cancelled",
                "Xbox login cancelled");
        }
        catch (XboxLogonException ex)
        {
            _logger.LogWarning(ex, "Xbox mapping login failed");
            var mappingContext = CreateXboxMappingContext(errorDetail: ex.Message);
            if (ex.Context is not null)
            {
                foreach (var (key, value) in ex.Context)
                {
                    mappingContext[key] = value;
                }
            }

            await reporter.CompleteAsync(
                success: false,
                stageKey: ex.StageKey,
                context: mappingContext);

            await EmitAuthStateAsync(
                reporter.OperationId,
                OperationStatus.Failed,
                ex.StageKey,
                context: mappingContext);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Xbox mapping login failed");
            await reporter.CompleteAsync(
                success: false,
                error: ex.Message,
                context: CreateXboxMappingContext(errorDetail: ex.Message));

            await EmitAuthStateAsync(
                reporter.OperationId,
                OperationStatus.Failed,
                "signalr.xbox.mapping.failed",
                "Xbox login failed",
                ex.Message);
        }
        finally
        {
            try
            {
                await reporter.DisposeAsync();
                if (ReferenceEquals(_currentMappingReporter, reporter))
                {
                    _currentMappingReporter = null;
                }
            }
            finally
            {
                if (refreshGateHeld)
                {
                    _refreshGate.Release();
                }

                signer.Dispose();
                lifetime.Dispose();
                _authStorage.FinishIntegrationLogin(login);
                await _authSessionLock.WaitAsync(CancellationToken.None);
                try
                {
                    if (ReferenceEquals(_loginReporter, reporter))
                    {
                        _loginReporter = null;
                        if (_loginAttempt == login) _loginAttempt = null;
                    }
                }
                finally
                {
                    _authSessionLock.Release();
                }
            }
        }
    }

    /// <summary>
    /// Logs out: cancels any in-flight login poll, clears saved credentials and in-memory auth state.
    /// No Docker container to terminate (the login was daemon-free).
    /// </summary>
    public async Task LogoutAsync(IntegrationCaller? caller = null)
    {
        await using var release = await _authStorage.BeginIntegrationReleaseAsync(caller);
        _loginReporter?.RequestCancellation();
        await _authSessionLock.WaitAsync();
        try
        {
            _authStorage.CompleteIntegrationRelease(release);
            SetIsAuthenticated(false);
            _displayName = null;
            _lastCollectionUtc = null;
            _gamesDiscovered = 0;
            _xuid = null;
        }
        finally { _authSessionLock.Release(); }
    }

    /// <summary>
    /// Cancels an in-flight device-code login poll WITHOUT touching saved credentials or the authenticated
    /// session - safe to call when the user closes the login modal. An account that was already signed in
    /// stays signed in; only a pending (not-yet-approved) poll is stopped, which then emits a terminal
    /// "cancelled" event. (Distinct from <see cref="LogoutAsync"/>, which also clears credentials.)
    /// </summary>
    public void CancelLogin(IntegrationCaller caller, Guid? attemptId)
        => _authStorage.CancelIntegrationLogin(caller, attemptId, () => _loginReporter?.RequestCancellation());

    public void CancelLogin()
    {
        var login = _loginAttempt;
        if (login is not null) _authStorage.FinishIntegrationLogin(login, () => _loginReporter?.RequestCancellation());
    }

    /// <summary>
    /// Startup auto-reconnect: silently refreshes the saved MSA refresh token (no browser, no daemon),
    /// rotates the stored token, and marks the session authenticated using the cached catalog counts.
    /// The actual catalog harvest happens on the next scheduled/manual refresh (mirrors Epic).
    /// </summary>
    private async Task TryAutoReconnectAsync()
    {
        var ct = _shutdownCts.Token;
        await _authSessionLock.WaitAsync(ct);
        var snapshot = _authStorage.GetIntegrationSnapshot();
        try
        {
            if (string.IsNullOrEmpty(snapshot.Auth.RefreshToken) || !_authStorage.IsIntegrationCurrent(snapshot.Version)) return;
            try
            {
                var msaToken = await _authClient.RefreshAccessTokenAsync(snapshot.Auth.RefreshToken, ct);
                ct.ThrowIfCancellationRequested();
                _authStorage.TryUpdateAuth(snapshot.Version, auth =>
                {
                    if (!string.IsNullOrEmpty(msaToken.RefreshToken)) auth.RefreshToken = msaToken.RefreshToken;
                    auth.LastAuthenticated = DateTime.UtcNow;
                }, () =>
                {
                    SetIsAuthenticated(true);
                    _displayName = snapshot.Auth.DisplayName;
                    _xuid = snapshot.Auth.Xuid;
                    _gamesDiscovered = snapshot.Auth.GamesDiscovered;
                    _lastCollectionUtc = snapshot.Auth.LastAuthenticated;
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (ValidationException ex)
            {
                _logger.LogWarning(ex, "Xbox refresh token was rejected");
                _authStorage.TryInvalidateAuth(snapshot.Version, () =>
                {
                    SetIsAuthenticated(false);
                    _displayName = null;
                    _gamesDiscovered = 0;
                    _xuid = null;
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to refresh Xbox mapping session");
                _authStorage.RunIfCurrent(snapshot.Version, () => SetIsAuthenticated(false));
            }
        }
        finally { _authSessionLock.Release(); }
    }

    private async Task<int> HarvestManagerCatalogAsync(CancellationToken ct)
    {
        var snapshot = _authStorage.GetIntegrationSnapshot();
        var version = snapshot.Version;
        var credentials = snapshot.Auth;
        if (string.IsNullOrEmpty(credentials.RefreshToken) || string.IsNullOrEmpty(credentials.DeviceKeyPkcs8)
            || !_authStorage.IsIntegrationCurrent(version)) return 0;
        XblRequestSigner? signer = null;
        try
        {
            var msaToken = await _authClient.RefreshAccessTokenAsync(credentials.RefreshToken, ct);
            ct.ThrowIfCancellationRequested();
            var updated = _authStorage.TryUpdateAuth(version, auth =>
            {
                if (!string.IsNullOrEmpty(msaToken.RefreshToken)) auth.RefreshToken = msaToken.RefreshToken;
            });
            if (updated is null) return 0;
            version = updated.Value;
            signer = XblRequestSigner.FromPkcs8Base64(credentials.DeviceKeyPkcs8);
            var harvest = await _authClient.HarvestCatalogAsync(msaToken.AccessToken!, signer, ct);
            ct.ThrowIfCancellationRequested();
            if (!_authStorage.IsIntegrationCurrent(version)) return 0;
            var newPatterns = harvest.CdnInfos.Count > 0
                ? await _mappingService.MergeDaemonCatalogAsync(harvest.CdnInfos, ct)
                : 0;
            await _authSessionLock.WaitAsync(ct);
            try
            {
                _authStorage.TryUpdateAuth(version, auth =>
                {
                    auth.LastAuthenticated = DateTime.UtcNow;
                    auth.GamesDiscovered = harvest.CdnInfos.Count;
                    if (!string.IsNullOrEmpty(harvest.DisplayName)) auth.DisplayName = harvest.DisplayName;
                    if (!string.IsNullOrEmpty(harvest.Xuid)) auth.Xuid = harvest.Xuid;
                }, () =>
                {
                    SetIsAuthenticated(true);
                    if (!string.IsNullOrEmpty(harvest.DisplayName)) _displayName = harvest.DisplayName;
                    if (!string.IsNullOrEmpty(harvest.Xuid)) _xuid = harvest.Xuid;
                    _gamesDiscovered = harvest.CdnInfos.Count;
                    _lastCollectionUtc = DateTime.UtcNow;
                });
            }
            finally { _authSessionLock.Release(); }
            return newPatterns;
        }
        catch (OperationCanceledException) { throw; }
        catch (ValidationException ex)
        {
            _logger.LogWarning(ex, "Xbox refresh token was rejected");
            _authStorage.TryInvalidateAuth(version, () => SetIsAuthenticated(false));
            return 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Xbox manager-side catalog harvest failed");
            return 0;
        }
        finally { signer?.Dispose(); }
    }

    private async Task EmitAuthStateAsync(
        Guid operationId,
        OperationStatus status,
        string stageKey,
        string? message = null,
        string? error = null,
        Dictionary<string, object?>? context = null)
    {
        try
        {
            await _notifications.NotifyAllAsync(
                SignalREvents.XboxMappingAuthStateChanged,
                new XboxMappingAuthStateChanged(
                    operationId,
                    status,
                    stageKey,
                    message,
                    error,
                    context));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit Xbox auth state ({StageKey})", stageKey);
        }
    }
}
