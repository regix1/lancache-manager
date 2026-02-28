using LancacheManager.Hubs;
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

        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

        _logger.LogInformation("Connecting to Steam...");
        _steamClient!.Connect();

        // Wait for connected (increased timeout to handle Steam server delays)
        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(60), ct, "Connecting to Steam");

        // Check if Steam daemon is active - if so, use anonymous mode to avoid session conflicts
        var isSteamDaemonActive = IsSteamDaemonActive();

        // Check if we have a saved refresh token for authenticated login
        var refreshToken = _stateService.GetSteamRefreshToken();
        var authMode = _stateService.GetSteamAuthMode();

        if (isSteamDaemonActive)
        {
            _logger.LogInformation("Steam daemon is active - using anonymous mode for depot mapping to avoid session conflicts");
            _steamUser!.LogOnAnonymous();
        }
        else if (!string.IsNullOrEmpty(refreshToken) && authMode == "authenticated")
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
        }
        else
        {
            _logger.LogInformation("Logging in anonymously...");
            _steamUser!.LogOnAnonymous();
            _logger.LogInformation("SteamKit2 anonymous login (no LoginID)");
        }

        // Wait for logged on (increased timeout to handle Steam server delays)
        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(60), ct, "Logging into Steam");
    }

    private async Task WaitForTaskWithTimeout(Task task, TimeSpan timeout, CancellationToken ct, string operationName = "Steam operation")
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
            if (IsSteamDaemonActive())
            {
                _logger.LogInformation("Steam daemon is active - using anonymous mode for reconnection to avoid session conflicts");
                _steamUser!.LogOnAnonymous();
                _logger.LogInformation("SteamKit2 reconnect anonymous login (no LoginID)");
            }
            else
            {
                // Check if we have a saved refresh token for authenticated login
                var refreshToken = _stateService.GetSteamRefreshToken();
                var authMode = _stateService.GetSteamAuthMode();

                if (!string.IsNullOrEmpty(refreshToken) && authMode == "authenticated")
                {
                    var username = _stateService.GetSteamUsername();
                    _logger.LogInformation("Re-logging in with saved refresh token for user: {Username}", username);

                    _steamUser!.LogOn(new SteamUser.LogOnDetails
                    {
                        Username = username,
                        AccessToken = refreshToken,
                        ShouldRememberPassword = true,
                        LoginID = _steamLoginId
                    });
                    _logger.LogInformation("SteamKit2 reconnect login with LoginID: {LoginID} (0x{LoginIDHex:X8}) for user: {Username}", _steamLoginId, _steamLoginId, username);
                }
                else
                {
                    _logger.LogInformation("Re-logging in anonymously...");
                    _steamUser!.LogOnAnonymous();
                    _logger.LogInformation("SteamKit2 reconnect anonymous login (no LoginID)");
                }
            }
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
        // This prevents endless reconnection loops after PICS crawls complete
        if (_isRunning && IsRebuildRunning)
        {
            _reconnectAttempt++;

            // Check if we've exceeded max attempts
            if (_reconnectAttempt > MaxReconnectAttempts)
            {
                var errorMessage = $"Failed to maintain Steam connection after {MaxReconnectAttempts} reconnection attempts. Steam servers may be experiencing issues or your network connection is unstable.";
                _logger.LogError("Max reconnection attempts ({MaxAttempts}) exceeded during rebuild", MaxReconnectAttempts);
                _lastErrorMessage = errorMessage;

                // Fail any pending connection/login tasks
                _connectedTcs?.TrySetException(new Exception(errorMessage));
                _loggedOnTcs?.TrySetException(new Exception(errorMessage));

                // Cancel the rebuild
                _currentRebuildCts?.Cancel();

                // Send error notification via SignalR
                _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
                {
                    errorType = "ConnectionFailed",
                    message = errorMessage,
                    reconnectAttempts = _reconnectAttempt,
                    timestamp = DateTime.UtcNow,
                    wasRebuildActive = true
                });

                // Send failure completion event
                _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingComplete, new
                {
                    success = false,
                    error = errorMessage,
                    errorType = "ConnectionFailed",
                    depotMappingsFound = _depotToAppMappings.Count,
                    timestamp = DateTime.UtcNow
                });

                _reconnectAttempt = 0; // Reset for next time
                return;
            }

            // Exponential backoff: 5s, 10s, 20s, 40s, 60s (capped)
            var delaySeconds = Math.Min(5 * (int)Math.Pow(2, _reconnectAttempt - 1), MaxReconnectDelaySeconds);
            _logger.LogInformation("Unexpected disconnection during active rebuild - attempting to reconnect in {Delay} seconds (attempt {Attempt}/{MaxAttempts})...",
                delaySeconds, _reconnectAttempt, MaxReconnectAttempts);

            // Send progress update so UI knows we're reconnecting
            SendDepotMappingProgress(
                $"Reconnecting to Steam (attempt {_reconnectAttempt}/{MaxReconnectAttempts})...",
                $"Connection lost. Reconnecting in {delaySeconds} seconds...",
                isReconnecting: true,
                reconnectAttempt: _reconnectAttempt
            ).Wait(); // Fire-and-forget equivalent for synchronous context

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
                _connectedTcs?.TrySetException(new Exception("Disconnected from Steam"));
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
                    "Steam server is temporarily unavailable. This often happens after a session was replaced. Please wait a moment and try again, or re-authenticate."
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

            _lastErrorMessage = errorMessage;
            _loggedOnTcs?.TrySetException(new Exception($"Logon failed: {callback.Result} / {callback.ExtendedResult}"));
            _logger.LogError("Unable to logon to Steam: {Result} / {ExtendedResult}", callback.Result, callback.ExtendedResult);

            // Send error notification to frontend
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
            {
                errorType,
                message = errorMessage,
                result = callback.Result.ToString(),
                extendedResult = callback.ExtendedResult.ToString(),
                timestamp = DateTime.UtcNow,
                wasRebuildActive = IsRebuildRunning
            });

            // If a rebuild was in progress, send failure completion
            if (IsRebuildRunning)
            {
                _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingComplete, new
                {
                    success = false,
                    error = errorMessage,
                    errorType,
                    depotMappingsFound = _depotToAppMappings.Count,
                    timestamp = DateTime.UtcNow
                });

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
            _stateService.SetSteamAuthMode("anonymous");
            _stateService.SetSteamRefreshToken(null);
            _stateService.SetSteamUsername(null);

            // Notify frontend to update auth state (so the UI reflects anonymous mode)
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamAutoLogout, new
            {
                message = errorMessage,
                reason = errorType,
                replacementCount = 0,
                timestamp = DateTime.UtcNow
            });
        }

        // For credential-invalidating errors, clear stored credentials and notify frontend
        if (errorType is "InvalidCredentials" or "AuthenticationRequired" or "SessionExpired")
        {
            _logger.LogWarning("Steam credentials are no longer valid ({ErrorType}). Clearing stored credentials.", errorType);
            _stateService.SetSteamRefreshToken(null);
            _stateService.SetSteamUsername(null);
            _stateService.SetSteamAuthMode("anonymous");

            // Notify frontend to update auth state
            _notifications.NotifyAllFireAndForget(SignalREvents.SteamAutoLogout, new
            {
                message = errorMessage,
                reason = errorType,
                replacementCount = 0,
                timestamp = DateTime.UtcNow
            });
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
                    message = errorMessage,
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
                _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingComplete, new
                {
                    success = false,
                    error = errorMessage,
                    errorType,
                    depotMappingsFound = _depotToAppMappings.Count,
                    timestamp = DateTime.UtcNow
                });

                // Cancel the rebuild
                _currentRebuildCts?.Cancel();
            }
        }
    }

    /// <summary>
    /// Helper method to disconnect from Steam properly
    /// </summary>
    private async Task DisconnectFromSteamAsync(int delayMs = 1000)
    {
        if (_steamClient?.IsConnected == true)
        {
            _steamUser?.LogOff();
            await Task.Delay(delayMs);
            _steamClient.Disconnect();
        }
    }

}
