using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;

namespace LancacheManager.Core.Services;

public abstract partial class PrefillDaemonServiceBase
{
#region Socket Event Handlers

    /// <summary>
    /// Handles credential challenge events from socket communication.
    /// </summary>
    private async Task OnCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        try
        {
            // Update auth state based on credential type
            session.AuthState = challenge.CredentialType switch
            {
                "username" => DaemonAuthState.UsernameRequired,
                "password" => DaemonAuthState.PasswordRequired,
                "2fa" => DaemonAuthState.TwoFactorRequired,
                "steamguard" => DaemonAuthState.SteamGuardRequired,
                "device-confirmation" => DaemonAuthState.DeviceConfirmationRequired,
                "authorization-url" => DaemonAuthState.AuthorizationUrlRequired,
                _ => session.AuthState
            };

            await NotifyCredentialChallengeAsync(session, challenge);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket credential challenge for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Handles status update events from socket communication.
    /// </summary>
    private async Task OnStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        try
        {
            var previousAuthState = session.AuthState;

            // Update auth state based on status
            var newAuthState = status.Status switch
            {
                "awaiting-login" => DaemonAuthState.NotAuthenticated,
                "logged-in" => DaemonAuthState.Authenticated,
                _ => session.AuthState
            };

            session.AuthState = newAuthState;

            // Capture the login-required service display name (Epic account name / Xbox gamertag)
            // from daemon status updates. This populates session.Username AND persists it via
            // SetUsernameAsync so it drives both the admin display AND username-banning. Anonymous
            // services (Battle.net/Riot) never report a DisplayName and ban via the UserId GUID path.
            if (newAuthState == DaemonAuthState.Authenticated
                && (session.Platform == "Epic" || session.Platform == "Xbox")
                && !string.IsNullOrEmpty(status.DisplayName))
            {
                session.Username = status.DisplayName;
                await _sessionService.SetUsernameAsync(session.Id, status.DisplayName);
            }

            if (session.AuthState != previousAuthState)
            {
                await NotifyAuthStateChangeAsync(session);

                // Notify derived class when a daemon becomes authenticated
                if (newAuthState == DaemonAuthState.Authenticated)
                {
                    FireAndForgetAsync(OnSessionAuthenticatedAsync, nameof(OnSessionAuthenticatedAsync));
                }
                // Notify when auth state changes FROM authenticated to non-authenticated
                else if (previousAuthState == DaemonAuthState.Authenticated && newAuthState != DaemonAuthState.Authenticated)
                {
                    // Check if any other daemons are still authenticated
                    if (!IsAnyDaemonAuthenticated())
                    {
                        FireAndForgetAsync(OnAllSessionsLoggedOutAsync, nameof(OnAllSessionsLoggedOutAsync));
                    }
                }
            }

            await NotifyStatusChangeAsync(session, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket status change for session {SessionId}", session.Id);
        }
    }

    /// <summary>
    /// Handles progress update events from socket communication.
    /// </summary>
    private async Task OnProgressChangeAsync(DaemonSession session, SocketPrefillProgress socketProgress)
    {
        try
        {
            // Convert socket progress to internal PrefillProgress format
            // Property names match daemon's PrefillProgressUpdate class
            var progress = new PrefillProgress
            {
                State = socketProgress.State ?? "downloading",
                CurrentAppId = socketProgress.CurrentAppId,
                CurrentAppName = socketProgress.CurrentAppName,
                TotalBytes = socketProgress.TotalBytes,
                BytesDownloaded = socketProgress.BytesDownloaded,
                PercentComplete = socketProgress.PercentComplete,
                BytesPerSecond = (long)socketProgress.BytesPerSecond,
                ElapsedSeconds = socketProgress.ElapsedSeconds,
                TotalApps = socketProgress.TotalApps,
                UpdatedApps = socketProgress.UpdatedApps,
                UpdatedAt = socketProgress.UpdatedAt,
                Result = socketProgress.Result,
                ErrorMessage = socketProgress.ErrorMessage,
                // Map depot info for cache tracking
                Depots = socketProgress.Depots?.Select(d => new DepotManifestProgressInfo
                {
                    DepotId = d.DepotId,
                    ManifestId = d.ManifestId,
                    TotalBytes = d.TotalBytes
                }).ToList()
            };

            _logger.LogDebug("Socket Progress: {AppName} ({AppId}) - {State}, {Bytes}/{Total} bytes",
                progress.CurrentAppName, progress.CurrentAppId, progress.State,
                progress.BytesDownloaded, progress.TotalBytes);

            await NotifyPrefillProgressAsync(session, progress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error handling socket progress for session {SessionId}", session.Id);
        }
    }

    #endregion

    /// <summary>
    /// Broadcasts a payload to all subscribed connections for a session.
    /// On error, removes the failing connectionId from the session's subscriptions unless removeOnError is false.
    /// </summary>
    private async Task BroadcastToSubscribersAsync(DaemonSession session, string eventName, object payload, bool removeOnError = true)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await SendToClientAsync(connectionId, eventName, payload);
            }
            catch (Exception ex)
            {
                if (removeOnError)
                {
                    _logger.LogWarning(ex, "Failed to notify {EventName} to {ConnectionId}, removing subscription", eventName, connectionId);
                    session.SubscribedConnections.Remove(connectionId);
                }
                // When removeOnError is false, silently ignore the error
            }
        }
    }

    protected async Task NotifyAuthStateChangeAsync(DaemonSession session)
    {
        // Single robust point covering every login path (interactive + auto-login): once a
        // session transitions to Authenticated, it no longer needs a re-login. Clear the flag
        // here so a previously-flagged persistent container stops reporting needs-relogin.
        if (session.AuthState == DaemonAuthState.Authenticated)
        {
            session.NeedsRelogin = false;
        }

        var payload = new { sessionId = session.Id, authState = session.AuthState.ToString() };
        await BroadcastToSubscribersAsync(session, EventAuthStateChanged, payload);

        // Mirror to DownloadHub so management UIs (persistent container list, prefill sessions)
        // update via SignalR instead of polling when a daemon self-authenticates or logs in.
        await NotifyHubAsync(EventAuthStateChanged, payload);

        try
        {
            var dto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(EventSessionUpdated, dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to broadcast session update after auth state change for session {SessionId}",
                session.Id);
        }
    }

    private async Task NotifyCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        await BroadcastToSubscribersAsync(session, EventCredentialChallenge,
            new { sessionId = session.Id, challenge });
    }

    private async Task NotifyStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        await BroadcastToSubscribersAsync(session, EventStatusChanged,
            new { sessionId = session.Id, status });
    }

    /// <summary>
    /// Emits the non-terminal <c>started</c> state transition: resets the per-run terminal
    /// idempotency guard, records the start time, clears any previous completion result, and
    /// broadcasts exactly one <c>PrefillStateChanged</c>. Terminal transitions
    /// (completed/failed/cancelled) go exclusively through <see cref="TransitionToTerminalAsync"/>.
    /// </summary>
    private async Task NotifyPrefillStartedAsync(DaemonSession session)
    {
        // Track when prefill started for duration calculation
        session.PrefillStartedAt = DateTime.UtcNow;
        // Arm the terminal funnel for this run (allows exactly one terminal transition)
        Interlocked.Exchange(ref session.TerminalCompletedFlag, 0);
        session.PrefillState = PrefillState.Started;
        // Clear any previous completion info
        session.LastPrefillCompletedAt = null;
        session.LastPrefillDurationSeconds = null;
        session.LastPrefillStatus = null;
        // Seed stall-watchdog state so a prefill that never moves is detectable from the start.
        // Written via Volatile so the cleanup-timer thread observes a torn-free tick value.
        Volatile.Write(ref session.LastProgressTicksUtc, DateTime.UtcNow.Ticks);
        session.LastProgressBytes = 0;

        var state = PrefillProgressState.Started.ToWireString();
        var startedPayload = new { sessionId = session.Id, state, durationSeconds = (int?)null };
        await BroadcastToSubscribersAsync(session, EventPrefillStateChanged, startedPayload);

        // Mirror to DownloadHub so management UIs (persistent container list, prefill sessions)
        // observe the prefill state change via SignalR instead of polling (matches
        // NotifyAuthStateChangeAsync's AuthStateChanged mirror).
        await NotifyHubAsync(EventPrefillStateChanged, startedPayload);
    }

    /// <summary>
    /// THE SINGLE terminal funnel for a prefill run. Idempotent via an
    /// <see cref="Interlocked.CompareExchange(ref int, int, int)"/> guard on
    /// <see cref="DaemonSession.TerminalCompletedFlag"/>, so a socket-death + a late daemon
    /// terminal event can never double-fire. This is the ONLY place that:
    /// sets <c>IsPrefilling=false</c>, records the <c>LastPrefill*</c> completion result,
    /// clears the <c>LastProgress</c> snapshot, and emits exactly one <c>PrefillStateChanged</c>.
    /// ALL terminal paths (completed / failed / cancelled / cancel / socket-disconnect) route here.
    /// </summary>
    private async Task TransitionToTerminalAsync(DaemonSession session, PrefillState terminalState)
    {
        // Idempotency: only the first caller for this run wins.
        if (Interlocked.CompareExchange(ref session.TerminalCompletedFlag, 1, 0) != 0)
        {
            return;
        }

        var state = terminalState switch
        {
            PrefillState.Completed => PrefillProgressState.Completed.ToWireString(),
            PrefillState.Failed => PrefillProgressState.Failed.ToWireString(),
            PrefillState.Cancelled => PrefillProgressState.Cancelled.ToWireString(),
            // Defensive: a non-terminal value should never reach here; treat as Failed.
            _ => PrefillProgressState.Failed.ToWireString()
        };

        int? durationSeconds = null;
        if (session.PrefillStartedAt.HasValue)
        {
            durationSeconds = (int)(DateTime.UtcNow - session.PrefillStartedAt.Value).TotalSeconds;
        }

        // The terminal funnel is the SOLE setter of IsPrefilling=false (started/download keep it true).
        session.IsPrefilling = false;
        session.PrefillState = terminalState;
        session.LastProgress = null;
        Volatile.Write(ref session.LastProgressTicksUtc, 0L);
        session.CurrentAppId = null;
        session.CurrentAppName = null;
        session.PreviousAppId = null;
        session.PreviousAppName = null;

        // Store the last prefill result for clients that were disconnected during prefill
        session.LastPrefillCompletedAt = DateTime.UtcNow;
        session.LastPrefillDurationSeconds = durationSeconds;
        session.LastPrefillStatus = state;

        _logger.LogInformation("Prefill {State} for session {SessionId}, duration: {Duration}s",
            state, session.Id, durationSeconds ?? 0);

        var terminalPayload = new { sessionId = session.Id, state, durationSeconds };
        await BroadcastToSubscribersAsync(session, EventPrefillStateChanged, terminalPayload);

        // Mirror to DownloadHub so management UIs update via SignalR instead of polling when a
        // prefill reaches a terminal state (matches NotifyAuthStateChangeAsync's AuthStateChanged mirror).
        await NotifyHubAsync(EventPrefillStateChanged, terminalPayload);

        // Keep admin pages in sync (IsPrefilling flipped false, current app cleared).
        try
        {
            var dto = DaemonSessionDto.FromSession(session);
            await NotifyHubAsync(EventSessionUpdated, dto);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast session update after terminal transition for session {SessionId}", session.Id);
        }
    }

    private async Task NotifyPrefillProgressAsync(DaemonSession session, PrefillProgress progress)
    {
        // Update session's current app info for admin visibility
        var appInfoChanged = session.CurrentAppId != progress.CurrentAppId ||
                             session.CurrentAppName != progress.CurrentAppName;

        // Track history: detect game transitions
        if (appInfoChanged && !string.IsNullOrEmpty(progress.CurrentAppId))
        {
            // If there was an app being prefilled, complete its history entry
            // Use the STORED bytes (from before the transition), not progress bytes (which are for the new app)
            if (!string.IsNullOrEmpty(session.CurrentAppId))
            {
                try
                {
                    // If no bytes were downloaded, mark as Cached
                    var status = session.CurrentBytesDownloaded == 0 ? "Cached" : "Completed";

                    await _sessionService.CompleteEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        session.CurrentBytesDownloaded,
                        session.CurrentTotalBytes);

                    _logger.LogInformation("App {Status} in session {SessionId}: {AppId} ({AppName}) - {Bytes}/{Total} bytes",
                        status, session.Id, session.CurrentAppId, session.CurrentAppName,
                        session.CurrentBytesDownloaded, session.CurrentTotalBytes);

                    // Broadcast history update
                    await BroadcastHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }

            // Start a new history entry for the current app
            try
            {
                var entry = await _sessionService.StartEntryAsync(session.Id, progress.CurrentAppId, progress.CurrentAppName);

                // Only broadcast if an entry was actually created (won't create if recently completed)
                if (entry != null)
                {
                    _logger.LogDebug("Started prefill history for app {AppId} ({AppName}) in session {SessionId}",
                        progress.CurrentAppId, progress.CurrentAppName, session.Id);

                    // Broadcast history update
                    await BroadcastHistoryUpdatedAsync(session.Id, progress.CurrentAppId, "InProgress");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start prefill history entry for app {AppId}", progress.CurrentAppId);
            }

            // Reset bytes tracking for the new app, then update from current progress
            session.CurrentBytesDownloaded = 0;
            session.CurrentTotalBytes = 0;
        }

        // Update bytes from progress BEFORE handling completion events
        // This ensures even instant completions (cached games) have the correct bytes
        if (!string.IsNullOrEmpty(progress.CurrentAppId))
        {
            if (progress.BytesDownloaded > 0)
            {
                session.CurrentBytesDownloaded = progress.BytesDownloaded;
            }
            if (progress.TotalBytes > 0)
            {
                session.CurrentTotalBytes = progress.TotalBytes;
            }
        }

        // Handle individual app completion (daemon sends "app_completed" for each app)
        // IMPORTANT: Use progress.CurrentAppId here, NOT session.CurrentAppId
        // For cached games, daemon sends app_completed without a prior "downloading" event,
        // so session.CurrentAppId may still point to the previous app
        if (PrefillProgressStateExtensions.ParseOrUnknown(progress.State) == PrefillProgressState.AppCompleted
            && !string.IsNullOrEmpty(progress.CurrentAppId))
        {
            try
            {
                // Check the Result field from daemon to determine if game was actually downloaded
                // "Success" = downloaded, "AlreadyUpToDate"/"Skipped"/"NoDepotsToDownload" = cached/skipped
                var isCached = progress.Result is "AlreadyUpToDate" or "Skipped" or "NoDepotsToDownload";

                // Determine the status based on the result
                string status;
                if (isCached)
                {
                    status = "Cached";
                }
                else if (progress.Result == "Failed")
                {
                    status = "Failed";
                }
                else
                {
                    status = "Completed";
                }

                // Use bytes from the app_completed event - daemon sends accurate final values
                // For Success: BytesDownloaded = TotalBytes (full size)
                // For AlreadyUpToDate/Skipped: BytesDownloaded = 0
                var bytesDownloaded = progress.BytesDownloaded > 0 ? progress.BytesDownloaded : session.CurrentBytesDownloaded;
                var totalBytes = progress.TotalBytes > 0 ? progress.TotalBytes : session.CurrentTotalBytes;

                await _sessionService.CompleteEntryAsync(
                    session.Id,
                    progress.CurrentAppId,
                    status,
                    bytesDownloaded,
                    totalBytes);

                _logger.LogInformation("App {Status} ({Result}): {AppId} ({AppName}) - {Bytes}/{Total} bytes",
                    status, progress.Result, progress.CurrentAppId, progress.CurrentAppName,
                    bytesDownloaded, totalBytes);

                // Broadcast history update
                await BroadcastHistoryUpdatedAsync(session.Id, progress.CurrentAppId, status);

                // Record cached depots for successful downloads (including AlreadyUpToDate)
                // This allows us to skip re-downloading games that are already cached
                if (progress.Result is "Success" or "AlreadyUpToDate" && progress.Depots != null && progress.Depots.Count > 0)
                {
                    try
                    {
                        if (uint.TryParse(progress.CurrentAppId, out var numericAppId))
                        {
                            await _cacheService.RecordCachedDepotsAsync(
                                numericAppId,
                                progress.CurrentAppName,
                                progress.Depots.Select(d => (d.DepotId, d.ManifestId, d.TotalBytes)),
                                session.SteamUsername);
                        }
                    }
                    catch (Exception cacheEx)
                    {
                        _logger.LogWarning(cacheEx, "Failed to record cached depots for app {AppId}", progress.CurrentAppId);
                    }
                }

                // Send the app completion event to frontend with appropriate state
                // For cached games, use "already_cached" so frontend can show animation
                // For downloaded games, use "app_completed"
                var frontendProgress = new PrefillProgress
                {
                    State = (isCached ? PrefillProgressState.AlreadyCached : PrefillProgressState.AppCompleted).ToWireString(),
                    CurrentAppId = progress.CurrentAppId,
                    CurrentAppName = progress.CurrentAppName,
                    TotalBytes = totalBytes,
                    BytesDownloaded = bytesDownloaded,
                    PercentComplete = 100,
                    BytesPerSecond = 0,
                    Result = progress.Result,
                    TotalApps = progress.TotalApps,
                    UpdatedApps = progress.UpdatedApps,
                    // Carry the cached/failed app counts so the frontend's
                    // processedApps = updatedApps + alreadyUpToDate + failedApps doesn't
                    // undercount across cached games (which never bump UpdatedApps) and
                    // "Game X of N" can't jump backward. (V4-A)
                    AlreadyUpToDate = progress.AlreadyUpToDate,
                    FailedApps = progress.FailedApps,
                    // Carry the running session total so a reconnect mid-cached-run
                    // re-hydrates the correct aggregate, not a stale value. (V4-B)
                    TotalBytesTransferred = session.TotalBytesTransferred
                };

                // Retain this app_completed snapshot as the live snapshot BEFORE broadcasting,
                // so a client that reconnects during a run of consecutive cached games (which
                // only emit app_completed ticks, no "downloading" ticks) re-hydrates the current
                // snapshot via GetCurrentPrefillProgress / subscribe-replay instead of a stale
                // "downloading" one. Bump Started -> Downloading like the normal path. (V4-B)
                session.LastProgress = frontendProgress;
                if (session.PrefillState == PrefillState.Started)
                {
                    session.PrefillState = PrefillState.Downloading;
                }

                await BroadcastToSubscribersAsync(session, EventPrefillProgress,
                    new { sessionId = session.Id, progress = frontendProgress });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete/skip prefill history entry for app {AppId}", progress.CurrentAppId);
            }
            // Update tracking: app is done, clear CurrentAppId so the "completed" handler
            // doesn't try to complete the same app again
            session.PreviousAppId = progress.CurrentAppId;
            session.PreviousAppName = progress.CurrentAppName;
            session.CurrentAppId = null;
            session.CurrentAppName = null;
            session.CurrentBytesDownloaded = 0;
            session.CurrentTotalBytes = 0;

            return; // Early return - don't process further for app_completed
        }

        // Handle overall prefill completion/failure/cancelled states
        var progressState = PrefillProgressStateExtensions.ParseOrUnknown(progress.State);
        if (progressState == PrefillProgressState.Completed
            || progressState == PrefillProgressState.Failed
            || progressState == PrefillProgressState.Error
            || progressState == PrefillProgressState.Cancelled)
        {
            if (!string.IsNullOrEmpty(session.CurrentAppId))
            {
                try
                {
                    // Determine status: Cached if no bytes on success, Failed if error, Completed otherwise
                    string status;
                    if (progressState == PrefillProgressState.Completed && session.CurrentBytesDownloaded == 0)
                    {
                        status = "Cached";
                    }
                    else if (progressState == PrefillProgressState.Failed || progressState == PrefillProgressState.Error)
                    {
                        status = "Failed";
                    }
                    else
                    {
                        status = "Completed";
                    }

                    await _sessionService.CompleteEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        session.CurrentBytesDownloaded,
                        session.CurrentTotalBytes,
                        progress.ErrorMessage);

                    _logger.LogDebug("App {Status} for {AppId} ({AppName})",
                        status, session.CurrentAppId, session.CurrentAppName);

                    // Broadcast history update
                    await BroadcastHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }

            // Route through the single idempotent terminal funnel.
            // Normalise daemon "error" → "failed" first.
            var normalised = progressState.NormaliseErrorToFailed();
            var terminalState = normalised switch
            {
                PrefillProgressState.Completed => PrefillState.Completed,
                PrefillProgressState.Cancelled => PrefillState.Cancelled,
                _ => PrefillState.Failed
            };
            await TransitionToTerminalAsync(session, terminalState);
            return; // Don't process further for terminal states
        }

        // Update previous app tracking before changing current
        session.PreviousAppId = session.CurrentAppId;
        session.PreviousAppName = session.CurrentAppName;
        session.CurrentAppId = progress.CurrentAppId;
        session.CurrentAppName = progress.CurrentAppName;

        // Calculate total bytes transferred ourselves since daemon doesn't track it
        // Use progress.TotalBytesTransferred if available, otherwise calculate from bytesDownloaded
        if (progress.TotalBytesTransferred > 0)
        {
            session.TotalBytesTransferred = progress.TotalBytesTransferred;
        }
        else
        {
            // When transitioning to a new app, add the completed app's bytes to the running total
            if (appInfoChanged && session.CurrentBytesDownloaded > 0)
            {
                session.CompletedBytesTransferred += session.CurrentBytesDownloaded;
            }
            // Total = completed games + current game progress (for real-time display)
            session.TotalBytesTransferred = session.CompletedBytesTransferred + progress.BytesDownloaded;
        }

        // Fill in the running session-level totals so the retained snapshot is self-contained
        // for re-hydration (a reconnecting client reads these straight off LastProgress).
        progress.TotalBytesTransferred = session.TotalBytesTransferred;

        // Retain the latest live snapshot on the session BEFORE broadcasting, so a client that
        // connects/refreshes/reconnects mid-prefill can immediately re-hydrate the bar
        // (GetCurrentPrefillProgress / subscribe replay) without waiting for the next tick.
        session.LastProgress = progress;
        // Advance stall-watchdog clock ONLY when bytes actually increased, so a zero-progress
        // session is detectable as stalled rather than being refreshed on every tick. Written via
        // Volatile so the cleanup-timer thread reads a torn-free tick value.
        if (session.TotalBytesTransferred > session.LastProgressBytes)
        {
            session.LastProgressBytes = session.TotalBytesTransferred;
            Volatile.Write(ref session.LastProgressTicksUtc, DateTime.UtcNow.Ticks);
        }
        if (session.PrefillState == PrefillState.Started)
        {
            session.PrefillState = PrefillState.Downloading;
        }

        // Broadcast session update to all clients on every progress (for admin pages - both hubs)
        // This ensures totalBytesTransferred updates in real-time
        var progressDto = DaemonSessionDto.FromSession(session);
        await NotifyHubAsync(EventSessionUpdated, progressDto);

        // Send detailed progress to subscribed connections (the user doing the prefill)
        await BroadcastToSubscribersAsync(session, EventPrefillProgress,
            new { sessionId = session.Id, progress });
    }

    private async Task BroadcastHistoryUpdatedAsync(string sessionId, string appId, string status)
    {
        var historyEvent = new { sessionId, appId, status };
        // Narrowed from NotifyAllDownloadsAndServiceHubAsync → NotifyAllAsync (downloads hub only).
        // Only the admin Prefill Sessions page (which subscribes via the default downloads hub)
        // consumes this event. The session-owner clients connected to the service-specific daemon
        // hub (/hubs/steam-daemon, /hubs/epic-prefill-daemon) have no handler registered for it
        // and were logging `No client method with the name 'prefillhistoryupdated' found` on every fire.
        await _notifications.NotifyAllAsync(EventPrefillHistoryUpdated, historyEvent);
    }

    private async Task NotifySessionEndedAsync(DaemonSession session, string reason)
    {
        // NotifySessionEndedAsync does NOT remove connectionId on error (session is ending anyway)
        await BroadcastToSubscribersAsync(session, EventSessionEnded,
            new { sessionId = session.Id, reason }, removeOnError: false);
    }
}
