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

    private async Task ConnectAndLoginAsync(CancellationToken ct)
    {
        if (_isLoggedOn && _steamClient?.IsConnected == true)
        {
            return;
        }

        await RunWithCmRetryAsync(async () =>
        {
            if (_steamClient?.IsConnected != true)
            {
                _connectedTcs = new TaskCompletionSource();
                _loggedOnTcs = new TaskCompletionSource();

                _logger.LogInformation("Connecting to Steam...");
                _steamClient!.Connect();

                // Wait for connected (increased timeout to handle Steam server delays)
                await WaitWithTimeoutAsync(_connectedTcs.Task, TimeSpan.FromSeconds(60), ct, "Connecting to Steam");
            }
            else
            {
                // Retry pass: ReconnectForCmRetryAsync already reconnected us to a different CM
                _loggedOnTcs = new TaskCompletionSource();
            }

            Login();

            // Wait for logged on (increased timeout to handle Steam server delays)
            await WaitWithTimeoutAsync(_loggedOnTcs.Task, TimeSpan.FromSeconds(60), ct, "Logging into Steam");
            return true;
        }, "Steam login", ct);
    }

    /// <summary>
    /// Determines which login mode to use and fires the appropriate SteamUser logon call.
    /// Synchronous (void) by design - the SteamKit2 reconnect callback (OnConnected) fires this
    /// synchronously, and ConnectAndLoginAsync awaits the logon result separately via _loggedOnTcs.
    /// Never use .Result or .Wait() here; SteamKit2 callbacks must not block the event loop thread.
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
            var errorMessage = $"{operationName} timed out after {timeout.TotalSeconds} seconds. This may indicate Steam servers are busy or your network connection is unstable.";
            _logger.LogWarning("{Operation} timed out after {Seconds}s", operationName, timeout.TotalSeconds);
            throw new TimeoutException(errorMessage);
        }

        await task; // Rethrow if faulted
    }

    private void OnConnected(SteamClient.ConnectedCallback callback)
    {
        _logger.LogInformation("Connected to Steam");
        _reconnectAttempt = 0; // Reset backoff on successful connection
        _connectedTcs?.TrySetResult();

        // If we're reconnecting during an active rebuild, automatically re-login
        if (IsRebuildRunning && !_isLoggedOn)
        {
            _logger.LogInformation("Reconnected during active rebuild - attempting to re-login");

            // Always check if Steam daemon is active during reconnection.
            // Steam often fires OnDisconnected WITHOUT a preceding OnLoggedOff callback,
            // so we can't rely on tracking session replacement flags. Checking the daemon
            // status directly is reliable and avoids the authenticated reconnection loop.
            Login();
        }
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

        // Only reconnect if we're running AND there's an active rebuild
        // This prevents endless reconnection loops after PICS crawls complete.
        // When a CM-rotation retry is in flight it owns reconnection - scheduling a second
        // Connect() here would race it and orphan its TaskCompletionSources.
        if (_isRunning && IsRebuildRunning && !_transientCmRetryActive)
        {
            _reconnectAttempt++;

            // Check if we've exceeded max attempts
            if (_reconnectAttempt > MaxReconnectAttempts)
            {
                var errorMessage = $"Failed to maintain Steam connection after {MaxReconnectAttempts} reconnection attempts. Steam servers may be experiencing issues or your network connection is unstable.";
                _logger.LogError("Max reconnection attempts ({MaxAttempts}) exceeded during rebuild", MaxReconnectAttempts);
                _lastErrorMessage = errorMessage;

                // Fail any pending connection/login tasks
                FailConnectionTasks(new Exception(errorMessage));

                // Cancel the rebuild
                _currentRebuildCts?.Cancel();

                // Send error notification via SignalR
                _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
                {
                    errorType = "ConnectionFailed",
                    stageKey = "signalr.steamSession.reconnectFailed",
                    context = new Dictionary<string, object?> { ["maxAttempts"] = MaxReconnectAttempts },
                    reconnectAttempts = _reconnectAttempt,
                    timestamp = DateTime.UtcNow,
                    wasRebuildActive = true
                });

                // Send failure completion event
                SendDepotMappingFailure(errorMessage, "ConnectionFailed");

                _reconnectAttempt = 0; // Reset for next time
                return;
            }

            // Exponential backoff: 5s, 10s, 20s, 40s, 60s (capped)
            var delaySeconds = Math.Min(5 * (int)Math.Pow(2, _reconnectAttempt - 1), MaxReconnectDelaySeconds);
            _logger.LogInformation("Unexpected disconnection during active rebuild - attempting to reconnect in {Delay} seconds (attempt {Attempt}/{MaxAttempts})...",
                delaySeconds, _reconnectAttempt, MaxReconnectAttempts);

            // Send progress update so UI knows we're reconnecting (fire-and-forget)
            _ = SendDepotMappingProgressAsync(
                $"Reconnecting to Steam (attempt {_reconnectAttempt}/{MaxReconnectAttempts})...",
                $"Connection lost. Reconnecting in {delaySeconds} seconds...",
                isReconnecting: true,
                reconnectAttempt: _reconnectAttempt
            );

            Task.Delay(delaySeconds * 1000, _cancellationTokenSource.Token).ContinueWith(_ =>
            {
                if (_isRunning && IsRebuildRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    // Create new TaskCompletionSources for the reconnection attempt
                    _connectedTcs = new TaskCompletionSource();
                    _loggedOnTcs = new TaskCompletionSource();
                    _steamClient?.Connect();
                }
            }, _cancellationTokenSource.Token);
        }
        else
        {
            // Fail pending tasks if not reconnecting
            if (!_connectedTcs?.Task.IsCompleted ?? false)
            {
                FailConnectionTasks(new Exception("Disconnected from Steam"));
            }
            _reconnectAttempt = 0; // Reset when not actively rebuilding
        }
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
            var isTransientServerFailure = callback.Result is EResult.TryAnotherCM or EResult.ServiceUnavailable;

            _lastErrorMessage = errorMessage;
            // Surface the friendly per-result message (not the raw enum) to the interactive login
            // modal, which returns this text via AuthenticateAsync. Raw result is logged just below.
            _loggedOnTcs?.TrySetException(isTransientServerFailure
                ? new SteamTransientLoginException(errorMessage)
                : new Exception(errorMessage));
            _logger.LogError("Unable to logon to Steam: {Result} / {ExtendedResult}", callback.Result, callback.ExtendedResult);

            if (isTransientServerFailure && _transientCmRetryActive)
            {
                // A CM-rotation retry is about to re-attempt this logon on a different server.
                // Hold off on the error toast and rebuild teardown until the final attempt fails.
                return;
            }

            // TryAnotherCM is a transient "reconnect to a different CM" signal, not a login problem,
            // so give it a friendly stageKey instead of the generic one that echoes the raw enum.
            var stageKey = callback.Result == EResult.TryAnotherCM
                ? "signalr.steamSession.serverBusy"
                : "signalr.steamSession.loginFailed";

            // Send error notification to frontend
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
            {
                errorType,
                stageKey,
                context = new Dictionary<string, object?> { ["result"] = callback.Result.ToString() },
                result = callback.Result.ToString(),
                extendedResult = callback.ExtendedResult.ToString(),
                timestamp = DateTime.UtcNow,
                wasRebuildActive = IsRebuildRunning
            });

            // If a rebuild was in progress, send failure completion
            if (IsRebuildRunning)
            {
                SendDepotMappingFailure(errorMessage, errorType);
                _currentRebuildCts?.Cancel();
            }
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
