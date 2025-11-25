using SteamKit2;
using SteamKit2.Authentication;

namespace LancacheManager.Application.Services;

public partial class SteamKit2Service
{
    /// <summary>
    /// Authenticate with Steam using username and password
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, string? twoFactorCode = null, string? emailCode = null, bool allowMobileConfirmation = false)
    {
        try
        {
            // Connect if not already connected
            if (_steamClient?.IsConnected != true)
            {
                _connectedTcs = new TaskCompletionSource();
                _steamClient!.Connect();
                await WaitForTaskWithTimeout(_connectedTcs.Task, TimeSpan.FromSeconds(30), CancellationToken.None);
            }

            // Create authenticator that returns provided codes
            var authenticator = new WebAuthenticator(twoFactorCode, emailCode, allowMobileConfirmation);

            // Begin authentication session using CredentialsAuthSession
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = true,
                Authenticator = authenticator
            });

            // Poll for result - this may throw if 2FA/email/mobile confirmation is required
            var pollResponse = default(SteamKit2.Authentication.AuthPollResult);
            try
            {
                pollResponse = await authSession.PollingWaitForResultAsync();
            }
            catch (AuthenticationException authEx) when (authEx.Message.Contains("Expired"))
            {
                // Steam's authentication session timed out (usually ~2-3 minutes for mobile confirmation)
                _logger.LogWarning("Authentication session expired - user did not confirm in time");
                return new AuthenticationResult
                {
                    Success = false,
                    SessionExpired = true,
                    Message = "Authentication session expired. Please try again and confirm on your Steam Mobile App within 2 minutes, or use a 2FA code instead."
                };
            }
            catch (InvalidOperationException)
            {
                // Check if we need mobile confirmation
                if (authenticator.NeedsMobileConfirmation)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresMobileConfirmation = true,
                        Message = "Mobile confirmation required"
                    };
                }

                // Check if we need 2FA or email code
                if (authenticator.NeedsTwoFactor)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresTwoFactor = true,
                        Message = "Two-factor authentication code required"
                    };
                }

                if (authenticator.NeedsEmailCode)
                {
                    return new AuthenticationResult
                    {
                        Success = false,
                        RequiresEmailCode = true,
                        Message = "Email verification code required"
                    };
                }

                // Re-throw if it's not a 2FA/email code/mobile confirmation request
                throw;
            }

            // NOTE: NewGuardData is not stored - modern Steam auth uses refresh tokens only
            // The NewGuardData field is legacy and usually null/empty with modern authentication

            // Ensure pollResponse is not null
            if (pollResponse == null)
            {
                throw new InvalidOperationException("Authentication failed - no poll response received");
            }

            // Store refresh token
            _stateService.SetSteamRefreshToken(pollResponse.RefreshToken);
            _logger.LogInformation("Successfully authenticated and saved refresh token");

            // Now login with the refresh token
            _loggedOnTcs = new TaskCompletionSource();
            _steamUser!.LogOn(new SteamUser.LogOnDetails
            {
                Username = pollResponse.AccountName,
                AccessToken = pollResponse.RefreshToken,
                ShouldRememberPassword = true
            });

            // Use longer timeout for authentication (Steam servers can be slow)
            await WaitForTaskWithTimeout(_loggedOnTcs.Task, TimeSpan.FromMinutes(2), CancellationToken.None);

            return new AuthenticationResult
            {
                Success = true,
                Message = "Authentication successful"
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

            // Clear stored refresh token (GuardData not used in modern auth)
            _stateService.SetSteamRefreshToken(null);

            // Disconnect from Steam
            _intentionalDisconnect = true;
            await DisconnectFromSteamAsync();

            _logger.LogInformation("Logged out from Steam and cleared credentials");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during logout");
        }
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
    }
}
