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
            _loggedOnTcs?.TrySetException(new Exception($"Logon failed: {callback.Result} / {callback.ExtendedResult}"));
            _logger.LogError($"Unable to logon to Steam: {callback.Result} / {callback.ExtendedResult}");
        }
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
    {
        _logger.LogWarning($"Logged off of Steam: {callback.Result}");
        _isLoggedOn = false;
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
