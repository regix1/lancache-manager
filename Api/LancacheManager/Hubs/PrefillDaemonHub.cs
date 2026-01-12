using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Steam Prefill daemon sessions.
/// Provides real-time updates for authentication state changes and prefill progress.
/// Uses secure encrypted credential exchange.
///
/// Authorization: Allows authenticated users OR guests with prefill permission.
/// </summary>
public class PrefillDaemonHub : Hub
{
    private readonly SteamPrefillDaemonService _daemonService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly GuestSessionService _guestSessionService;
    private readonly ILogger<PrefillDaemonHub> _logger;

    public PrefillDaemonHub(
        SteamPrefillDaemonService daemonService,
        DeviceAuthService deviceAuthService,
        GuestSessionService guestSessionService,
        ILogger<PrefillDaemonHub> logger)
    {
        _daemonService = daemonService;
        _deviceAuthService = deviceAuthService;
        _guestSessionService = guestSessionService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning("Prefill daemon hub connection attempt without device ID from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        // Check if user has prefill access (authenticated OR guest with prefill permission)
        if (!HasPrefillAccess(deviceId))
        {
            _logger.LogWarning("Unauthorized prefill daemon hub connection attempt from {ConnectionId}, DeviceId: {DeviceId}", Context.ConnectionId, deviceId);
            Context.Abort();
            return;
        }

        _logger.LogDebug("Prefill daemon hub connected: {ConnectionId}, DeviceId: {DeviceId}", Context.ConnectionId, deviceId);
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Checks if the user has prefill access (authenticated OR guest with prefill permission)
    /// </summary>
    private bool HasPrefillAccess(string deviceId)
    {
        // Authenticated users always have access
        if (_deviceAuthService.ValidateDevice(deviceId))
        {
            return true;
        }

        // Check if guest has prefill permission
        var guestSession = _guestSessionService.GetSessionByDeviceId(deviceId);
        if (guestSession != null)
        {
            var (isValid, _) = _guestSessionService.ValidateSessionWithReason(deviceId);
            if (isValid && guestSession.PrefillEnabled && !guestSession.IsPrefillExpired)
            {
                _logger.LogDebug("Guest with prefill permission granted hub access for device {DeviceId}", deviceId);
                return true;
            }
        }

        return false;
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _daemonService.RemoveSubscriber(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "Prefill daemon hub disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("Prefill daemon hub disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Creates a new daemon session and returns session info
    /// </summary>
    public async Task<DaemonSessionDto> CreateSession()
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            throw new HubException("Device ID required");
        }

        try
        {
            var httpContext = Context.GetHttpContext();
            var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request.Headers["User-Agent"].FirstOrDefault();

            _logger.LogInformation("Creating daemon session for device {DeviceId}", deviceId);
            var session = await _daemonService.CreateSessionAsync(deviceId, ipAddress, userAgent);

            // Subscribe this connection to session events
            _daemonService.AddSubscriber(session.Id, Context.ConnectionId);

            return DaemonSessionDto.FromSession(session);
        }
        catch (InvalidOperationException ex)
        {
            // Log clean message without stack trace for expected errors (e.g., Docker not available)
            _logger.LogWarning("Failed to create session for device {DeviceId}: {Message}", deviceId, ex.Message);
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating session for device {DeviceId}", deviceId);
            throw new HubException("Failed to create daemon session");
        }
    }

    /// <summary>
    /// Subscribes to an existing session's events
    /// </summary>
    public async Task SubscribeToSession(string sessionId)
    {
        var deviceId = GetDeviceId();

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            throw new HubException("Session not found");
        }

        if (session.UserId != deviceId)
        {
            throw new HubException("Access denied");
        }

        _daemonService.AddSubscriber(sessionId, Context.ConnectionId);

        // Send current state
        await Clients.Caller.SendAsync("SessionSubscribed", DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Starts the login process
    /// </summary>
    public async Task<CredentialChallenge?> StartLogin(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Starting login for session {SessionId}", sessionId);
        return await _daemonService.StartLoginAsync(sessionId, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge
    /// </summary>
    public async Task ProvideCredential(string sessionId, CredentialChallenge challenge, string credential)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Providing {CredentialType} credential for session {SessionId}",
            challenge.CredentialType, sessionId);

        await _daemonService.ProvideCredentialAsync(sessionId, challenge, credential);
    }

    /// <summary>
    /// Waits for the next credential challenge
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallenge(string sessionId, int timeoutSeconds = 30)
    {
        ValidateSessionAccess(sessionId, out var session);

        return await _daemonService.WaitForChallengeAsync(sessionId, TimeSpan.FromSeconds(timeoutSeconds));
    }

    /// <summary>
    /// Cancels a pending login attempt and resets auth state
    /// </summary>
    public async Task CancelLogin(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Cancelling login for session {SessionId}", sessionId);
        await _daemonService.CancelLoginAsync(sessionId);
    }

    /// <summary>
    /// Cancels a running prefill operation
    /// </summary>
    public async Task CancelPrefill(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Cancelling prefill for session {SessionId}", sessionId);
        await _daemonService.CancelPrefillAsync(sessionId);
    }

    /// <summary>
    /// Gets the daemon status for a session
    /// </summary>
    public async Task<DaemonStatus?> GetStatus(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        return await _daemonService.GetSessionStatusAsync(sessionId);
    }

    /// <summary>
    /// Gets owned games for a logged-in session
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGames(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        return await _daemonService.GetOwnedGamesAsync(sessionId);
    }

    /// <summary>
    /// Sets selected apps for prefill
    /// </summary>
    public async Task SetSelectedApps(string sessionId, List<uint> appIds)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("SetSelectedApps called for session {SessionId} with {Count} app IDs: [{AppIds}]",
            sessionId, appIds?.Count ?? 0, appIds != null ? string.Join(", ", appIds.Take(10)) + (appIds.Count > 10 ? "..." : "") : "null");

        await _daemonService.SetSelectedAppsAsync(sessionId, appIds ?? new List<uint>());

        _logger.LogInformation("SetSelectedApps completed for session {SessionId}", sessionId);
    }

    /// <summary>
    /// Starts a prefill operation
    /// </summary>
    public async Task<PrefillResult> StartPrefill(string sessionId, bool all = false, bool recent = false, bool force = false, string? operatingSystems = null)
    {
        try
        {
            ValidateSessionAccess(sessionId, out var session);

            // Parse comma-separated OS string into list
            List<string>? osList = null;
            if (!string.IsNullOrEmpty(operatingSystems))
            {
                osList = operatingSystems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            _logger.LogInformation("Starting prefill for session {SessionId} (all={All}, recent={Recent}, force={Force}, os={OS})",
                sessionId, all, recent, force, operatingSystems ?? "default");

            return await _daemonService.PrefillAsync(sessionId, all: all, recent: recent, force: force, operatingSystems: osList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartPrefill failed for session {SessionId}", sessionId);
            throw;
        }
    }

    /// <summary>
    /// Clears the temporary cache
    /// </summary>
    public async Task<ClearCacheResult> ClearCache(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Clearing cache for session {SessionId}", sessionId);

        return await _daemonService.ClearCacheAsync(sessionId);
    }

    /// <summary>
    /// Gets cache info
    /// </summary>
    public async Task<ClearCacheResult> GetCacheInfo(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        return await _daemonService.GetCacheInfoAsync(sessionId);
    }

    /// <summary>
    /// Gets selected apps status with download sizes
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatus(string sessionId, List<string>? operatingSystems = null)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Getting selected apps status for session {SessionId} with OS: {OperatingSystems}",
            sessionId, operatingSystems != null ? string.Join(",", operatingSystems) : "all");

        return await _daemonService.GetSelectedAppsStatusAsync(sessionId, operatingSystems);
    }

    /// <summary>
    /// Checks cached games against Steam's current manifests and removes outdated entries.
    /// Should be called after successful authentication to ensure cache status is accurate.
    /// </summary>
    /// <returns>Number of outdated cache entries removed</returns>
    public async Task<int> CheckAndUpdateCacheStatus(string sessionId, List<string>? operatingSystems = null)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Checking cache status for session {SessionId}", sessionId);

        return await _daemonService.CheckAndUpdateCacheStatusAsync(sessionId, operatingSystems);
    }

    /// <summary>
    /// Terminates a session immediately (force kill)
    /// </summary>
    public async Task EndSession(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("Force ending session {SessionId}", sessionId);
        // Use force=true for immediate termination without waiting
        await _daemonService.TerminateSessionAsync(sessionId, "User ended session", force: true);
    }

    /// <summary>
    /// Gets session info
    /// </summary>
    public DaemonSessionDto? GetSessionInfo(string sessionId)
    {
        var session = _daemonService.GetSession(sessionId);
        return session != null ? DaemonSessionDto.FromSession(session) : null;
    }

    /// <summary>
    /// Gets the last prefill result for a session.
    /// Used by clients to check if prefill completed while they were disconnected.
    /// </summary>
    public LastPrefillResultDto? GetLastPrefillResult(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        if (!session.LastPrefillCompletedAt.HasValue)
        {
            return null;
        }

        return new LastPrefillResultDto
        {
            Status = session.LastPrefillStatus ?? "unknown",
            CompletedAt = session.LastPrefillCompletedAt.Value,
            DurationSeconds = session.LastPrefillDurationSeconds ?? 0
        };
    }

    /// <summary>
    /// Gets all sessions for the current user
    /// </summary>
    public IEnumerable<DaemonSessionDto> GetMySessions()
    {
        var deviceId = GetDeviceId();
        if (string.IsNullOrEmpty(deviceId))
        {
            return Enumerable.Empty<DaemonSessionDto>();
        }

        return _daemonService.GetUserSessions(deviceId)
            .Select(DaemonSessionDto.FromSession);
    }

    private string? GetDeviceId()
    {
        return Context.GetHttpContext()?.Request.Query["deviceId"].FirstOrDefault();
    }

    private void ValidateSessionAccess(string sessionId, out DaemonSession session)
    {
        var deviceId = GetDeviceId();

        var s = _daemonService.GetSession(sessionId);
        if (s == null)
        {
            throw new HubException("Session not found");
        }

        if (s.UserId != deviceId)
        {
            throw new HubException("Access denied");
        }

        session = s;
    }
}
