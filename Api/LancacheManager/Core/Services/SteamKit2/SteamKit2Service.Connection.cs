using LancacheManager.Hubs;
using SteamKit2;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    private async Task ConnectAndLoginAsync(CancellationToken ct)
    {
        if (_isLoggedOn && _steamClient?.IsConnected == true)
        {
            return;
        }

        // Clear auto-logout flag when starting fresh connection
        _sessionReplacementAutoLogout = false;

        _connectedTcs = new TaskCompletionSource();
        _loggedOnTcs = new TaskCompletionSource();

        _logger.LogInformation("Connecting to Steam...");
        _steamClient!.Connect();

        // Wait for connected (increased timeout to handle Steam server delays)
        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(60), ct, "Connecting to Steam");

        // Check if a prefill daemon is currently authenticated
        // If so, we must use anonymous mode to avoid session conflicts
        var isPrefillDaemonActive = _prefillDaemonService.IsAnyDaemonAuthenticated();

        // Check if we have a saved refresh token for authenticated login
        var refreshToken = _stateService.GetSteamRefreshToken();
        var authMode = _stateService.GetSteamAuthMode();

        if (isPrefillDaemonActive)
        {
            // Prefill daemon has priority - use anonymous mode to avoid kicking it out
            _logger.LogInformation("Prefill daemon is active - using anonymous mode for depot mapping to avoid session conflicts");
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
        }
        else
        {
            _logger.LogInformation("Logging in anonymously...");
            _steamUser!.LogOnAnonymous();
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

            // Check if a prefill daemon is currently authenticated
            // If so, we must use anonymous mode to avoid session conflicts
            var isPrefillDaemonActive = _prefillDaemonService.IsAnyDaemonAuthenticated();

            // Check if we have a saved refresh token for authenticated login
            var refreshToken = _stateService.GetSteamRefreshToken();
            var authMode = _stateService.GetSteamAuthMode();

            if (isPrefillDaemonActive)
            {
                // Prefill daemon has priority - use anonymous mode to avoid kicking it out
                _logger.LogInformation("Prefill daemon is active - using anonymous mode for depot mapping to avoid session conflicts");
                _steamUser!.LogOnAnonymous();
            }
            else if (!string.IsNullOrEmpty(refreshToken) && authMode == "authenticated")
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
            }
            else
            {
                _logger.LogInformation("Re-logging in anonymously...");
                _steamUser!.LogOnAnonymous();
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

        // Don't reconnect if we're intentionally yielding to prefill daemon
        if (_yieldingToPrefillDaemon)
        {
            _logger.LogInformation("Not reconnecting - yielding session to prefill daemon");
            _reconnectAttempt = 0;
            return;
        }

        // Only reconnect if we're running AND there's an active rebuild
        // This prevents endless reconnection loops after PICS crawls complete
        // Also skip reconnection if auto-logout due to session replacement just occurred
        if (_isRunning && IsRebuildRunning && !_sessionReplacementAutoLogout)
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
            _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingProgress, new
            {
                status = $"Reconnecting to Steam (attempt {_reconnectAttempt}/{MaxReconnectAttempts})...",
                percentComplete = _totalBatches > 0 ? (_processedBatches * 100.0 / _totalBatches) : 0,
                processedBatches = _processedBatches,
                totalBatches = _totalBatches,
                depotMappingsFound = _depotToAppMappings.Count,
                isLoggedOn = false,
                isReconnecting = true,
                reconnectAttempt = _reconnectAttempt,
                maxReconnectAttempts = MaxReconnectAttempts,
                message = $"Connection lost. Reconnecting in {delaySeconds} seconds..."
            });

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
            _sessionReplacementAutoLogout = false; // Clear auto-logout flag
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _isLoggedOn = true;

            // Only reset session replacement counter if this is NOT a reconnection after a session replacement
            // This prevents the counter from resetting when we automatically reconnect after being kicked
            if (!_isReconnectingAfterSessionReplaced)
            {
                _stateService.ResetSessionReplacedCount();
            }

            _loggedOnTcs?.TrySetResult();
            _logger.LogInformation("Successfully logged onto Steam!");
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
                "Your Steam session was replaced. This happens when you log into Steam from another device or application (Steam client, another server, etc.). Please close other Steam sessions and try again.",
                true,
                true
            ),
            EResult.LoggedInElsewhere => (
                "LoggedInElsewhere",
                "You logged into Steam from another location. Steam only allows one active session for PICS access. Please close other Steam sessions and try again.",
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

        // Track session replacement errors and auto-logout after repeated failures
        // Counter is persisted to state.json to survive server restarts
        if (isSessionReplaced)
        {
            // Check if a prefill daemon is currently authenticated
            // If so, session replacement is expected behavior - the prefill daemon has priority
            // Don't count this as an error or show notifications
            var isPrefillDaemonActive = _prefillDaemonService.IsAnyDaemonAuthenticated();

            if (isPrefillDaemonActive)
            {
                _logger.LogInformation("Session replaced by prefill daemon (expected behavior). Depot mapping will use anonymous mode or retry later.");
                // Don't increment counter, don't show errors - this is expected when prefill is running
                shouldCancelRebuild = false; // Don't treat as error
                _isReconnectingAfterSessionReplaced = false;
            }
            else
            {
                _stateService.IncrementSessionReplacedCount();
                var currentCount = _stateService.GetSessionReplacedCount();
                _isReconnectingAfterSessionReplaced = true; // Prevent counter reset on reconnection login
                _logger.LogWarning("Session replaced count: {Count}/{Max}", currentCount, MaxSessionReplacedBeforeLogout);

                if (currentCount >= MaxSessionReplacedBeforeLogout)
                {
                    _logger.LogWarning("Steam session was replaced. Auto-logging out and switching to anonymous mode. User must re-authenticate.");

                    // Set flag to prevent OnDisconnected from attempting reconnections
                    _sessionReplacementAutoLogout = true;

                    // Clear credentials to stop reconnection attempts
                    _stateService.SetSteamRefreshToken(null);
                    _stateService.SetSteamUsername(null);
                    _stateService.SetSteamAuthMode("anonymous");
                    _stateService.ResetSessionReplacedCount(); // Reset counter in state.json
                    _isReconnectingAfterSessionReplaced = false; // Clear flag since we're logging out

                    errorMessage = "Your Steam session was replaced by another login. This usually means you logged into Steam from another device or application (Steam client, browser, etc.). Your credentials have been cleared and the system has switched to anonymous mode. Please close other Steam sessions and re-authenticate.";
                    errorType = "AutoLogout";

                    // Send auto-logout notification
                    _notifications.NotifyAllFireAndForget(SignalREvents.SteamAutoLogout, new
                    {
                        message = errorMessage,
                        reason = "RepeatedSessionReplacement",
                        replacementCount = MaxSessionReplacedBeforeLogout,
                        timestamp = DateTime.UtcNow
                    });
                }
            }
        }
        else
        {
            // Reset counter on other types of disconnections (not session replaced)
            _stateService.ResetSessionReplacedCount();
            _isReconnectingAfterSessionReplaced = false;
        }

        // Send SignalR notification for significant errors
        // Skip SteamSessionError if auto-logout was triggered since we already sent SteamAutoLogout event
        if (shouldCancelRebuild)
        {
            _lastErrorMessage = errorMessage;

            // Only send SteamSessionError if this isn't an auto-logout scenario
            // (auto-logout already sends SteamAutoLogout which the frontend handles)
            if (!_sessionReplacementAutoLogout)
            {
                _notifications.NotifyAllFireAndForget(SignalREvents.SteamSessionError, new
                {
                    errorType,
                    message = errorMessage,
                    result = callback.Result.ToString(),
                    timestamp = DateTime.UtcNow,
                    wasRebuildActive = IsRebuildRunning,
                    sessionReplacedCount = isSessionReplaced ? _stateService.GetSessionReplacedCount() : 0
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

    /// <summary>
    /// Wait for Steam to fully disconnect. Useful when you need to ensure a clean disconnection before reconnecting.
    /// </summary>
    /// <param name="timeoutMs">Maximum time to wait in milliseconds (default 5000ms)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>True if disconnected successfully, false if timeout reached</returns>
    public async Task<bool> WaitForDisconnectionAsync(int timeoutMs = 5000, CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;

        while (_steamClient?.IsConnected == true)
        {
            if (ct.IsCancellationRequested)
                return false;

            if ((DateTime.UtcNow - startTime).TotalMilliseconds >= timeoutMs)
            {
                _logger.LogWarning("Timeout waiting for Steam disconnection after {TimeoutMs}ms", timeoutMs);
                return false;
            }

            await Task.Delay(100, ct); // Check every 100ms
        }

        return true;
    }

    /// <summary>
    /// Event handler called when a prefill daemon session authenticates.
    /// Yields the SteamKit2 session to avoid conflicts.
    /// </summary>
    private async Task OnPrefillDaemonAuthenticatedAsync()
    {
        _logger.LogInformation("Prefill daemon authenticated - yielding SteamKit2 session to avoid conflicts");

        // Set flag to prevent reconnection
        _yieldingToPrefillDaemon = true;

        // If we're currently connected, disconnect gracefully
        if (_isLoggedOn && _steamClient?.IsConnected == true)
        {
            _intentionalDisconnect = true;
            await DisconnectFromSteamAsync();
            _logger.LogInformation("Disconnected SteamKit2 session to yield to prefill daemon");

            // Notify UI that depot mapping is paused
            _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingProgress, new
            {
                status = "Paused - Prefill daemon is using Steam session",
                percentComplete = _totalBatches > 0 ? (_processedBatches * 100.0 / _totalBatches) : 0,
                processedBatches = _processedBatches,
                totalBatches = _totalBatches,
                depotMappingsFound = _depotToAppMappings.Count,
                isLoggedOn = false,
                isPaused = true,
                pauseReason = "PrefillDaemonActive"
            });
        }
    }

    /// <summary>
    /// Event handler called when all prefill daemon sessions have ended.
    /// Resumes the SteamKit2 session if needed.
    /// </summary>
    private async Task OnPrefillDaemonSessionEndedAsync()
    {
        _logger.LogInformation("All prefill daemon sessions ended - SteamKit2 can resume if needed");

        // Clear the yielding flag
        _yieldingToPrefillDaemon = false;

        // If we have an active rebuild that needs to continue, reconnect
        if (IsRebuildRunning && !_isLoggedOn)
        {
            _logger.LogInformation("Resuming SteamKit2 session for active depot mapping operation");

            try
            {
                await ConnectAndLoginAsync(_cancellationTokenSource.Token);
                _logger.LogInformation("Successfully resumed SteamKit2 session after prefill daemon ended");

                // Notify UI that depot mapping has resumed
                _notifications.NotifyAllFireAndForget(SignalREvents.DepotMappingProgress, new
                {
                    status = "Resumed depot mapping",
                    percentComplete = _totalBatches > 0 ? (_processedBatches * 100.0 / _totalBatches) : 0,
                    processedBatches = _processedBatches,
                    totalBatches = _totalBatches,
                    depotMappingsFound = _depotToAppMappings.Count,
                    isLoggedOn = _isLoggedOn,
                    isPaused = false
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resume SteamKit2 session after prefill daemon ended");
            }
        }
    }
}
