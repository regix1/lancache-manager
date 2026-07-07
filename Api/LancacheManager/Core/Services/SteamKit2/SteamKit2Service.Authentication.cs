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
            var pollResult = await PollCredentialsWithRetryAsync(
                username,
                password,
                twoFactorCode,
                emailCode,
                allowMobileConfirmation,
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
        catch (Exception ex) when (ex is AsyncJobFailedException or SteamConnectionLostException)
        {
            _logger.LogError(ex, "Scheduled prefill Steam refresh token acquisition failed (Steam servers busy)");
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
            var pollResult = await PollCredentialsWithRetryAsync(
                username,
                password,
                twoFactorCode,
                emailCode,
                allowMobileConfirmation,
                "Steam authentication");

            if (!pollResult.Success)
            {
                return pollResult.Result;
            }

            // Store refresh token
            _stateService.SetSteamRefreshToken(pollResult.RefreshToken!);
            _logger.LogInformation("Successfully authenticated and saved refresh token");

            // Now log on with the fresh refresh token through the shared session engine
            await _sessionGate.WaitAsync();
            try
            {
                // Longer logon timeout for the interactive auth flow (Steam servers can be slow)
                await LogonLockedAsync(new SteamUser.LogOnDetails
                {
                    Username = pollResult.AccountName!,
                    AccessToken = pollResult.RefreshToken!,
                    ShouldRememberPassword = true,
                    LoginID = _steamLoginId
                }, CancellationToken.None, logonTimeout: TimeSpan.FromMinutes(2));
            }
            finally
            {
                _sessionGate.Release();
            }

            return new AuthenticationResult
            {
                Success = true,
                Message = "Authentication successful"
            };
        }
        catch (Exception ex) when (ex is AsyncJobFailedException or SteamConnectionLostException)
        {
            // A CM dropped the auth job or the connection mid-login and the CM-rotation retries
            // were exhausted. Surface a friendly message, not the raw exception text.
            _logger.LogError(ex, "Authentication failed (Steam servers busy): {Message}", ex.Message);
            return new AuthenticationResult
            {
                Success = false,
                Message = "Steam's servers are busy right now. This is temporary, please wait a moment and try again."
            };
        }
        catch (SteamLogonException ex)
        {
            // The logon was rejected after retries. Message is already friendly; the toast keeps
            // the second frontend surface (SteamSessionError) in sync with the modal response.
            _logger.LogError(ex, "Authentication failed: {Message}", ex.Message);
            NotifySessionError(ex);
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
    /// Runs the credentials auth poll while holding _sessionGate, retrying on a different CM
    /// server when the current one drops the auth job (AsyncJobFailedException) or the connection.
    /// The gate is held for the whole poll (which can wait minutes for a mobile confirmation) so
    /// no other flow can log the shared client into a different mode mid-authentication.
    /// </summary>
    private async Task<CredentialsAuthPollOutcome> PollCredentialsWithRetryAsync(
        string username,
        string password,
        string? twoFactorCode,
        string? emailCode,
        bool allowMobileConfirmation,
        string stepName)
    {
        await _sessionGate.WaitAsync();
        try
        {
            return await RetryOnBusyCmLockedAsync(
                () => PollCredentialsAuthAsync(
                    username,
                    password,
                    twoFactorCode,
                    emailCode,
                    allowMobileConfirmation),
                stepName,
                CancellationToken.None);
        }
        finally
        {
            _sessionGate.Release();
        }
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
