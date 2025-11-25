using Microsoft.AspNetCore.SignalR;
using SteamKit2;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
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
        await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(60), ct);

        // Check if we have a saved refresh token for authenticated login
        var refreshToken = _stateService.GetSteamRefreshToken();
        var authMode = _stateService.GetSteamAuthMode();

        if (!string.IsNullOrEmpty(refreshToken) && authMode == "authenticated")
        {
            var username = _stateService.GetSteamUsername();
            _logger.LogInformation("Logging in with saved refresh token for user: {Username}", username);

            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = username,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });
        }
        else
        {
            _logger.LogInformation("Logging in anonymously...");
            _steamUser!.LogOnAnonymous();
        }

        // Wait for logged on (increased timeout to handle Steam server delays)
        await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromSeconds(60), ct);
    }

    private async Task WaitForTaskWithTimeout(Task task, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        var completedTask = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

        if (completedTask != task)
        {
            throw new TimeoutException("Operation timed out");
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

        if (!_connectedTcs?.Task.IsCompleted ?? false)
        {
            _connectedTcs?.TrySetException(new Exception("Disconnected during connect"));
        }

        // Only reconnect if we're running AND there's an active rebuild
        // This prevents endless reconnection loops after PICS crawls complete
        if (_isRunning && IsRebuildRunning)
        {
            _logger.LogInformation("Unexpected disconnection during active rebuild - attempting to reconnect in 5 seconds...");
            Task.Delay(5000, _cancellationTokenSource.Token).ContinueWith(_ =>
            {
                if (_isRunning && IsRebuildRunning && !_cancellationTokenSource.Token.IsCancellationRequested)
                {
                    _steamClient?.Connect();
                }
            }, _cancellationTokenSource.Token);
        }
        else
        {
        }
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
    {
        if (callback.Result == EResult.OK)
        {
            _isLoggedOn = true;
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
            _ = _hubContext.Clients.All.SendAsync("SteamSessionError", new
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
                _ = _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
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
        var (errorType, errorMessage, shouldCancelRebuild) = callback.Result switch
        {
            EResult.LogonSessionReplaced => (
                "SessionReplaced",
                "Your Steam session was replaced. This happens when you log into Steam from another device or application (Steam client, another server, etc.). Please close other Steam sessions and try again.",
                true
            ),
            EResult.LoggedInElsewhere => (
                "LoggedInElsewhere",
                "You logged into Steam from another location. Steam only allows one active session for PICS access. Please close other Steam sessions and try again.",
                true
            ),
            EResult.AccountLogonDenied => (
                "AuthenticationRequired",
                "Steam authentication is required. Please re-authenticate with your Steam account.",
                true
            ),
            EResult.InvalidPassword => (
                "InvalidCredentials",
                "Your Steam credentials are no longer valid. Please re-authenticate with your Steam account.",
                true
            ),
            EResult.Expired => (
                "SessionExpired",
                "Your Steam session has expired. Please re-authenticate with your Steam account.",
                true
            ),
            _ => (
                "Disconnected",
                $"Disconnected from Steam: {callback.Result}",
                false
            )
        };

        // Send SignalR notification for significant errors
        if (shouldCancelRebuild)
        {
            _lastErrorMessage = errorMessage;

            // Send error notification to frontend
            _ = _hubContext.Clients.All.SendAsync("SteamSessionError", new
            {
                errorType,
                message = errorMessage,
                result = callback.Result.ToString(),
                timestamp = DateTime.UtcNow,
                wasRebuildActive = IsRebuildRunning
            });

            // Cancel the rebuild if one is active
            if (IsRebuildRunning)
            {
                _logger.LogError("Steam session error during active rebuild: {ErrorType} - {Message}", errorType, errorMessage);

                // Send a failure completion event
                _ = _hubContext.Clients.All.SendAsync("DepotMappingComplete", new
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
    /// Start idle disconnect timer to close connection after inactivity
    /// </summary>
    private void StartIdleDisconnectTimer()
    {
        // Cancel existing timer if any
        _idleDisconnectTimer?.Dispose();

        // Create new timer that checks every 10 seconds
        _idleDisconnectTimer = new Timer(async _ =>
        {
            if (_steamClient?.IsConnected == true && !IsRebuildRunning)
            {
                var idleTime = DateTime.UtcNow - _lastConnectionActivity;
                if (idleTime.TotalSeconds >= ConnectionKeepAliveSeconds)
                {
                    _intentionalDisconnect = true;
                    await DisconnectFromSteamAsync();
                    StopIdleDisconnectTimer();
                }
            }
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Stop idle disconnect timer
    /// </summary>
    private void StopIdleDisconnectTimer()
    {
        _idleDisconnectTimer?.Dispose();
        _idleDisconnectTimer = null;
    }
}
