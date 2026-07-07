using SteamKit2;
using SteamKit2.Authentication;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Acquires a Steam refresh token without saving credentials or logging into the shared SteamKit2 session.
    /// Used for scheduled prefill credential storage that must remain isolated from depot mapping auth.
    /// </summary>
    public async Task<AuthenticationResult> AcquireRefreshTokenAsync(
        string username,
        string password,
        string? twoFactorCode = null,
        string? emailCode = null,
        bool allowMobileConfirmation = false)
    {
        try
        {
            var pollResult = await RunWithCmRetryAsync(
                () => PollCredentialsAuthAsync(
                    username,
                    password,
                    twoFactorCode,
                    emailCode,
                    allowMobileConfirmation),
                "Steam refresh token acquisition");

            if (!pollResult.Success)
            {
                return pollResult.Result;
            }

            return new AuthenticationResult
            {
                Success = true,
                Message = "Authentication successful",
                AccountName = pollResult.AccountName,
                RefreshToken = pollResult.RefreshToken
            };
        }
        catch (AsyncJobFailedException ex)
        {
            _logger.LogError(ex, "Scheduled prefill Steam refresh token acquisition failed (Steam job failed - servers likely busy)");
            return new AuthenticationResult
            {
                Success = false,
                Message = "Steam's servers are busy right now. This is temporary, please wait a moment and try again."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduled prefill Steam refresh token acquisition failed");
            return new AuthenticationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Authenticate with Steam using username and password
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, string? twoFactorCode = null, string? emailCode = null, bool allowMobileConfirmation = false)
    {
        try
        {
            var pollResult = await RunWithCmRetryAsync(
                () => PollCredentialsAuthAsync(
                    username,
                    password,
                    twoFactorCode,
                    emailCode,
                    allowMobileConfirmation),
                "Steam authentication");

            if (!pollResult.Success)
            {
                return pollResult.Result;
            }

            // Store refresh token
            _stateService.SetSteamRefreshToken(pollResult.RefreshToken!);
            _logger.LogInformation("Successfully authenticated and saved refresh token");

            // Now login with the refresh token
            await RunWithCmRetryAsync(async () =>
            {
                _loggedOnTcs = new TaskCompletionSource();
                _steamUser!.LogOn(new SteamUser.LogOnDetails
                {
                    Username = pollResult.AccountName!,
                    AccessToken = pollResult.RefreshToken!,
                    ShouldRememberPassword = true,
                    LoginID = _steamLoginId
                });
                _logger.LogInformation(
                    "SteamKit2 auth-flow login with LoginID: {LoginID} (0x{LoginIDHex:X8}) for user: {Username}",
                    _steamLoginId,
                    _steamLoginId,
                    pollResult.AccountName);

                // Use longer timeout for authentication (Steam servers can be slow)
                await WaitWithTimeoutAsync(_loggedOnTcs.Task, TimeSpan.FromMinutes(2), CancellationToken.None);
                return true;
            }, "Steam sign-in");

            return new AuthenticationResult
            {
                Success = true,
                Message = "Authentication successful"
            };
        }
        catch (AsyncJobFailedException ex)
        {
            // A Steam connection-manager dropped the auth job mid-flight (usually TryAnotherCM
            // server churn) and the CM-rotation retries were exhausted. Surface a friendly
            // message, not the raw exception text.
            _logger.LogError(ex, "Authentication failed (Steam job failed - servers likely busy)");
            return new AuthenticationResult
            {
                Success = false,
                Message = "Steam's servers are busy right now. This is temporary, please wait a moment and try again."
            };
        }
        catch (SteamTransientLoginException ex)
        {
            // Logon kept landing on busy CM servers even after rotating; Message is already friendly.
            _logger.LogError(ex, "Authentication failed after CM-rotation retries (Steam servers busy)");
            return new AuthenticationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return new AuthenticationResult
            {
                Success = false,
                Message = ex.Message
            };
        }
    }

    /// <summary>
    /// Runs a Steam auth step, reconnecting and retrying (bounded) when the current CM server
    /// turns us away with a transient failure: a TryAnotherCM/ServiceUnavailable logon result or a
    /// dropped auth job (AsyncJobFailedException). SteamKit2 marks the failing endpoint bad, so the
    /// reconnect rotates to a different CM server - staying on the same connection just fails again.
    /// </summary>
    private async Task<T> RunWithCmRetryAsync<T>(Func<Task<T>> step, string stepName, CancellationToken ct = default)
    {
        await _cmRetryGate.WaitAsync(ct);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                _transientCmRetryActive = attempt <= TransientCmRetries;
                try
                {
                    return await step();
                }
                catch (Exception ex) when (attempt <= TransientCmRetries &&
                                           ex is AsyncJobFailedException or SteamTransientLoginException)
                {
                    _logger.LogWarning(
                        "{Step} hit a busy Steam server (attempt {Attempt}/{MaxAttempts}) - reconnecting to a different server and retrying: {Message}",
                        stepName, attempt, TransientCmRetries + 1, ex.Message);
                    try
                    {
                        await ReconnectForCmRetryAsync(ct);
                    }
                    catch (Exception reconnectEx)
                    {
                        // Reconnection itself failed (e.g. Steam dropped us mid-handshake). Surface
                        // the original transient failure - its message is the friendly one.
                        _logger.LogWarning(reconnectEx, "Reconnect during {Step} retry failed - surfacing the original transient failure", stepName);
                        throw ex;
                    }
                }
                finally
                {
                    _transientCmRetryActive = false;
                }
            }
        }
        finally
        {
            _cmRetryGate.Release();
        }
    }

    /// <summary>
    /// Forces a fresh connection so SteamKit2 picks a different CM server. After TryAnotherCM the
    /// CM usually disconnects us itself; after a dropped auth job we are still connected to the
    /// same bad server and must disconnect explicitly.
    /// </summary>
    private async Task ReconnectForCmRetryAsync(CancellationToken ct)
    {
        if (_steamClient?.IsConnected == true)
        {
            _intentionalDisconnect = true;
            _steamClient.Disconnect();
        }

        // Give the disconnect a moment to settle before reconnecting
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        // The disconnect callback can land after the settle delay and fault the fresh
        // _connectedTcs below (seen live as "Disconnected from Steam" killing the retry),
        // so allow one extra connect pass before giving up.
        for (var connectAttempt = 1; ; connectAttempt++)
        {
            _connectedTcs = new TaskCompletionSource();
            _steamClient!.Connect();
            try
            {
                await WaitWithTimeoutAsync(_connectedTcs.Task, TimeSpan.FromSeconds(30), ct, "Reconnecting to Steam");
                return;
            }
            catch (Exception ex) when (connectAttempt == 1 && !ct.IsCancellationRequested)
            {
                _logger.LogWarning("Reconnect pass {Attempt} failed ({Message}) - trying once more", connectAttempt, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    /// <summary>
    /// Thrown when Steam logon fails with a transient server-side result (TryAnotherCM /
    /// ServiceUnavailable): the CM turned us away and a different server should be tried.
    /// The message is already user-friendly.
    /// </summary>
    public sealed class SteamTransientLoginException : Exception
    {
        public SteamTransientLoginException(string message) : base(message) { }
    }

    /// <summary>
    /// Logout from Steam and clear stored credentials
    /// </summary>
    public async Task LogoutAsync()
    {
        try
        {
            // Cancel any active PICS rebuild
            if (IsRebuildRunning && _currentRebuildCts != null)
            {
                _logger.LogInformation("Cancelling active PICS rebuild before logout");
                try
                {
                    _currentRebuildCts.Cancel();

                    // Wait briefly for cancellation to complete
                    if (_currentBuildTask != null)
                    {
                        await Task.WhenAny(_currentBuildTask, Task.Delay(3000));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cancelling rebuild during logout");
                }
            }

            // Terminate Steam daemon sessions
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var daemonService = scope.ServiceProvider.GetService<SteamDaemonService>();

                if (daemonService != null)
                {
                    await daemonService.TerminateAllSessionsAsync(
                        "Steam PICS authentication logged out");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error terminating Steam daemon sessions during logout");
            }

            // Clear stored credentials and reset to anonymous mode
            ClearSteamCredentials();

            // Disconnect from Steam
            _intentionalDisconnect = true;
            await DisconnectAsync();

            _logger.LogInformation("Logged out from Steam and cleared credentials");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
        }
    }

    private sealed class CredentialsAuthPollOutcome
    {
        public bool Success { get; init; }
        public AuthenticationResult Result { get; init; } = new();
        public string? AccountName { get; init; }
        public string? RefreshToken { get; init; }
    }

    private async Task<CredentialsAuthPollOutcome> PollCredentialsAuthAsync(
        string username,
        string password,
        string? twoFactorCode,
        string? emailCode,
        bool allowMobileConfirmation)
    {
        // Connect if not already connected
        if (_steamClient?.IsConnected != true)
        {
            _connectedTcs = new TaskCompletionSource();
            _steamClient!.Connect();
            await WaitWithTimeoutAsync(_connectedTcs.Task, TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        var authenticator = new WebAuthenticator(twoFactorCode, emailCode, allowMobileConfirmation);

        var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
        {
            Username = username,
            Password = password,
            IsPersistentSession = true,
            Authenticator = authenticator
        });

        var pollResponse = default(global::SteamKit2.Authentication.AuthPollResult);
        try
        {
            pollResponse = await authSession.PollingWaitForResultAsync();
        }
        catch (AuthenticationException authEx) when (authEx.Message.Contains("Expired"))
        {
            _logger.LogWarning("Authentication session expired - user did not confirm in time");
            return new CredentialsAuthPollOutcome
            {
                Success = false,
                Result = new AuthenticationResult
                {
                    Success = false,
                    SessionExpired = true,
                    Message =
                        "Authentication session expired. Please try again and confirm on your Steam Mobile App within 2 minutes, or use a 2FA code instead."
                }
            };
        }
        catch (InvalidOperationException)
        {
            if (authenticator.NeedsMobileConfirmation)
            {
                return new CredentialsAuthPollOutcome
                {
                    Success = false,
                    Result = new AuthenticationResult
                    {
                        Success = false,
                        RequiresMobileConfirmation = true,
                        Message = "Mobile confirmation required"
                    }
                };
            }

            if (authenticator.NeedsTwoFactor)
            {
                return new CredentialsAuthPollOutcome
                {
                    Success = false,
                    Result = new AuthenticationResult
                    {
                        Success = false,
                        RequiresTwoFactor = true,
                        Message = "Two-factor authentication code required"
                    }
                };
            }

            if (authenticator.NeedsEmailCode)
            {
                return new CredentialsAuthPollOutcome
                {
                    Success = false,
                    Result = new AuthenticationResult
                    {
                        Success = false,
                        RequiresEmailCode = true,
                        Message = "Email verification code required"
                    }
                };
            }

            throw;
        }

        if (pollResponse == null)
        {
            throw new InvalidOperationException("Authentication failed - no poll response received");
        }

        return new CredentialsAuthPollOutcome
        {
            Success = true,
            AccountName = pollResponse.AccountName,
            RefreshToken = pollResponse.RefreshToken
        };
    }

    /// <summary>
    /// Simple authenticator for web-based authentication
    /// </summary>
    private class WebAuthenticator : IAuthenticator
    {
        private readonly string? _twoFactorCode;
        private readonly string? _emailCode;
        private readonly bool _allowMobileConfirmation;

        public bool NeedsTwoFactor { get; private set; }
        public bool NeedsEmailCode { get; private set; }
        public bool NeedsMobileConfirmation { get; private set; }

        public WebAuthenticator(string? twoFactorCode, string? emailCode, bool allowMobileConfirmation = false)
        {
            _twoFactorCode = twoFactorCode;
            _emailCode = emailCode;
            _allowMobileConfirmation = allowMobileConfirmation;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            NeedsTwoFactor = true;
            if (!string.IsNullOrEmpty(_twoFactorCode))
            {
                return Task.FromResult(_twoFactorCode);
            }
            throw new InvalidOperationException("Two-factor code required");
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            NeedsEmailCode = true;
            if (!string.IsNullOrEmpty(_emailCode))
            {
                return Task.FromResult(_emailCode);
            }
            throw new InvalidOperationException("Email code required");
        }

        public Task<bool> AcceptDeviceConfirmationAsync()
        {
            // If user provided a 2FA code, don't wait for mobile confirmation
            // Return false so SteamKit2 will call GetDeviceCodeAsync instead
            if (!string.IsNullOrEmpty(_twoFactorCode))
            {
                return Task.FromResult(false);
            }

            // If mobile confirmation is not allowed and no code provided, throw error
            if (!_allowMobileConfirmation)
            {
                NeedsMobileConfirmation = true;
                throw new InvalidOperationException("Mobile confirmation required");
            }

            // Return true to tell SteamKit2 to wait for the user to confirm via Steam Mobile App
            // This enables the mobile push notification flow
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Authentication result
    /// </summary>
    public class AuthenticationResult
    {
        public bool Success { get; set; }
        public bool RequiresTwoFactor { get; set; }
        public bool RequiresEmailCode { get; set; }
        public bool RequiresMobileConfirmation { get; set; }
        public bool SessionExpired { get; set; }
        public string? Message { get; set; }
        public string? AccountName { get; set; }
        public string? RefreshToken { get; set; }
    }
}
