using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Steam daemon sessions.
/// Only admin sessions can connect.
/// </summary>
public class SteamDaemonHub : Hub
{
    private readonly SteamDaemonService _daemonService;
    private readonly ISteamAuthStorageService _steamAuthStorage;
    private readonly SessionService _sessionService;
    private readonly ILogger<SteamDaemonHub> _logger;

    public SteamDaemonHub(
        SteamDaemonService daemonService,
        ISteamAuthStorageService steamAuthStorage,
        SessionService sessionService,
        ILogger<SteamDaemonHub> logger)
    {
        _daemonService = daemonService;
        _steamAuthStorage = steamAuthStorage;
        _sessionService = sessionService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var rawToken = httpContext != null ? SessionService.GetSessionTokenFromCookie(httpContext) : null;

        if (string.IsNullOrEmpty(rawToken))
        {
            _logger.LogWarning("Steam daemon hub connection attempt without session from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var session = await _sessionService.ValidateSessionAsync(rawToken);
        var isAdmin = session?.SessionType == "admin";
        var hasPrefillAccess = session?.PrefillExpiresAtUtc != null && session.PrefillExpiresAtUtc > DateTime.UtcNow;
        if (session == null || (!isAdmin && !hasPrefillAccess))
        {
            _logger.LogWarning("Steam daemon hub connection rejected - no prefill access: {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        // Store session ID in connection items for later use
        Context.Items["SessionId"] = session.Id.ToString();

        _logger.LogDebug("Steam daemon hub connected: {ConnectionId}, SessionId: {SessionId}", Context.ConnectionId, session.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _daemonService.RemoveSubscriber(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "Steam daemon hub disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("Steam daemon hub disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Creates a new daemon session and returns session info
    /// </summary>
    public async Task<DaemonSessionDto> CreateSession()
    {
        var authSessionId = GetSessionId();
        if (string.IsNullOrEmpty(authSessionId))
        {
            throw new HubException("Session required");
        }

        try
        {
            var httpContext = Context.GetHttpContext();
            var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request.Headers["User-Agent"].FirstOrDefault();

            _logger.LogInformation("Creating daemon session for auth session {SessionId}", authSessionId);
            var session = await _daemonService.CreateSessionAsync(authSessionId, ipAddress, userAgent);

            // Subscribe this connection to session events
            _daemonService.AddSubscriber(session.Id, Context.ConnectionId);

            return DaemonSessionDto.FromSession(session);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create session for auth session {SessionId}: {Message}", authSessionId, ex.Message);
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating session for auth session {SessionId}", authSessionId);
            throw new HubException("Failed to create daemon session");
        }
    }

    /// <summary>
    /// Subscribes to an existing session's events
    /// </summary>
    public async Task SubscribeToSession(string sessionId)
    {
        var authSessionId = GetSessionId();

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            throw new HubException("Session not found");
        }

        if (session.UserId != authSessionId)
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
    public async Task SetSelectedApps(string sessionId, List<string> appIds)
    {
        ValidateSessionAccess(sessionId, out var session);

        _logger.LogInformation("SetSelectedApps called for session {SessionId} with {Count} app IDs: [{AppIds}]",
            sessionId, appIds?.Count ?? 0, appIds != null ? string.Join(", ", appIds.Take(10)) + (appIds.Count > 10 ? "..." : "") : "null");

        await _daemonService.SetSelectedAppsAsync(sessionId, appIds ?? new List<string>());

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
        var authSessionId = GetSessionId();
        if (string.IsNullOrEmpty(authSessionId))
        {
            return Enumerable.Empty<DaemonSessionDto>();
        }

        return _daemonService.GetUserSessions(authSessionId)
            .Select(DaemonSessionDto.FromSession);
    }

    private string? GetSessionId()
    {
        return Context.Items.TryGetValue("SessionId", out var id) ? id as string : null;
    }

    private void ValidateSessionAccess(string sessionId, out DaemonSession session)
    {
        var authSessionId = GetSessionId();

        var s = _daemonService.GetSession(sessionId);
        if (s == null)
        {
            throw new HubException("Session not found");
        }

        if (s.UserId != authSessionId)
        {
            throw new HubException("Access denied");
        }

        session = s;
    }
}
