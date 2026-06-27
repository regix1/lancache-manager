using LancacheManager.Hubs;
using LancacheManager.Models;

namespace LancacheManager.Services.Xbox;

/// <summary>
/// Manager-side, daemon-free Xbox MSA device-code login partial for
/// <see cref="XboxCatalogMappingService"/>. Mirrors
/// <c>EpicMappingService.Authentication.cs</c> in shape (auth state + login + logout +
/// startup auto-reconnect, feeding the EXISTING <c>MergeDaemonCatalogAsync</c> + <c>ResolveDownloadsAsync</c>),
/// but adapted for the MSA device-code grant: the backend POLLS the token endpoint in the background
/// instead of accepting a pasted code, so <see cref="StartLoginAsync"/> returns a device-code challenge
/// and completion is surfaced over the <c>XboxMappingProgress</c> SignalR event.
/// </summary>
public partial class XboxCatalogMappingService
{
    // Auth state (shared across the partial via the single class instance).
    private bool _isAuthenticated;
    private string? _displayName;
    private DateTime? _lastCollectionUtc;
    private int _gamesDiscovered;
    private string? _xuid;

    // Serializes auth-state mutations so a completing login and a logout cannot interleave.
    private readonly SemaphoreSlim _authSessionLock = new(1, 1);
    // Serializes the login-start sequence so two near-simultaneous clicks can't both register a poll
    // CTS; an abandoned prior login is superseded inside, never blocked (single admin, last-writer-wins).
    private readonly SemaphoreSlim _loginStartLock = new(1, 1);
    private CancellationTokenSource? _loginPollCts;

    /// <summary>True once a saved/just-completed MSA session is active. Drives the auth-status surface.</summary>
    public bool IsAuthenticated => _isAuthenticated;
    public string? DisplayName => _displayName;
    public DateTime? LastCollectionUtc => _lastCollectionUtc;
    public int GamesDiscovered => _gamesDiscovered;

    /// <summary>The authenticated account's Xbox user id (xuid), captured for diagnostics.</summary>
    public string? Xuid => _xuid;

    /// <summary>Returns the current auth snapshot for the REST <c>auth-status</c> endpoint.</summary>
    public XboxMappingAuthStatus GetAuthStatus()
    {
        return new XboxMappingAuthStatus
        {
            IsAuthenticated = _isAuthenticated,
            DisplayName = _displayName,
            LastCollectionUtc = _lastCollectionUtc,
            GamesDiscovered = _gamesDiscovered
        };
    }

    /// <summary>
    /// Starts the device-code login: requests a device code from MSA, kicks a background poll loop, and
    /// returns the <c>userCode</c>/<c>verificationUri</c> for the user to approve in their own browser.
    /// No Docker container and no prefill daemon are involved. Completion (success/failure/cancel) is
    /// emitted over <see cref="SignalREvents.XboxMappingProgress"/>; the frontend re-fetches auth-status.
    /// </summary>
    public async Task<XboxDeviceCodeChallenge> StartLoginAsync(CancellationToken ct = default)
    {
        // Single admin, last-writer-wins: a prior login that was abandoned (modal closed without
        // approving) is SUPERSEDED here rather than blocking this one, so re-clicking Login always works
        // and never 409s. The short lock only guards two truly-simultaneous starts from racing to
        // register their poll CTS - it does not block an abandoned-then-retry.
        await _loginStartLock.WaitAsync(ct);
        try
        {
            // Cancel any stale in-flight login poll before starting a fresh one. The old poll loop observes
            // the cancellation, emits a terminal "cancelled" event, and disposes its own CTS in its finally.
            try
            {
                _loginPollCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Old poll already finished.
            }

            // Register the login CTS BEFORE the device-code request and link it to shutdown, so a logout
            // (or host shutdown) can cancel the flow even during RequestDeviceCodeAsync, and so the entire
            // background poll - which outlives this HTTP request - is cancelled when the host stops. The poll
            // is deliberately NOT tied to the request token: it must keep running after the POST returns.
            var pollCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            _loginPollCts = pollCts;

            try
            {
                // The device-code request itself is also cancellable by the HTTP request that triggered it,
                // via a short-lived linked source that does not affect the long-lived poll CTS.
                XboxDeviceCodeResponse deviceCode;
                using (var requestCts = CancellationTokenSource.CreateLinkedTokenSource(pollCts.Token, ct))
                {
                    deviceCode = await _authClient.RequestDeviceCodeAsync(requestCts.Token);
                }

                // Restore the stable device identity from storage, or create a fresh one (persisted on
                // success so the signed device token keeps validating across restarts).
                var authData = _authStorage.GetAuthData();
                var signer = !string.IsNullOrEmpty(authData.DeviceKeyPkcs8)
                    ? XblRequestSigner.FromPkcs8Base64(authData.DeviceKeyPkcs8)
                    : XblRequestSigner.CreateNew();

                var operationId = Guid.NewGuid();

                // Device-code grant: the BACKEND polls. Fire-and-forget the poll loop; it disposes pollCts
                // and emits a terminal XboxMappingProgress event when it finishes.
                _ = Task.Run(() => RunLoginPollAsync(deviceCode, signer, operationId, pollCts), CancellationToken.None);

                return new XboxDeviceCodeChallenge
                {
                    UserCode = deviceCode.UserCode ?? string.Empty,
                    VerificationUri = deviceCode.VerificationUri ?? string.Empty,
                    ExpiresIn = deviceCode.ExpiresIn,
                    Interval = deviceCode.Interval,
                    OperationId = operationId
                };
            }
            catch
            {
                // The poll loop never started, so dispose the CTS here so the user can retry.
                if (ReferenceEquals(_loginPollCts, pollCts))
                {
                    _loginPollCts = null;
                }
                pollCts.Dispose();
                throw;
            }
        }
        finally
        {
            _loginStartLock.Release();
        }
    }

    /// <summary>
    /// Background poll loop for a started device-code login. On approval it runs the full token chain +
    /// catalog harvest, merges into the shared catalog, resolves downloads, persists credentials, and
    /// emits a terminal <c>XboxMappingProgress</c> event.
    /// </summary>
    private async Task RunLoginPollAsync(
        XboxDeviceCodeResponse deviceCode, XblRequestSigner signer, Guid operationId, CancellationTokenSource pollCts)
    {
        var ct = pollCts.Token;
        try
        {
            await EmitProgressAsync(operationId, "signalr.xbox.mapping.authenticating", 10, "Waiting for Microsoft sign-in...");

            var msaToken = await _authClient.PollForTokenAsync(deviceCode, ct);

            await EmitProgressAsync(operationId, "signalr.xbox.mapping.collecting", 40, "Collecting Xbox library...");

            var harvest = await _authClient.HarvestCatalogAsync(msaToken.AccessToken!, signer, ct);

            // Reuse the existing producer + resolver - mapping/banner/detection logic is unchanged.
            if (harvest.CdnInfos.Count > 0)
            {
                await _mappingService.MergeDaemonCatalogAsync(harvest.CdnInfos, ct);
            }
            var resolved = await _mappingService.ResolveDownloadsAsync(ct);
            _logger.LogInformation("Xbox mapping login resolved {Resolved} existing download(s)", resolved);

            await _authSessionLock.WaitAsync(CancellationToken.None);
            try
            {
                // A concurrent logout cancels this login's CTS and clears credentials while holding the
                // same lock. Re-check under the lock so we never persist or keep credentials a logout just
                // cleared (which would leave the session in-memory-authenticated with no stored creds).
                ct.ThrowIfCancellationRequested();

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

                _isAuthenticated = true;
                _displayName = harvest.DisplayName;
                _xuid = harvest.Xuid;
                _gamesDiscovered = harvest.CdnInfos.Count;
                _lastCollectionUtc = DateTime.UtcNow;
            }
            finally
            {
                _authSessionLock.Release();
            }

            await EmitTerminalAsync(operationId, success: true, cancelled: false,
                "signalr.xbox.mapping.completed", $"Xbox login complete - {harvest.CdnInfos.Count} games",
                _gamesDiscovered, error: null);

            _logger.LogInformation("Xbox mapping login complete: {DisplayName}, {Games} games",
                harvest.DisplayName, harvest.CdnInfos.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Xbox mapping login cancelled");
            await EmitTerminalAsync(operationId, success: false, cancelled: true,
                "signalr.xbox.mapping.cancelled", "Xbox login cancelled", _gamesDiscovered, error: null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Xbox mapping login failed");
            await EmitTerminalAsync(operationId, success: false, cancelled: false,
                "signalr.xbox.mapping.failed", "Xbox login failed", _gamesDiscovered, error: ex.Message);
        }
        finally
        {
            signer.Dispose();
            if (ReferenceEquals(_loginPollCts, pollCts))
            {
                _loginPollCts = null;
            }
            pollCts.Dispose();
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
            _loginPollCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Poll loop already finished.
        }

        await _authSessionLock.WaitAsync();
        try
        {
            _authStorage.ClearAuthData();

            _isAuthenticated = false;
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
            _loginPollCts?.Cancel();
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

                _isAuthenticated = true;
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

                _isAuthenticated = false;
                _displayName = null;
                _gamesDiscovered = 0;
                _xuid = null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-reconnect Xbox mapping session");
            _isAuthenticated = false;
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
                _isAuthenticated = true;
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

    // Best-effort: the poll loop is fire-and-forget, so a SignalR send failure must never fault the
    // unobserved task (especially when emitted from a catch block). Swallow and log instead.
    private async Task EmitProgressAsync(Guid operationId, string stageKey, double percentComplete, string message)
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.XboxMappingProgress, new
            {
                operationId,
                success = false,
                // OperationStatus serializes lowercase ("running"/"completed"/"failed"/"cancelled") via its
                // type-level converter, matching the frontend XboxMappingProgressEvent.status contract.
                status = OperationStatus.Running,
                stageKey,
                percentComplete,
                gamesDiscovered = _gamesDiscovered,
                cancelled = false,
                error = (string?)null,
                message,
                isTerminal = false
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit Xbox mapping progress ({StageKey})", stageKey);
        }
    }

    private async Task EmitTerminalAsync(
        Guid operationId, bool success, bool cancelled, string stageKey, string message, int gamesDiscovered, string? error)
    {
        try
        {
            await _notifications.NotifyAllAsync(SignalREvents.XboxMappingProgress, new
            {
                operationId,
                success,
                // Mirror Epic: a cancel is emitted as Completed (with cancelled:true), NOT a raw
                // Cancelled status. The frontend status-aware notification handler only treats
                // completed/failed as terminal, so a "cancelled" status would never auto-dismiss and
                // the "Xbox login cancelled" card would stick forever. The cancelled flag still drives
                // the cancelled message + dismiss behavior.
                status = success || cancelled
                    ? OperationStatus.Completed
                    : OperationStatus.Failed,
                stageKey,
                percentComplete = success || cancelled ? 100.0 : 0.0,
                gamesDiscovered,
                cancelled,
                error,
                message,
                isTerminal = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to emit terminal Xbox mapping progress ({StageKey})", stageKey);
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
