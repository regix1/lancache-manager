using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using static LancacheManager.Infrastructure.Utilities.SignalRNotifications;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Manager-side, daemon-free Xbox MSA device-code login partial for
/// <see cref="XboxCatalogMappingService"/>. Mirrors
/// <c>EpicMappingService.Authentication.cs</c> in shape (auth state + login + logout +
/// startup auto-reconnect, feeding the EXISTING <c>MergeDaemonCatalogAsync</c> + <c>ResolveDownloadsAsync</c>),
/// but adapted for the MSA device-code grant: the backend POLLS the token endpoint in the background
/// instead of accepting a pasted code, so <see cref="StartLoginAsync"/> returns a device-code challenge
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
    // Serializes the login-start sequence so two near-simultaneous clicks can't both register a
    // reporter; an abandoned prior login is superseded inside, never blocked (single admin,
    // last-writer-wins).
    private readonly SemaphoreSlim _loginStartLock = new(1, 1);

    // The in-flight sign-in's tracked operation, so a logout and the modal's cancel can stop it. Held
    // apart from _currentMappingReporter because a scheduled refresh writes that field too
    // (Scheduling.cs:50), and cancelling a sign-in must never stop a catalog refresh. Non-null for the
    // WHOLE attempt (approval wait plus the catalog stretch after it), which is what GetAuthStatus
    // reports. Volatile because the poll task clears it and a request thread reads it.
    private volatile MappingOperationReporter? _loginReporter;

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
    public XboxMappingAuthStatus GetAuthStatus()
    {
        return new XboxMappingAuthStatus
        {
            IsAuthenticated = _isAuthenticated,
            DisplayName = _displayName,
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
    public async Task<XboxDeviceCodeChallenge> StartLoginAsync(CancellationToken ct = default)
    {
        // Single admin, last-writer-wins: a prior login that was abandoned (modal closed without
        // approving) is SUPERSEDED here rather than blocking this one, so re-clicking Login always works
        // and never 409s. The short lock only guards two truly-simultaneous starts from racing to
        // register their reporter - it does not block an abandoned-then-retry.
        await _loginStartLock.WaitAsync(ct);
        try
        {
            // Cancel any stale in-flight login poll before starting a fresh one. The old poll loop observes
            // the cancellation, emits a terminal "cancelled" event, and disposes its own reporter in its
            // finally.
            try
            {
                _loginReporter?.RequestCancellation();
            }
            catch (ObjectDisposedException)
            {
                // Old poll already finished.
            }

            // Build the login's reporter BEFORE the device-code request so ONE tracked operation covers the
            // whole sign-in, and link its token to shutdown, so a logout (or host shutdown) can cancel the
            // flow even during RequestDeviceCodeAsync, and so the entire background poll - which outlives
            // this HTTP request - is cancelled when the host stops. The poll is deliberately NOT tied to the
            // request token: it must keep running after the POST returns. The operation is registered at the
            // top of the poll instead of here, so it never exists outside _refreshGate.
            _refreshShowNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);
            var reporter = new MappingOperationReporter(
                _notifications,
                _operationTracker,
                MappingOperations.Xbox,
                _refreshShowNotification,
                _shutdownCts.Token,
                _logger);
            _loginReporter = reporter;

            try
            {
                // The device-code request itself is also cancellable by the HTTP request that triggered it,
                // via a short-lived linked source that does not affect the long-lived login token.
                XboxDeviceCodeResponse deviceCode;
                using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(reporter.Token, ct))
                {
                    deviceCode = await _authClient.RequestDeviceCodeAsync(requestCts.Token);
                }

                // Restore the stable device identity from storage, or create a fresh one (persisted on
                // success so the signed device token keeps validating across restarts).
                var authData = _authStorage.GetAuthData();
                var signer = !string.IsNullOrEmpty(authData.DeviceKeyPkcs8)
                    ? XblRequestSigner.FromPkcs8Base64(authData.DeviceKeyPkcs8)
                    : XblRequestSigner.CreateNew();

                // Device-code grant: the BACKEND polls. Fire-and-forget the poll loop; it registers the
                // tracked operation and emits auth-state, and disposes the reporter when it finishes.
                _ = Task.Run(() => RunLoginPollAsync(deviceCode, signer, reporter), CancellationToken.None);

                return new XboxDeviceCodeChallenge
                {
                    UserCode = deviceCode.UserCode ?? string.Empty,
                    VerificationUri = deviceCode.VerificationUri ?? string.Empty,
                    ExpiresIn = deviceCode.ExpiresIn,
                    Interval = deviceCode.Interval
                };
            }
            catch
            {
                // The poll loop never started, so dispose the reporter here so the user can retry.
                if (ReferenceEquals(_loginReporter, reporter))
                {
                    _loginReporter = null;
                }
                await reporter.DisposeAsync();
                throw;
            }
        }
        finally
        {
            _loginStartLock.Release();
        }
    }

    /// <summary>
    /// Background poll loop for a started device-code login. It holds the catalog-mapping gate and the
    /// login's tracked operation for the whole wait; on approval it runs the full token chain + catalog
    /// harvest, merges into the shared catalog, resolves downloads, persists credentials, and emits a
    /// terminal auth-state event.
    /// </summary>
    private async Task RunLoginPollAsync(
        XboxDeviceCodeResponse deviceCode, XblRequestSigner signer, MappingOperationReporter reporter)
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

            _currentMappingReporter = reporter;

            // The card the user watches while approving is the one this started event creates, and it
            // shows the started stage key, so that key is the waiting one rather than the generic
            // starting one. The reporter's own progress events take the message over after approval.
            await reporter.StartAsync(CreateXboxMappingContext(), XboxAwaitingSignInStageKey);

            XboxMsaTokenResponse msaToken;
            _awaitingSignIn = true;
            try
            {
                await EmitAuthStateAsync(
                    reporter.OperationId,
                    "waiting",
                    XboxAwaitingSignInStageKey,
                    "Waiting for Microsoft sign-in...");

                msaToken = await _authClient.PollForTokenAsync(deviceCode, reporter.Token);
            }
            finally
            {
                _awaitingSignIn = false;
            }

            await reporter.ReportAsync(
                25,
                "signalr.xboxMapping.collecting",
                CreateXboxMappingContext());

            var harvest = await _authClient.HarvestCatalogAsync(
                msaToken.AccessToken!,
                signer,
                reporter.Token);

            if (harvest.CdnInfos.Count > 0)
            {
                await _mappingService.MergeDaemonCatalogAsync(harvest.CdnInfos, reporter.Token);
            }
            _gamesDiscovered = harvest.CdnInfos.Count;
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
                reporter.Token.ThrowIfCancellationRequested();

                // Persist credentials (refresh token + device key) for auto-reconnect, atomically with the
                // in-memory state under the lock so logout and login-success are mutually exclusive.
                _authStorage.SaveAuthData(new XboxAuthData
                {
                    RefreshToken = msaToken.RefreshToken,
                    DeviceKeyPkcs8 = signer.ExportPkcs8Base64(),
                    DisplayName = harvest.DisplayName,
                    Xuid = harvest.Xuid,
                    LastAuthenticated = DateTime.UtcNow,
                    GamesDiscovered = harvest.CdnInfos.Count
                });

                SetIsAuthenticated(true);
                _displayName = harvest.DisplayName;
                _xuid = harvest.Xuid;
                _gamesDiscovered = harvest.CdnInfos.Count;
                _lastCollectionUtc = DateTime.UtcNow;
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
                "completed",
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
                "cancelled",
                "signalr.xbox.mapping.cancelled",
                "Xbox login cancelled");
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
                "failed",
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
                if (ReferenceEquals(_loginReporter, reporter))
                {
                    _loginReporter = null;
                }
            }
        }
    }

    /// <summary>
    /// Logs out: cancels any in-flight login poll, clears saved credentials and in-memory auth state.
    /// No Docker container to terminate (the login was daemon-free).
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            _loginReporter?.RequestCancellation();
        }
        catch (ObjectDisposedException)
        {
            // Poll loop already finished.
        }

        await _authSessionLock.WaitAsync();
        try
        {
            _authStorage.ClearAuthData();

            SetIsAuthenticated(false);
            _displayName = null;
            _lastCollectionUtc = null;
            _gamesDiscovered = 0;
            _xuid = null;

            _logger.LogInformation("Xbox mapping session logged out and credentials cleared");
        }
        finally
        {
            _authSessionLock.Release();
        }
    }

    /// <summary>
    /// Cancels an in-flight device-code login poll WITHOUT touching saved credentials or the authenticated
    /// session - safe to call when the user closes the login modal. An account that was already signed in
    /// stays signed in; only a pending (not-yet-approved) poll is stopped, which then emits a terminal
    /// "cancelled" event. (Distinct from <see cref="LogoutAsync"/>, which also clears credentials.)
    /// </summary>
    public void CancelLogin()
    {
        try
        {
            _loginReporter?.RequestCancellation();
        }
        catch (ObjectDisposedException)
        {
            // Poll already finished.
        }
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
        try
        {
            var authData = _authStorage.GetAuthData();
            if (string.IsNullOrEmpty(authData.RefreshToken))
            {
                _logger.LogInformation("No saved Xbox refresh token, skipping auto-reconnect");
                return;
            }

            _logger.LogInformation("Attempting Xbox mapping auto-reconnect with saved refresh token...");

            try
            {
                var msaToken = await _authClient.RefreshAccessTokenAsync(authData.RefreshToken, ct);

                // Rotate the refresh token if MSA returned a new one.
                _authStorage.UpdateAuthData(d =>
                {
                    if (!string.IsNullOrEmpty(msaToken.RefreshToken))
                    {
                        d.RefreshToken = msaToken.RefreshToken;
                    }
                    d.LastAuthenticated = DateTime.UtcNow;
                });

                SetIsAuthenticated(true);
                _displayName = authData.DisplayName;
                _xuid = authData.Xuid;
                _gamesDiscovered = authData.GamesDiscovered;
                _lastCollectionUtc = authData.LastAuthenticated;

                _logger.LogInformation("Xbox auto-reconnect authenticated: {DisplayName}, {Games} cached games",
                    authData.DisplayName, authData.GamesDiscovered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Xbox refresh token expired or invalid, clearing credentials");
                _authStorage.ClearAuthData();

                SetIsAuthenticated(false);
                _displayName = null;
                _gamesDiscovered = 0;
                _xuid = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-reconnect Xbox mapping session");
            SetIsAuthenticated(false);
        }
        finally
        {
            _authSessionLock.Release();
        }
    }

    /// <summary>
    /// Harvests the catalog from the manager-side authenticated session for a scheduled/manual refresh:
    /// refreshes the MSA access token, re-mints the XSTS chain with the stored device key, and merges the
    /// titlehub + packagespc fragments. Returns the number of CDN patterns newly persisted. Best-effort:
    /// a failure here never breaks the daemon source or the resolver pass.
    /// </summary>
    private async Task<int> HarvestManagerCatalogAsync(CancellationToken ct)
    {
        var authData = _authStorage.GetAuthData();
        if (string.IsNullOrEmpty(authData.RefreshToken) || string.IsNullOrEmpty(authData.DeviceKeyPkcs8))
        {
            return 0;
        }

        XblRequestSigner? signer = null;
        try
        {
            var msaToken = await _authClient.RefreshAccessTokenAsync(authData.RefreshToken, ct);
            _authStorage.UpdateAuthData(d =>
            {
                if (!string.IsNullOrEmpty(msaToken.RefreshToken))
                {
                    d.RefreshToken = msaToken.RefreshToken;
                }
            });

            signer = XblRequestSigner.FromPkcs8Base64(authData.DeviceKeyPkcs8);
            var harvest = await _authClient.HarvestCatalogAsync(msaToken.AccessToken!, signer, ct);

            var newPatterns = harvest.CdnInfos.Count > 0
                ? await _mappingService.MergeDaemonCatalogAsync(harvest.CdnInfos, ct)
                : 0;

            await _authSessionLock.WaitAsync(ct);
            try
            {
                SetIsAuthenticated(true);
                if (!string.IsNullOrEmpty(harvest.DisplayName))
                {
                    _displayName = harvest.DisplayName;
                }
                if (!string.IsNullOrEmpty(harvest.Xuid))
                {
                    _xuid = harvest.Xuid;
                }
                _gamesDiscovered = harvest.CdnInfos.Count;
                _lastCollectionUtc = DateTime.UtcNow;
            }
            finally
            {
                _authSessionLock.Release();
            }

            _authStorage.UpdateAuthData(d =>
            {
                d.LastAuthenticated = DateTime.UtcNow;
                d.GamesDiscovered = harvest.CdnInfos.Count;
                if (!string.IsNullOrEmpty(harvest.DisplayName))
                {
                    d.DisplayName = harvest.DisplayName;
                }
            });

            return newPatterns;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Xbox manager-side catalog harvest failed");
            return 0;
        }
        finally
        {
            signer?.Dispose();
        }
    }

    private async Task EmitAuthStateAsync(
        Guid operationId,
        string status,
        string stageKey,
        string? message = null,
        string? error = null)
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
                    error));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit Xbox auth state ({StageKey})", stageKey);
        }
    }
}

/// <summary>
/// Auth-status response for the REST <c>auth-status</c> endpoint. Mirrors <c>EpicMappingAuthStatus</c>.
/// Serialized camelCase over REST + SignalR -&gt; the frontend sees
/// <c>isAuthenticated/displayName/lastCollectionUtc/gamesDiscovered</c>.
/// </summary>
public class XboxMappingAuthStatus
{
    public bool IsAuthenticated { get; set; }
    public string? DisplayName { get; set; }
    public DateTime? LastCollectionUtc { get; set; }
    public int GamesDiscovered { get; set; }
    /// <summary>
    /// True while a device-code login attempt is still alive - the approval wait AND the catalog
    /// harvest that follows approval, unlike <c>AwaitingSignIn</c> which covers only the wait. False
    /// together with <see cref="IsAuthenticated"/> false is the only pair that means the attempt is
    /// over and did not succeed, so a client that lost the completion event can tell a dead login from
    /// a busy one instead of waiting for a message that will never arrive.
    /// </summary>
    public bool LoginInProgress { get; set; }
    /// <summary>
    /// Approximate expiry of the MSA refresh token: last-auth time + ~90 days.
    /// Slides forward on each auto-renew (startup reconnect and the 12h schedule).
    /// Null when not authenticated.
    /// </summary>
    public DateTime? ExpiresAtUtc { get; set; }
}

/// <summary>
/// Device-code challenge returned by <c>POST auth/login</c>. The frontend renders
/// <see cref="UserCode"/> + <see cref="VerificationUri"/> for the user; completion arrives via SignalR.
/// </summary>
public class XboxDeviceCodeChallenge
{
    public string UserCode { get; set; } = string.Empty;
    public string VerificationUri { get; set; } = string.Empty;
    public int ExpiresIn { get; set; }
    public int Interval { get; set; }
    public Guid OperationId { get; set; }
}
