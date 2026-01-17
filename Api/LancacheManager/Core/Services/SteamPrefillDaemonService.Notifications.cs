using System;
using System.Linq;
using System.Threading.Tasks;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Core.Services;

public partial class SteamPrefillDaemonService
{
#region Socket Event Handlers

    /// <summary>
    /// Handles credential challenge events from socket communication.
    /// </summary>
    private async Task HandleSocketCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
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
    private async Task HandleStatusChangeFromSocketAsync(DaemonSession session, DaemonStatus status)
    {
        try
        {
            var previousAuthState = session.AuthState;

            // Update auth state based on status
            session.AuthState = status.Status switch
            {
                "awaiting-login" => DaemonAuthState.NotAuthenticated,
                "logged-in" => DaemonAuthState.Authenticated,
                _ => session.AuthState
            };

            if (session.AuthState != previousAuthState)
            {
                await NotifyAuthStateChangeAsync(session);
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
    private async Task HandleProgressChangeFromSocketAsync(DaemonSession session, SocketPrefillProgress socketProgress)
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

    private async Task NotifyAuthStateChangeAsync(DaemonSession session)
    {
        _logger.LogInformation("NotifyAuthStateChangeAsync: Sending AuthStateChanged ({State}) to {Count} connections for session {SessionId}",
            session.AuthState, session.SubscribedConnections.Count, session.Id);
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            _logger.LogDebug("NotifyAuthStateChangeAsync: Sending to connection {ConnectionId}", connectionId);
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("AuthStateChanged", session.Id, session.AuthState.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify auth state to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyCredentialChallengeAsync(DaemonSession session, CredentialChallenge challenge)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("CredentialChallenge", session.Id, challenge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify credential challenge to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyStatusChangeAsync(DaemonSession session, DaemonStatus status)
    {
        _logger.LogInformation("NotifyStatusChangeAsync: Sending StatusChanged ({Status}) to {Count} connections for session {SessionId}",
            status.Status, session.SubscribedConnections.Count, session.Id);
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            _logger.LogDebug("NotifyStatusChangeAsync: Sending to connection {ConnectionId}", connectionId);
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("StatusChanged", session.Id, status);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify status to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyPrefillStateChangeAsync(DaemonSession session, string state)
    {
        int? durationSeconds = null;

        // For completion states, calculate duration and store the result for background detection
        if (state == "completed" || state == "failed" || state == "cancelled")
        {
            if (session.PrefillStartedAt.HasValue)
            {
                durationSeconds = (int)(DateTime.UtcNow - session.PrefillStartedAt.Value).TotalSeconds;
            }

            // Store the last prefill result for clients that were disconnected during prefill
            session.LastPrefillCompletedAt = DateTime.UtcNow;
            session.LastPrefillDurationSeconds = durationSeconds;
            session.LastPrefillStatus = state;

            _logger.LogInformation("Prefill {State} for session {SessionId}, duration: {Duration}s",
                state, session.Id, durationSeconds ?? 0);
        }
        else if (state == "started")
        {
            // Track when prefill started for duration calculation
            session.PrefillStartedAt = DateTime.UtcNow;
            // Clear any previous completion info
            session.LastPrefillCompletedAt = null;
            session.LastPrefillDurationSeconds = null;
            session.LastPrefillStatus = null;
        }

        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("PrefillStateChanged", session.Id, state, durationSeconds);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify prefill state to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task NotifyPrefillProgressAsync(DaemonSession session, PrefillProgress progress)
    {
        // Update session's current app info for admin visibility
        var appInfoChanged = session.CurrentAppId != progress.CurrentAppId ||
                             session.CurrentAppName != progress.CurrentAppName;

        // Track history: detect game transitions
        if (appInfoChanged && progress.CurrentAppId > 0)
        {
            // If there was an app being prefilled, complete its history entry
            // Use the STORED bytes (from before the transition), not progress bytes (which are for the new app)
            if (session.CurrentAppId > 0)
            {
                try
                {
                    // If no bytes were downloaded, mark as Cached
                    var status = session.CurrentBytesDownloaded == 0 ? "Cached" : "Completed";

                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        session.CurrentBytesDownloaded,
                        session.CurrentTotalBytes);

                    _logger.LogInformation("App {Status} in session {SessionId}: {AppId} ({AppName}) - {Bytes}/{Total} bytes",
                        status, session.Id, session.CurrentAppId, session.CurrentAppName,
                        session.CurrentBytesDownloaded, session.CurrentTotalBytes);

                    // Broadcast history update
                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }

            // Start a new history entry for the current app
            try
            {
                var entry = await _sessionService.StartPrefillEntryAsync(session.Id, progress.CurrentAppId, progress.CurrentAppName);

                // Only broadcast if an entry was actually created (won't create if recently completed)
                if (entry != null)
                {
                    _logger.LogDebug("Started prefill history for app {AppId} ({AppName}) in session {SessionId}",
                        progress.CurrentAppId, progress.CurrentAppName, session.Id);

                    // Broadcast history update
                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, progress.CurrentAppId, "InProgress");
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
        if (progress.CurrentAppId > 0)
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
        if (progress.State == "app_completed" && progress.CurrentAppId > 0)
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

                await _sessionService.CompletePrefillEntryAsync(
                    session.Id,
                    progress.CurrentAppId,
                    status,
                    bytesDownloaded,
                    totalBytes);

                _logger.LogInformation("App {Status} ({Result}): {AppId} ({AppName}) - {Bytes}/{Total} bytes",
                    status, progress.Result, progress.CurrentAppId, progress.CurrentAppName,
                    bytesDownloaded, totalBytes);

                // Broadcast history update
                await BroadcastPrefillHistoryUpdatedAsync(session.Id, progress.CurrentAppId, status);

                // Record cached depots for successful downloads (including AlreadyUpToDate)
                // This allows us to skip re-downloading games that are already cached
                if (progress.Result is "Success" or "AlreadyUpToDate" && progress.Depots != null && progress.Depots.Count > 0)
                {
                    try
                    {
                        await _cacheService.RecordCachedDepotsAsync(
                            progress.CurrentAppId,
                            progress.CurrentAppName,
                            progress.Depots.Select(d => (d.DepotId, d.ManifestId, d.TotalBytes)),
                            session.SteamUsername);
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
                    State = isCached ? "already_cached" : "app_completed",
                    CurrentAppId = progress.CurrentAppId,
                    CurrentAppName = progress.CurrentAppName,
                    TotalBytes = totalBytes,
                    BytesDownloaded = bytesDownloaded,
                    PercentComplete = 100,
                    BytesPerSecond = 0,
                    Result = progress.Result,
                    TotalApps = progress.TotalApps,
                    UpdatedApps = progress.UpdatedApps
                };

                foreach (var connectionId in session.SubscribedConnections.ToList())
                {
                    try
                    {
                        await _hubContext.Clients.Client(connectionId)
                            .SendAsync("PrefillProgress", session.Id, frontendProgress);
                    }
                    catch (Exception notifyEx)
                    {
                        _logger.LogWarning(notifyEx, "Failed to notify app completion to {ConnectionId}", connectionId);
                        session.SubscribedConnections.Remove(connectionId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to complete/skip prefill history entry for app {AppId}", progress.CurrentAppId);
            }
            // Update tracking for completed app
            session.PreviousAppId = session.CurrentAppId;
            session.PreviousAppName = session.CurrentAppName;
            session.CurrentAppId = progress.CurrentAppId;
            session.CurrentAppName = progress.CurrentAppName;
            // Reset bytes for next app
            session.CurrentBytesDownloaded = 0;
            session.CurrentTotalBytes = 0;

            return; // Early return - don't process further for app_completed
        }

        // Handle overall prefill completion/failure/cancelled states
        if (progress.State == "completed" || progress.State == "failed" || progress.State == "error" || progress.State == "cancelled")
        {
            if (session.CurrentAppId > 0)
            {
                try
                {
                    // Determine status: Cached if no bytes on success, Failed if error, Completed otherwise
                    string status;
                    if (progress.State == "completed" && session.CurrentBytesDownloaded == 0)
                    {
                        status = "Cached";
                    }
                    else if (progress.State == "failed" || progress.State == "error")
                    {
                        status = "Failed";
                    }
                    else
                    {
                        status = "Completed";
                    }

                    await _sessionService.CompletePrefillEntryAsync(
                        session.Id,
                        session.CurrentAppId,
                        status,
                        session.CurrentBytesDownloaded,
                        session.CurrentTotalBytes,
                        progress.ErrorMessage);

                    _logger.LogDebug("App {Status} for {AppId} ({AppName})",
                        status, session.CurrentAppId, session.CurrentAppName);

                    // Broadcast history update
                    await BroadcastPrefillHistoryUpdatedAsync(session.Id, session.CurrentAppId, status);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to complete prefill history entry for app {AppId}", session.CurrentAppId);
                }
            }

            // Notify frontend of prefill state change (completed/failed)
            var notifyState = progress.State == "error" ? "failed" : progress.State;
            await NotifyPrefillStateChangeAsync(session, notifyState);
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

        // Broadcast session update to all clients on every progress (for admin pages - both hubs)
        // This ensures totalBytesTransferred updates in real-time
        var progressDto = DaemonSessionDto.FromSession(session);
        await _hubContext.Clients.All.SendAsync("DaemonSessionUpdated", progressDto);
        await _downloadHubContext.Clients.All.SendAsync("DaemonSessionUpdated", progressDto);

        // Send detailed progress to subscribed connections (the user doing the prefill)
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("PrefillProgress", session.Id, progress);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to notify prefill progress to {ConnectionId}", connectionId);
                session.SubscribedConnections.Remove(connectionId);
            }
        }
    }

    private async Task BroadcastPrefillHistoryUpdatedAsync(string sessionId, uint appId, string status)
    {
        var historyEvent = new { sessionId, appId, status };
        await _hubContext.Clients.All.SendAsync("PrefillHistoryUpdated", historyEvent);
        await _downloadHubContext.Clients.All.SendAsync("PrefillHistoryUpdated", historyEvent);
    }

    private async Task NotifySessionEndedAsync(DaemonSession session, string reason)
    {
        foreach (var connectionId in session.SubscribedConnections.ToList())
        {
            try
            {
                await _hubContext.Clients.Client(connectionId)
                    .SendAsync("SessionEnded", session.Id, reason);
            }
            catch
            {
                // Ignore
            }
        }
    }

    }
