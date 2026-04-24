using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

/// <summary>
/// Abstract base SignalR hub for prefill daemon sessions (Steam, Epic, etc.).
/// Contains all shared hub methods; subclasses provide the concrete daemon service
/// and platform-specific authorization checks.
/// Browser clients connect via session cookies; the [Authorize] attribute provides
/// the first authentication gate. OnConnectedAsync still validates admin/prefill access
/// for fine-grained authorization and group assignment.
/// </summary>
[Authorize]
public abstract class PrefillDaemonHubBase<TDaemon> : Hub where TDaemon : PrefillDaemonServiceBase
{
    protected readonly TDaemon _daemonService;
    protected readonly SessionService _sessionService;
    protected readonly ILogger _logger;

    protected PrefillDaemonHubBase(
        TDaemon daemonService,
        SessionService sessionService,
        ILogger logger)
    {
        _daemonService = daemonService;
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// Display name for log messages (e.g., "Steam daemon", "Epic prefill").
    /// </summary>
    protected abstract string HubDisplayName { get; }

    /// <summary>
    /// Returns the platform-specific prefill expiry date from the user session,
    /// e.g. session.SteamPrefillExpiresAtUtc or session.EpicPrefillExpiresAtUtc.
    /// </summary>
    protected abstract DateTime? GetPrefillExpiry(UserSession session);

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var rawToken = httpContext != null ? Security.SessionService.GetSessionTokenFromCookie(httpContext) : null;

        if (string.IsNullOrEmpty(rawToken))
        {
            _logger.LogWarning("{Hub} hub connection attempt without session from {ConnectionId}",
                HubDisplayName, Context.ConnectionId);
            Context.Abort();
            return;
        }

        var session = await _sessionService.ValidateSessionAsync(rawToken);
        var isAdmin = session?.SessionType == SessionType.Admin;
        var hasPrefillAccess = session != null && GetPrefillExpiry(session) != null && GetPrefillExpiry(session) > DateTime.UtcNow;
        if (session == null || (!isAdmin && !hasPrefillAccess))
        {
            _logger.LogWarning("{Hub} hub connection rejected - no prefill access: {ConnectionId}",
                HubDisplayName, Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items["SessionId"] = session.Id;

        _logger.LogDebug("{Hub} hub connected: {ConnectionId}, SessionId: {SessionId}",
            HubDisplayName, Context.ConnectionId, session.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _daemonService.RemoveSubscriber(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "{Hub} hub disconnected with error: {ConnectionId}",
                HubDisplayName, Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("{Hub} hub disconnected: {ConnectionId}",
                HubDisplayName, Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Creates a new daemon session and returns session info.
    /// </summary>
    public async Task<DaemonSessionDto> CreateSessionAsync()
    {
        var authSessionId = GetSessionId();
        if (!authSessionId.HasValue)
        {
            throw new HubException("Session required");
        }

        try
        {
            var httpContext = Context.GetHttpContext();
            var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request.Headers["User-Agent"].FirstOrDefault();

            _logger.LogInformation("Creating {Hub} session for auth session {SessionId}",
                HubDisplayName, authSessionId);
            var session = await _daemonService.CreateSessionAsync(authSessionId.Value, ipAddress, userAgent);

            _daemonService.AddSubscriber(session.Id, Context.ConnectionId);

            return DaemonSessionDto.FromSession(session);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create {Hub} session for auth session {SessionId}: {Message}",
                HubDisplayName, authSessionId, ex.Message);
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating {Hub} session for auth session {SessionId}",
                HubDisplayName, authSessionId);
            throw new HubException($"Failed to create {HubDisplayName} daemon session");
        }
    }

    /// <summary>
    /// Subscribes to an existing session's events.
    /// </summary>
    public async Task SubscribeToSessionAsync(string sessionId)
    {
        var authSessionId = GetSessionId();

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
        {
            throw new HubException("Session not found");
        }

        if (!authSessionId.HasValue || session.UserId != authSessionId.Value)
        {
            throw new HubException("Access denied");
        }

        _daemonService.AddSubscriber(sessionId, Context.ConnectionId);

        await Clients.Caller.SendAsync("SessionSubscribed", DaemonSessionDto.FromSession(session));
    }

    /// <summary>
    /// Starts the login process.
    /// </summary>
    public async Task<CredentialChallenge?> StartLoginAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Starting login for {Hub} session {SessionId}", HubDisplayName, sessionId);
        return await _daemonService.StartLoginAsync(sessionId, TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Provides an encrypted credential in response to a challenge.
    /// </summary>
    public async Task ProvideCredentialAsync(string sessionId, CredentialChallenge challenge, string credential)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Providing {CredentialType} credential for {Hub} session {SessionId}",
            challenge.CredentialType, HubDisplayName, sessionId);

        await _daemonService.ProvideCredentialAsync(sessionId, challenge, credential);
    }

    /// <summary>
    /// Waits for the next credential challenge.
    /// </summary>
    public async Task<CredentialChallenge?> WaitForChallengeAsync(string sessionId, int timeoutSeconds = 30)
    {
        ValidateSessionAccess(sessionId, out _);

        return await _daemonService.WaitForChallengeAsync(sessionId, TimeSpan.FromSeconds(timeoutSeconds));
    }

    /// <summary>
    /// Cancels a pending login attempt and resets auth state.
    /// </summary>
    public async Task CancelLoginAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Cancelling login for {Hub} session {SessionId}", HubDisplayName, sessionId);
        await _daemonService.CancelLoginAsync(sessionId);
    }

    /// <summary>
    /// Cancels a running prefill operation.
    /// </summary>
    public async Task CancelPrefillAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Cancelling prefill for {Hub} session {SessionId}", HubDisplayName, sessionId);
        await _daemonService.CancelPrefillAsync(sessionId);
    }

    /// <summary>
    /// Clears the temporary cache.
    /// </summary>
    public async Task<ClearCacheResult> ClearCacheAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Clearing cache for {Hub} session {SessionId}", HubDisplayName, sessionId);

        return await _daemonService.ClearCacheAsync(sessionId);
    }

    /// <summary>
    /// Gets selected apps status with download sizes.
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(string sessionId, List<string>? operatingSystems = null)
    {
        ValidateSessionAccess(sessionId, out _);

        return await _daemonService.GetSelectedAppsStatusAsync(sessionId, operatingSystems);
    }

    /// <summary>
    /// Terminates a session immediately (force kill).
    /// </summary>
    public async Task EndSessionAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Force ending {Hub} session {SessionId}", HubDisplayName, sessionId);
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
            Status = session.LastPrefillStatus ?? PrefillProgressState.Unknown.ToWireString(),
            CompletedAt = session.LastPrefillCompletedAt.Value,
            DurationSeconds = session.LastPrefillDurationSeconds ?? 0
        };
    }

    /// <summary>
    /// Gets all sessions for the current user.
    /// </summary>
    public IEnumerable<DaemonSessionDto> GetMySessions()
    {
        var authSessionId = GetSessionId();
        if (!authSessionId.HasValue)
        {
            return Enumerable.Empty<DaemonSessionDto>();
        }

        return _daemonService.GetUserSessions(authSessionId.Value)
            .Select(DaemonSessionDto.FromSession);
    }

    /// <summary>
    /// Returns the authenticated UserSession.Id stored in Context.Items during OnConnectedAsync.
    /// This is the Guid user-auth session id (not the 16-char daemon-local session id).
    /// </summary>
    protected Guid? GetSessionId()
    {
        return Context.Items.TryGetValue("SessionId", out var id) && id is Guid g ? g : null;
    }

    protected void ValidateSessionAccess(string sessionId, out DaemonSession session)
    {
        var authSessionId = GetSessionId();

        var s = _daemonService.GetSession(sessionId);
        if (s == null)
        {
            throw new HubException("Session not found");
        }

        if (!authSessionId.HasValue || s.UserId != authSessionId.Value)
        {
            throw new HubException("Access denied");
        }

        session = s;
    }
}
