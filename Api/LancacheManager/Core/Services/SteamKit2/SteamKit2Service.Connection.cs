using LancacheManager.Hubs;
using LancacheManager.Models;
using SteamKit2;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Check if the Steam Daemon is currently active/authenticated.
    /// Used to determine whether to use anonymous mode to avoid session conflicts.
    /// </summary>
    private bool IsSteamDaemonActive()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var daemonService = scope.ServiceProvider.GetService<SteamDaemonService>();
            return daemonService?.IsAnyDaemonAuthenticated() == true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check Steam daemon status");
            return false;
        }
    }

    /// <summary>
    /// Ensures the shared SteamKit2 session is connected and logged on, using the mode picked by
    /// Login() (daemon-active anonymous / saved token / anonymous). This is THE entry point for
    /// every flow that needs a session: all transitions are serialized by _sessionGate and
    /// transient CM failures are retried on a different server. On final failure the
    /// SteamSessionError toast is emitted here (not in the callbacks) and the exception
    /// propagates to the caller, whose own operation lifecycle reports the failure.
    /// </summary>
    private async Task EnsureSessionAsync(CancellationToken ct, bool forceReconnect = false)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            if (!forceReconnect && _isLoggedOn && _steamClient?.IsConnected == true)
            {
                return;
            }

            if (forceReconnect)
            {
                // The caller saw the CM drop its job while the socket stayed up. The session
                // looks healthy but the server is bad - rotate instead of reusing it.
                await ResetConnectionLockedAsync(ct);
            }

            await LogonLockedAsync(details: null, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastErrorMessage = ex.Message;
            NotifySessionError(ex);
            throw;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// The session engine: connect if needed, fire the logon, await the result, and rotate to a
    /// different CM server on transient failures (bounded by MaxLogonAttempts). Must be called
    /// while holding _sessionGate. When details is null, Login() picks the mode; otherwise the
    /// explicit LogOnDetails are used (interactive auth logging on with a fresh refresh token).
    /// A disconnect that lands mid-handshake faults the pending wait with
    /// SteamConnectionLostException, which is just another transient failure here.
    /// </summary>
    private async Task LogonLockedAsync(SteamUser.LogOnDetails? details, CancellationToken ct, TimeSpan? logonTimeout = null)
    {
        await RetryOnBusyCmLockedAsync(async () =>
        {
            if (_steamClient?.IsConnected != true)
            {
                _connectedTcs = new TaskCompletionSource();
                _logger.LogInformation("Connecting to Steam...");
                _steamClient!.Connect();
                await WaitWithTimeoutAsync(_connectedTcs.Task, TimeSpan.FromSeconds(60), ct, "Connecting to Steam");
            }

            _loggedOnTcs = new TaskCompletionSource();
            if (details != null)
            {
                _steamUser!.LogOn(details);
                _logger.LogInformation(
                    "SteamKit2 auth-flow login with LoginID: {LoginID} (0x{LoginIDHex:X8}) for user: {Username}",
                    _steamLoginId, _steamLoginId, details.Username);
            }
            else
            {
                Login();
            }

            await WaitWithTimeoutAsync(_loggedOnTcs.Task, logonTimeout ?? TimeSpan.FromSeconds(60), ct, "Logging into Steam");
            return true;
        }, "Steam logon", ct);
    }

    /// <summary>
    /// The single CM-rotation retry policy: runs one session step (logon or credentials poll),
    /// rotating to a different CM server between attempts when the current one fails transiently.
    /// Must be called while holding _sessionGate.
    /// </summary>
    private async Task<T> RetryOnBusyCmLockedAsync<T>(Func<Task<T>> step, string stepName, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await step();
            }
            catch (Exception ex) when (attempt < MaxLogonAttempts && IsTransientSessionFailure(ex))
            {
                _logger.LogWarning(
                    "{Step} hit a busy Steam server (attempt {Attempt}/{MaxAttempts}) - rotating to a different CM server: {Message}",
                    stepName, attempt, MaxLogonAttempts, ex.Message);
                await ResetConnectionLockedAsync(ct);
            }
        }
    }

    /// <summary>
    /// Transient session-step failures a CM rotation can fix: a dropped connection, a dropped
    /// auth job, or a busy-server logon result. Credential/permission rejections are not here.
    /// </summary>
    private static bool IsTransientSessionFailure(Exception ex) =>
        ex is SteamConnectionLostException
            or AsyncJobFailedException
            or SteamLogonException { IsTransient: true };

    /// <summary>
    /// True when an exception from a PICS operation means the Steam session could not be
    /// re-established (RunPicsWithRecoveryAsync exhausted its retries or the re-logon was
    /// rejected). Every subsequent operation would fail the same way, so batch loops must
    /// rethrow these instead of logging-and-continuing.
    /// </summary>
    private static bool IsSessionFatal(Exception ex) =>
        ex is SteamLogonException or SteamConnectionLostException or TimeoutException;

    /// <summary>
    /// Drops the current connection (if any) so the next connect picks a different CM server.
    /// Must be called while holding _sessionGate. After TryAnotherCM the CM usually disconnects
    /// us itself; after a dropped auth job we are still connected to the same bad server.
    /// </summary>
    private async Task ResetConnectionLockedAsync(CancellationToken ct)
    {
        if (_steamClient?.IsConnected == true)
        {
            _intentionalDisconnect = true;
            _steamClient.Disconnect();
        }

        // Give the disconnect a moment to settle before the next attempt reconnects
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    /// <summary>
    /// Emits the SteamSessionError toast for a failed session transition. Called once per
    /// operation at the point we give up - never from the SteamKit2 callbacks, so retries
    /// don't spam the frontend. Timeouts and cancellations surface through the calling
    /// operation's own error path instead (matching the old behavior, which never toasted them).
    /// </summary>
    private void NotifySessionError(Exception ex)
    {
        if (ex is SteamLogonException logonEx)
        {
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
            {
                errorType = logonEx.ErrorType,
                stageKey = logonEx.StageKey,
                context = new Dictionary<string, object?> { ["result"] = logonEx.Result },
                result = logonEx.Result,
                extendedResult = logonEx.ExtendedResult,
                timestamp = DateTime.UtcNow,
                wasRebuildActive = IsRebuildRunning
            });
        }
        else if (ex is SteamConnectionLostException)
        {
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
            {
                errorType = "ConnectionFailed",
                stageKey = "signalr.steamSession.reconnectFailed",
                context = new Dictionary<string, object?> { ["maxAttempts"] = MaxLogonAttempts },
                timestamp = DateTime.UtcNow,
                wasRebuildActive = IsRebuildRunning
            });
        }
    }

    /// <summary>
    /// Determines which login mode to use and fires the appropriate SteamUser logon call.
    /// Synchronous (void) by design - only LogonLockedAsync calls this, and it awaits the logon
    /// result separately via _loggedOnTcs.
    /// </summary>
    private void Login()
    {
        if (IsSteamDaemonActive())
        {
            _logger.LogInformation("Steam daemon is active - using anonymous mode for depot mapping to avoid session conflicts");
            _steamUser!.LogOnAnonymous();
            return;
        }

        var refreshToken = _stateService.GetSteamRefreshToken();
        var authMode = _stateService.GetSteamAuthMode();

        if (!string.IsNullOrEmpty(refreshToken) && authMode == SteamAuthMode.Authenticated)
        {
            var username = _stateService.GetSteamUsername();
            _logger.LogInformation("Logging in with saved refresh token for user: {Username}", username);
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = refreshToken,
                ShouldRememberPassword = true,
                LoginID = _steamLoginId
            });
            _logger.LogInformation("SteamKit2 authenticated login with LoginID: {LoginID} (0x{LoginIDHex:X8}) for user: {Username}", _steamLoginId, _steamLoginId, username);
            return;
        }

        _logger.LogInformation("Logging in anonymously...");
        _steamUser!.LogOnAnonymous();
        _logger.LogInformation("SteamKit2 anonymous login (no LoginID)");
    }

    private async Task WaitWithTimeoutAsync(Task task, TimeSpan timeout, CancellationToken ct, string operationName = "Steam operation")
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

        if (completedTask != task)
        {
            // The caller cancelled (request aborted / rebuild cancelled) - report that, not a timeout
            ct.ThrowIfCancellationRequested();

            var errorMessage = $"{operationName} timed out after {timeout.TotalSeconds} seconds. This may indicate Steam servers are busy or your network connection is unstable.";
            _logger.LogWarning("{Operation} timed out after {Seconds}s", operationName, timeout.TotalSeconds);
            throw new TimeoutException(errorMessage);
        }

        await task; // Rethrow if faulted
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _logger.LogInformation("Connected to Steam");
        _connectedTcs?.TrySetResult();
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback callback)
    {
        // Log as info if intentional, warning if unexpected
        if (_intentionalDisconnect)
        {
            _logger.LogInformation("Disconnected from Steam (intentional)");
            _intentionalDisconnect = false;
        }
        else
        {
            _logger.LogWarning("Disconnected from Steam");
        }
        _isLoggedOn = false;

        // Fault any pending session waits so their owner reacts: LogonLockedAsync treats this as
        // a transient failure and rotates CM servers; a PICS batch re-establishes the session via
        // EnsureSessionAsync and retries. All reconnection lives in those owning flows - this
        // callback never schedules its own Connect().
        FailConnectionTasks(new SteamConnectionLostException("Disconnected from Steam"));
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _isLoggedOn = true;
            _loggedOnTcs?.TrySetResult();
            _logger.LogInformation("Successfully logged onto Steam!");
            _logger.LogInformation("Steam login succeeded. Active LoginID: {LoginID} (0x{LoginIDHex:X8}), IsAuthenticated: {IsAuth}", _steamLoginId, _steamLoginId, IsSteamAuthenticated);
        }
        else
        {
            // Create user-friendly error messages for common login failures
            var (errorType, errorMessage) = callback.Result switch
            {
                EResult.InvalidPassword => (
                    "InvalidCredentials",
                    "Your Steam credentials are invalid or have expired. Please re-authenticate with your Steam account."
                ),
                EResult.AccountLogonDenied => (
                    "AuthenticationRequired",
                    "Steam Guard authentication is required. Please re-authenticate with your Steam account."
                ),
                EResult.TryAnotherCM => (
                    "ServerUnavailable",
                    "Steam's servers are busy right now. This is temporary, please wait a moment and try again."
                ),
                EResult.ServiceUnavailable => (
                    "ServiceUnavailable",
                    "Steam service is temporarily unavailable. Please try again later."
                ),
                EResult.RateLimitExceeded => (
                    "RateLimited",
                    "Too many login attempts. Please wait a few minutes before trying again."
                ),
                EResult.Expired => (
                    "SessionExpired",
                    "Your Steam session has expired. Please re-authenticate with your Steam account."
                ),
                _ => (
                    "LoginFailed",
                    $"Unable to log into Steam: {callback.Result}. Please try again or re-authenticate."
                )
            };

            // TryAnotherCM/ServiceUnavailable mean this particular CM server can't serve us right
            // now - a reconnect (which rotates to a different server) is the documented remedy.
            // TryAnotherCM keeps the friendly serverBusy stageKey instead of echoing the raw enum.
            var isTransientServerFailure = callback.Result is EResult.TryAnotherCM or EResult.ServiceUnavailable;
            var stageKey = callback.Result == EResult.TryAnotherCM
                ? "signalr.steamSession.serverBusy"
                : "signalr.steamSession.loginFailed";

            _lastErrorMessage = errorMessage;
            _logger.LogError("Unable to logon to Steam: {Result} / {ExtendedResult}", callback.Result, callback.ExtendedResult);

            // Fault the pending logon wait with the friendly per-result message (not the raw
            // enum). The owning flow retries transient failures on a different CM server, emits
            // the SteamSessionError toast if it gives up, and its operation lifecycle (rebuild
            // terminal emit / login modal response) reports the failure - nothing else happens
            // in this callback.
            _loggedOnTcs?.TrySetException(new SteamLogonException(
                errorMessage,
                errorType,
                stageKey,
                callback.Result.ToString(),
                callback.ExtendedResult.ToString(),
                isTransientServerFailure));
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _logger.LogWarning("Logged off of Steam: {Result}", callback.Result);
        _isLoggedOn = false;

        // Handle specific logoff reasons with user-friendly messages
        var (errorType, errorMessage, shouldCancelRebuild, isSessionReplaced) = callback.Result switch
        {
            EResult.LogonSessionReplaced => (
                "SessionReplaced",
                "Steam session was replaced by another application. Your authentication has been switched to anonymous mode. Please try again.",
                true,
                true
            ),
            EResult.LoggedInElsewhere => (
                "LoggedInElsewhere",
                "Steam session was replaced by another application. Your authentication has been switched to anonymous mode. Please try again.",
                true,
                true
            ),
            EResult.AccountLogonDenied => (
                "AuthenticationRequired",
                "Steam authentication is required. Please re-authenticate with your Steam account.",
                true,
                false
            ),
            EResult.InvalidPassword => (
                "InvalidCredentials",
                "Your Steam credentials are no longer valid. Please re-authenticate with your Steam account.",
                true,
                false
            ),
            EResult.Expired => (
                "SessionExpired",
                "Your Steam session has expired. Please re-authenticate with your Steam account.",
                true,
                false
            ),
            _ => (
                "Disconnected",
                $"Disconnected from Steam: {callback.Result}",
                false,
                false
            )
        };

        if (isSessionReplaced)
        {
            _logger.LogWarning("Steam session was replaced by another login. Our LoginID: {LoginID} (0x{LoginIDHex:X8}). Switching to anonymous mode and failing the operation.", _steamLoginId, _steamLoginId);

            // Switch user to anonymous mode and clear stored credentials
            ClearSteamCredentials();

            // Notify frontend to update auth state (so the UI reflects anonymous mode)
            NotifyAutoLogout(errorMessage, errorType);
        }

        // For credential-invalidating errors, clear stored credentials and notify frontend
        if (errorType is "InvalidCredentials" or "AuthenticationRequired" or "SessionExpired")
        {
            _logger.LogWarning("Steam credentials are no longer valid ({ErrorType}). Clearing stored credentials.", errorType);
            ClearSteamCredentials();

            // Notify frontend to update auth state
            NotifyAutoLogout(errorMessage, errorType);
        }

        // Send SignalR notification for significant errors
        // Skip if rebuild is running - DepotMappingComplete will convey the error to avoid duplicate notifications
        if (shouldCancelRebuild)
        {
            _lastErrorMessage = errorMessage;

            if (!IsRebuildRunning)
            {
                _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
                {
                    errorType,
                    stageKey = "signalr.steamSession.disconnected",
                    context = new Dictionary<string, object?> { ["result"] = callback.Result.ToString() },
                    result = callback.Result.ToString(),
                    timestamp = DateTime.UtcNow,
                    wasRebuildActive = false
                });
            }

            // Cancel the rebuild if one is active
            if (IsRebuildRunning)
            {
                _logger.LogError("Steam session error during active rebuild: {ErrorType} - {Message}", errorType, errorMessage);

                // Send a failure completion event
                SendDepotMappingFailure(errorMessage, errorType);

                // Cancel the rebuild
                _currentRebuildCts?.Cancel();
            }
        }
    }

    /// <summary>
    /// Helper method to disconnect from Steam properly
    /// </summary>
    private async Task DisconnectAsync(int delayMs = 1000)
    {
        if (_steamClient?.IsConnected == true)
        {
            _steamUser?.LogOff();
            await Task.Delay(delayMs);
            _steamClient.Disconnect();
        }
    }

}
