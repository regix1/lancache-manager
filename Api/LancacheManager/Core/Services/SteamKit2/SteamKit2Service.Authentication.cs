using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using SteamKit2;
using SteamKit2.Authentication;

namespace LancacheManager.Core.Services.SteamKit2;

public partial class SteamKit2Service
{
    private readonly object _loginOwnerLock = new();
    private bool _hasPendingLoginOwner;
    private string? _pendingLoginUsername;
    private IntegrationLogin? _loginAttempt;

    /// <summary>
    /// Authenticate with Steam using username and password. Authentication remains separate from
    /// depot mapping; a successful result may start the mapper at the controller boundary.
    ///
    /// The poll no longer takes the login request's abort signal. A mobile confirmation can wait
    /// minutes for a phone tap, and killing it because the browser went away meant the sign-in the
    /// user had already approved on their phone was thrown out. Only the tracked operation's own
    /// token ends the poll early now, which is what the card's cancel and shutdown both go through.
    /// The response still carries the outcome, because the Steam Guard challenge has no other route
    /// to the browser.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        string? twoFactorCode = null,
        string? emailCode = null,
        bool allowMobileConfirmation = false,
        Guid? ownerAccountId = null,
        IntegrationCaller? caller = null,
        Guid? attemptId = null,
        bool recover = false)
    {
        // The nullable-owner entry is retained for trusted internal callers. HTTP callers supply
        // the server-resolved identity, including the initiating LANCache session.
        caller ??= new(ownerAccountId, ownerAccountId, ownerAccountId is not null);
        lock (_loginOwnerLock)
            if (_loginReporter is not null) IntegrationLease.Refuse("login-in-progress");
        var access = _steamAuthRepository.GetIntegrationAccess(caller);
        var continuation = !string.IsNullOrWhiteSpace(twoFactorCode)
            || !string.IsNullOrWhiteSpace(emailCode)
            || (attemptId is not null && access.AttemptId == attemptId);
        var login = continuation
            ? _steamAuthRepository.ContinueIntegrationLogin(caller, attemptId)
            : await _steamAuthRepository.BeginIntegrationLoginAsync(caller, attemptId, recover);

        MappingOperationReporter? reporter = null;
        var admitted = false;
        var keepPendingLoginOwner = false;
        try
        {
            lock (_loginOwnerLock)
            {
                if (!_steamAuthRepository.RunIntegrationLogin(login, () =>
                {
                    if (_loginReporter is not null) IntegrationLease.Refuse("login-in-progress");
                    if (continuation && !string.Equals(_pendingLoginUsername, username, StringComparison.OrdinalIgnoreCase))
                        IntegrationLease.Refuse("attempt-expired");
                    _hasPendingLoginOwner = true;
                    _pendingLoginUsername = username;
                    _loginAttempt = login;
                    Interlocked.Exchange(ref _loginActive, 1);
                    // Never started: the reporter is only the sign-in's cancellation handle, so the
                    // sign-in has no run of its own and draws nothing.
                    reporter = CreateDepotMappingReporter(
                        _cancellationTokenSource.Token,
                        new RunNotice(EffectiveNotificationMode, RunTrigger.Manual));
                    _loginReporter = reporter;
                    admitted = true;
                })) IntegrationLease.Refuse("attempt-expired");
            }

            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(reporter!.Token);
            lifetime.CancelAfter(login.ExpiresAtUtc > DateTime.UtcNow ? login.ExpiresAtUtc - DateTime.UtcNow : TimeSpan.Zero);
            using var cancelled = lifetime.Token.Register(() =>
            {
                lock (_loginOwnerLock)
                    if (_loginAttempt == login) CancelLogin();
            });
            lifetime.Token.ThrowIfCancellationRequested();
            if (!_steamAuthRepository.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
            var pollResult = await PollCredentialsWithRetryAsync(
                login, username, password, twoFactorCode, emailCode, allowMobileConfirmation,
                "Steam authentication", lifetime.Token);
            lifetime.Token.ThrowIfCancellationRequested();
            if (!_steamAuthRepository.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();

            if (!pollResult.Success)
            {
                keepPendingLoginOwner = pollResult.Result.RequiresTwoFactor
                    || pollResult.Result.RequiresEmailCode
                    || pollResult.Result.RequiresMobileConfirmation;
                return await CompleteLoginAsync(login, pollResult.Result);
            }

            await _sessionGate.WaitAsync(lifetime.Token);
            try
            {
                if (!_steamAuthRepository.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
                var anonymous = IsSteamDaemonActive() != false;
                if (_isLoggedOn && (!anonymous || !HasSessionMode(anonymous: true)))
                    await ResetConnectionLockedAsync(lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested();
                if (!_steamAuthRepository.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
                if (!anonymous || !_isLoggedOn || _steamClient?.IsConnected != true)
                {
                    await LogonLockedAsync(anonymous ? null : new SteamUser.LogOnDetails
                    {
                        Username = pollResult.AccountName!,
                        AccessToken = pollResult.RefreshToken!,
                        ShouldRememberPassword = true,
                        LoginID = _steamLoginId
                    }, lifetime.Token, logonTimeout: TimeSpan.FromMinutes(2), anonymous: anonymous);
                }

                lock (_loginOwnerLock)
                {
                    lifetime.Token.ThrowIfCancellationRequested();
                    if (!_isLoggedOn) throw new SteamConnectionLostException("Steam ended the connection before sign-in completed. Please try again.");
                    var auth = new SteamAuthData
                    {
                        Mode = SteamAuthMode.Authenticated.ToWireString(),
                        Username = pollResult.AccountName,
                        RefreshToken = pollResult.RefreshToken,
                        LastAuthenticated = DateTime.UtcNow,
                        OwnerAccountId = login.AccountId
                    };
                    if (!_steamAuthRepository.CompleteIntegrationLogin(login, auth, () =>
                    {
                        _sessionReplaced = false;
                        _sessionCredential = anonymous ? null : (login.AccountId, pollResult.RefreshToken!);
                        _sessionAuthVersion = _steamAuthRepository.GetIntegrationSnapshot().Version;
                    })) throw new OperationCanceledException();
                }
            }
            finally { _sessionGate.Release(); }

            return await CompleteLoginAsync(login,
                new AuthenticationResult { Success = true, Message = "Authentication successful" });
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Steam sign-in cancelled");
            return await CompleteLoginAsync(login, new AuthenticationResult
            {
                Success = false, Message = "Sign-in was cancelled.", StageKey = "errors.steam.signInCancelled"
            });
        }
        catch (LancacheManager.Middleware.ApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is AsyncJobFailedException or SteamConnectionLostException)
        {
            _logger.LogWarning(ex, "Steam authentication could not reach a usable connection");
            return await CompleteLoginAsync(login, new AuthenticationResult
            {
                Success = false, Message = "Steam's servers are busy right now. Please try again.",
                StageKey = "errors.steam.serversBusy"
            });
        }
        catch (SteamLogonException ex)
        {
            if (_steamAuthRepository.IsIntegrationLoginCurrent(login)) NotifySessionError(ex);
            return await CompleteLoginAsync(login, new AuthenticationResult
            {
                Success = false, Message = ex.Message, StageKey = ex.StageKey
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Steam authentication failed");
            return await CompleteLoginAsync(login, new AuthenticationResult
            {
                Success = false, Message = "Steam sign-in could not be completed. Please try again."
            });
        }
        finally
        {
            lock (_loginOwnerLock)
            {
                if (admitted && ReferenceEquals(_loginReporter, reporter))
                {
                    _loginReporter = null;
                    Interlocked.Exchange(ref _loginActive, 0);
                    if (!keepPendingLoginOwner)
                    {
                        _steamAuthRepository.FinishIntegrationLogin(login);
                        _hasPendingLoginOwner = false;
                        _pendingLoginUsername = null;
                        _loginAttempt = null;
                        ReportSteamIntegrationAuthenticated();
                    }
                }
            }
            if (reporter is not null) await reporter.DisposeAsync();
        }
    }

    /// <summary>Trusted shutdown cancellation; HTTP callers use the actor-bound overload.</summary>
    public void CancelLogin()
    {
        lock (_loginOwnerLock)
        {
            if (_loginAttempt is { } login)
                _steamAuthRepository.FinishIntegrationLogin(login);
            _hasPendingLoginOwner = false;
            _pendingLoginUsername = null;
            _loginAttempt = null;
            var reporter = _loginReporter;
            _loginReporter = null;
            Interlocked.Exchange(ref _loginActive, 0);
            reporter?.RequestCancellation();
        }
    }

    public void CancelLogin(IntegrationCaller caller, Guid? attemptId)
    {
        lock (_loginOwnerLock)
            _steamAuthRepository.CancelIntegrationLogin(caller, attemptId, CancelLogin);
    }

    /// <summary>
    /// Stamps the owned attempt lifetime onto the outcome the caller returns. Mapping operation ids
    /// are deliberately absent because authentication has not started a mapping run.
    /// </summary>
    private static Task<AuthenticationResult> CompleteLoginAsync(
        IntegrationLogin login,
        AuthenticationResult result)
    {
        result.AttemptId = login.AttemptId;
        result.ExpiresAtUtc = login.ExpiresAtUtc;
        return Task.FromResult(result);
    }

    /// <summary>
    /// Runs the credentials auth poll while holding _sessionGate, retrying on a different CM
    /// server when the current one drops the auth job (AsyncJobFailedException) or the connection.
    /// The gate is held for the whole poll (which can wait minutes for a mobile confirmation) so
    /// no other flow can log the shared client into a different mode mid-authentication.
    /// </summary>
    private async Task<CredentialsAuthPollOutcome> PollCredentialsWithRetryAsync(
        IntegrationLogin login,
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
            lock (_loginOwnerLock)
            {
                if (!_steamAuthRepository.IsIntegrationLoginCurrent(login)) throw new OperationCanceledException();
                if (_initialized)
                {
                    _steamClient?.Disconnect();
                    InitializeSteamClient();
                    _isLoggedOn = false;
                }
            }
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
    /// <summary>Trusted installation invalidation; request paths supply their resolved caller.</summary>
    public Task LogoutAsync() => LogoutAsync((IntegrationCaller?)null);

    public async Task LogoutAsync(IntegrationCaller? caller)
    {
        await using var release = await _steamAuthRepository.BeginIntegrationReleaseAsync(caller);
        await LogoutAsync(release);
    }

    private async Task LogoutAsync(IntegrationLease release)
    {
        _steamAuthRepository.ValidateIntegrationLease(release);
        CancelLogin();
        if (IsRebuildRunning && _currentRebuildCts is { } rebuild)
        {
            _currentMappingReporter?.RequestCancellation();
            rebuild.Cancel();
            if (_currentBuildTask is { } build) await Task.WhenAny(build, Task.Delay(3000));
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var daemon = scope.ServiceProvider.GetService<SteamDaemonService>();
            if (daemon is not null)
                await daemon.TerminateAllSessionsAsync("Steam PICS authentication logged out", includePersistent: false);
        }

        await _sessionGate.WaitAsync(_cancellationTokenSource.Token);
        try
        {
            _intentionalDisconnect = true;
            await DisconnectAsync();
            _steamAuthRepository.CompleteIntegrationRelease(release, auth =>
            {
                auth.OwnerAccountId = null;
                auth.RefreshToken = null;
                auth.Username = null;
                auth.LastAuthenticated = null;
                auth.Mode = SteamAuthMode.Anonymous.ToWireString();
            });
            lock (_loginOwnerLock)
            {
                _sessionCredential = null;
                _isLoggedOn = false;
            }
            ReportSteamIntegrationAuthenticated();
        }
        finally { _sessionGate.Release(); }
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
            _connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

        global::SteamKit2.Authentication.AuthPollResult pollResponse;
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
                        Message = "Mobile confirmation required",
                        StageKey = "errors.steam.mobileConfirmationRequired"
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

}
