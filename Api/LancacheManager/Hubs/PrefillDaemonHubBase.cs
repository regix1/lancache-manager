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
        var rawToken = httpContext != null ? Security.SessionService.TokenFromCookie(httpContext) : null;

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

            // Resolve admin vs guest so guest/temporary containers get the manager-enforced lifetime
            // cap. Re-validate from the cookie token (mirrors OnConnectedAsync); a session that cannot
            // be resolved is treated as a guest so the cap is applied conservatively.
            var rawToken = httpContext != null ? Security.SessionService.TokenFromCookie(httpContext) : null;
            var userSession = string.IsNullOrEmpty(rawToken) ? null : await _sessionService.ValidateSessionAsync(rawToken);
            var sessionType = userSession == null ? SessionType.Guest : userSession.SessionType;

            _logger.LogInformation("Creating {Hub} session for auth session {SessionId} (type {SessionType})",
                HubDisplayName, authSessionId, sessionType);
            var session = await _daemonService.CreateSessionAsync(authSessionId.Value, ipAddress, userAgent, sessionType);

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

        await Clients.Caller.SendAsync(SignalREvents.SessionSubscribed, DaemonSessionDto.FromSession(session));

        // Re-hydration: if a prefill is in flight, replay the retained live progress snapshot to
        // THIS caller only (reuses the existing PrefillProgress event; no new event; no
        // double-broadcast to other subscribers) so the bar binds without waiting for the next tick.
        await _daemonService.ReplayProgressAsync(sessionId, Context.ConnectionId);
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

        try
        {
            await _daemonService.ProvideCredentialAsync(sessionId, challenge, credential);
        }
        catch (DaemonCredentialRejectedException ex)
        {
            // A guest/mapping-flow session is never adopted or replaced (each login is its own
            // standalone session), so a rejected credential here can't be the RC3 cross-session leak
            // that this exception exists to catch on the persistent flow - it is a redundant, already-
            // resolved credential (e.g. the device-confirmation "confirm" ack, which this event-driven
            // flow can deliver more than once for the same challenge). Log and swallow rather than
            // failing the whole login over a harmless duplicate submission.
            _logger.LogWarning(ex,
                "Ignoring rejected {CredentialType} credential for {Hub} session {SessionId} - likely a redundant resend",
                challenge.CredentialType, HubDisplayName, sessionId);
        }
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
    /// Gets the live prefill progress snapshot for a session.
    /// Used by clients to re-hydrate the progress bar after connect / reconnect / tab-return
    /// while a prefill is still running. Returns the retained <see cref="PrefillProgress"/>
    /// snapshot when the session is prefilling, else null. Mirrors the owned-session check
    /// and shape of <see cref="GetLastPrefillResult"/>. This is a hub INVOKE (not a broadcast
    /// event), so it is intentionally absent from SIGNALR_EVENTS.
    /// </summary>
    public PrefillProgress? GetCurrentPrefillProgress(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        // Snapshot the (IsPrefilling, LastProgress) pair into locals with single reads each.
        // These two fields are written on the daemon-event thread (NotifyPrefillProgressAsync /
        // TransitionToTerminalAsync) and read here on the hub-invoke thread with no lock. Reading
        // each once means this call returns a self-consistent pair (never IsPrefilling==true with a
        // LastProgress that a concurrent terminal transition has already nulled). A torn read just
        // self-heals on the next tick, and the frontend already handles a null snapshot. (V9)
        var isPrefilling = session.IsPrefilling;
        var snapshot = session.LastProgress;

        if (!isPrefilling)
        {
            return null;
        }

        return snapshot;
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
