using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using SteamKit2;
using SteamKit2.Authentication;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    /// <summary>
    /// Authenticate with Steam using username and password. The sign-in owns a tracked depot mapping
    /// operation for its whole life, so its notification card carries a real operation id and the
    /// cancel on the bar reaches the credentials poll.
    ///
    /// The poll no longer takes the login request's abort signal. A mobile confirmation can wait
    /// minutes for a phone tap, and killing it because the browser went away meant the sign-in the
    /// user had already approved on their phone was thrown out. Only the tracked operation's own
    /// token ends the poll early now, which is what the card's cancel and shutdown both go through.
    /// The response still carries the outcome, because the Steam Guard challenge has no other route
    /// to the browser.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(string username, string password, string? twoFactorCode = null, string? emailCode = null, bool allowMobileConfirmation = false)
    {
        if (Interlocked.CompareExchange(ref _loginActive, 1, 0) != 0)
        {
            return new AuthenticationResult
            {
                Success = false,
                Message = "A Steam sign-in is already in progress."
            };
        }

        MappingOperationReporter? reporter = null;
        try
        {
            // Signing in is an explicit user action, so do not inherit the visibility decision of the
            // last scheduled crawl - a silent one would leave the sign-in with no card at all.
            _depotRunShowNotification = EffectiveNotificationMode.AllowsTrigger(RunTrigger.Manual);
            reporter = CreateDepotMappingReporter(_cancellationTokenSource.Token);
            // Published before the poll starts, so a modal closed during the phone-approval wait has
            // something to cancel for the whole time the wait can run.
            _loginReporter = reporter;

            // This stage key is what the card shows for the whole wait, which runs to a minute while
            // the user goes and finds their phone. The reporter's default would say "Starting depot
            // mapping...", which is the wrong thing to read at that moment.
            await reporter.StartAsync(
                CreateDepotContext(message: "Waiting for Steam sign-in..."),
                stageKey: "signalr.steamLogin.waitingSignIn");

            var pollResult = await PollCredentialsWithRetryAsync(
                username,
                password,
                twoFactorCode,
                emailCode,
                allowMobileConfirmation,
                "Steam authentication",
                reporter.Token);

            if (!pollResult.Success)
            {
                // A Steam Guard prompt, a wrong code or an expired confirmation window. The attempt
                // is over either way; the next submit from the modal starts its own operation.
                return await CompleteLoginAsync(
                    reporter,
                    cancelled: false,
                    pollResult.Result);
            }

            // Store refresh token
            _stateService.SetSteamRefreshToken(pollResult.RefreshToken!);
            _logger.LogInformation("Successfully authenticated and saved refresh token");

            // Now log on with the fresh refresh token through the shared session engine
            await _sessionGate.WaitAsync(reporter.Token);
            try
            {
                // Longer logon timeout for the interactive auth flow (Steam servers can be slow)
                await LogonLockedAsync(new SteamUser.LogOnDetails
                {
                    Username = pollResult.AccountName!,
                    AccessToken = pollResult.RefreshToken!,
                    ShouldRememberPassword = true,
                    LoginID = _steamLoginId
                }, reporter.Token, logonTimeout: TimeSpan.FromMinutes(2));
            }
            finally
            {
                _sessionGate.Release();
            }

            // Terminal here rather than at the caller, so this operation is finished before the
            // caller starts the PICS rebuild and its own depotMapping operation.
            return await CompleteLoginAsync(
                reporter,
                cancelled: false,
                new AuthenticationResult
                {
                    Success = true,
                    Message = "Authentication successful"
                });
        }
        catch (OperationCanceledException)
        {
            // The tracked operation was cancelled: the card's cancel, or shutdown. Not an error.
            _logger.LogInformation("Steam sign-in cancelled");
            return await CompleteLoginAsync(
                reporter,
                cancelled: true,
                new AuthenticationResult
                {
                    Success = false,
                    Message = "Sign-in was cancelled."
                });
        }
        catch (Exception ex) when (ex is AsyncJobFailedException or SteamConnectionLostException)
        {
            // A CM dropped the auth job or the connection mid-login and the CM-rotation retries
            // were exhausted. Surface a friendly message, not the raw exception text.
            _logger.LogError(ex, "Authentication failed (Steam servers busy): {Message}", ex.Message);
            return await CompleteLoginAsync(
                reporter,
                cancelled: false,
                new AuthenticationResult
                {
                    Success = false,
                    Message = "Steam's servers are busy right now. This is temporary, please wait a moment and try again."
                });
        }
        catch (SteamLogonException ex)
        {
            // The logon was rejected after retries. Message is already friendly; the toast keeps
            // the second frontend surface (SteamSessionError) in sync with the modal response.
            _logger.LogError(ex, "Authentication failed: {Message}", ex.Message);
            NotifySessionError(ex);
            return await CompleteLoginAsync(
                reporter,
                cancelled: false,
                new AuthenticationResult
                {
                    Success = false,
                    Message = ex.Message
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authentication failed");
            return await CompleteLoginAsync(
                reporter,
                cancelled: false,
                new AuthenticationResult
                {
                    Success = false,
                    Message = ex.Message
                });
        }
        finally
        {
            // Cleared before the dispose below, so a cancel arriving now finds nothing rather than a
            // reporter that is about to go away.
            _loginReporter = null;

            if (reporter is not null)
            {
                // Backstop: a path that reached neither the returns above nor a catch still lands on
                // one terminal here, because the reporter completes itself when it is disposed
                // unfinished. A sign-in that registers and never completes would leave a card that
                // never clears, which is worse than the untracked login this replaced.
                await reporter.DisposeAsync();
            }

            Interlocked.Exchange(ref _loginActive, 0);
        }
    }

    /// <summary>
    /// Cancels an in-flight sign-in WITHOUT touching saved credentials or an authenticated session -
    /// safe to call when the user closes the login modal. Only the sign-in's own tracked operation is
    /// stopped, so a PICS rebuild running beside it keeps going. Nothing in flight is a no-op, which
    /// is what a close during the Steam Guard step hits: that step waits on the person, not on Steam.
    /// </summary>
    public void CancelLogin()
    {
        try
        {
            _loginReporter?.RequestCancellation();
        }
        catch (ObjectDisposedException)
        {
            // The poll finished between the field read and the cancel.
        }
    }

    /// <summary>
    /// Ends the sign-in's tracked operation and stamps its id onto the outcome the caller returns.
    /// A reporter that never started (the service was disposed mid-request) completes to nothing and
    /// leaves the id empty, which is the honest answer: no operation was ever registered.
    /// </summary>
    private async Task<AuthenticationResult> CompleteLoginAsync(
        MappingOperationReporter? reporter,
        bool cancelled,
        AuthenticationResult result)
    {
        if (reporter is null)
        {
            return result;
        }

        var errorDetail = result.Success || cancelled ? null : result.Message;
        await reporter.CompleteAsync(
            success: result.Success,
            error: errorDetail,
            cancelled: cancelled,
            context: CreateDepotContext(message: result.Message, errorDetail: errorDetail));
        result.OperationId = reporter.IsStarted ? reporter.OperationId : null;
        return result;
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
        string stepName,
        CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            // A guard code the user just typed is single-use. Re-running the poll starts a fresh
            // auth session carrying a code Steam has already spent, so the retry cannot succeed and
            // the user is told the servers are busy when the real answer is that the code is gone.
            // Retry only the legs that carry no code (first password submit, mobile confirmation),
            // where rotating to another CM server is free.
            if (!string.IsNullOrEmpty(twoFactorCode) || !string.IsNullOrEmpty(emailCode))
            {
                return await PollCredentialsAuthAsync(
                    username,
                    password,
                    twoFactorCode,
                    emailCode,
                    allowMobileConfirmation,
                    ct);
            }

            return await RetryOnBusyCmLockedAsync(
                () => PollCredentialsAuthAsync(
                    username,
                    password,
                    twoFactorCode,
                    emailCode,
                    allowMobileConfirmation,
                    ct),
                stepName,
                ct);
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
                    if (_currentPicsOperationId.HasValue)
                    {
                        _depotRunFailures.TryRemove(_currentPicsOperationId.Value, out _);
                    }
                    _currentMappingReporter?.RequestCancellation();
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
        bool allowMobileConfirmation,
        CancellationToken ct)
    {
        // Connect if not already connected
        if (_steamClient?.IsConnected != true)
        {
            _connectedTcs = new TaskCompletionSource();
            _steamClient!.Connect();
            await WaitWithTimeoutAsync(_connectedTcs.Task, TimeSpan.FromSeconds(30), ct);
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
            pollResponse = await authSession.PollingWaitForResultAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // SteamKit2 fails in-flight jobs with TaskCanceledException on a LOCAL failure (the
            // connection dropped mid-poll) - distinct from AsyncJobFailedException (the remote CM
            // dropped the job) and from a genuine caller cancellation (ct). Map it so the
            // CM-rotation retry treats it like any other transient connection loss.
            throw new SteamConnectionLostException("Steam connection dropped during sign-in");
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
                        Message = authenticator.CodeWasRejected
                            ? "Steam did not accept that two-factor code. Codes expire quickly and each one works once, so open your authenticator and enter the current code."
                            : "Two-factor authentication code required"
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
                        Message = authenticator.CodeWasRejected
                            ? "Steam did not accept that email code. Check the latest message from Steam and enter the code it contains."
                            : "Email verification code required"
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
    // Internal rather than private, with InternalsVisibleTo("LancacheManager.Tests"), so a test can
    // prove a rejected code is never handed back. Resubmitting one spins SteamKit2's guard loop with
    // no delay and no exit, and nothing else in the stack can catch that.
    internal class WebAuthenticator : IAuthenticator
    {
        private readonly string? _twoFactorCode;
        private readonly string? _emailCode;
        private readonly bool _allowMobileConfirmation;

        public bool NeedsTwoFactor { get; private set; }
        public bool NeedsEmailCode { get; private set; }
        public bool NeedsMobileConfirmation { get; private set; }

        /// <summary>
        /// Set when Steam rejected the code that was submitted, as opposed to never having been
        /// given one. The two need different wording: the first prompt asks for a code, a rejection
        /// has to say the code did not work or the user retypes the same one.
        /// </summary>
        public bool CodeWasRejected { get; private set; }

        public WebAuthenticator(string? twoFactorCode, string? emailCode, bool allowMobileConfirmation = false)
        {
            _twoFactorCode = twoFactorCode;
            _emailCode = emailCode;
            _allowMobileConfirmation = allowMobileConfirmation;
        }

        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
        {
            NeedsTwoFactor = true;
            // Steam sets previousCodeWasIncorrect after rejecting the last code and asks again for a
            // NEW one. A submitted code is single-use, so returning the same one leaves SteamKit2's
            // resubmit loop with nothing to change: it sends, gets TwoFactorCodeMismatch, and asks
            // again immediately, with no delay and no exit. Failing here ends the poll, and the
            // InvalidOperationException catch turns it into RequiresTwoFactor so the modal asks for
            // a fresh code.
            if (previousCodeWasIncorrect)
            {
                CodeWasRejected = true;
                throw new InvalidOperationException("Two-factor code was rejected");
            }
            if (!string.IsNullOrEmpty(_twoFactorCode))
            {
                return Task.FromResult(_twoFactorCode);
            }
            throw new InvalidOperationException("Two-factor code required");
        }

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        {
            NeedsEmailCode = true;
            // Same single-use rule as the device code above.
            if (previousCodeWasIncorrect)
            {
                CodeWasRejected = true;
                throw new InvalidOperationException("Email code was rejected");
            }
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

        /// <summary>
        /// The tracked operation the sign-in ran under. Null only when the service was shutting down
        /// and no operation was ever registered.
        /// </summary>
        public Guid? OperationId { get; set; }
    }
}
