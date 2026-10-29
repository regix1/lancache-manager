using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Middleware;
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
        var isAccountHolder = session is not null && _sessionService.CanManage(session);
        var hasPrefillAccess = session != null && GetPrefillExpiry(session) != null && GetPrefillExpiry(session) > DateTime.UtcNow;
        if (session == null || (!isAccountHolder && !hasPrefillAccess))
        {
            _logger.LogWarning("{Hub} hub connection rejected - no prefill access: {ConnectionId}",
                HubDisplayName, Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items["SessionId"] = session.Id;
        Context.Items["IsAccountHolder"] = isAccountHolder;

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
    /// Creates a new daemon session and returns session info. A page still open from the previous version calls
    /// this and stores the answer as its session, so it keeps that answer and refuses with a hub error; current
    /// pages call <see cref="OpenSessionAsync"/>.
    /// </summary>
    public async Task<DaemonSessionDto> CreateSessionAsync()
    {
        var authSessionId = GetSessionId() ?? throw new HubException("Session required");
        try
        {
            return await CreateSubscribedSessionAsync(authSessionId);
        }
        catch (Exception ex) when (ex is ApiException or InvalidOperationException or DockerTimeoutException)
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
    /// Creates a new daemon session and answers it, or the key and values of the refusal.
    /// </summary>
    public async Task<CreateSessionResult> OpenSessionAsync()
    {
        var authSessionId = GetSessionId() ?? throw new HubException("Session required");
        try
        {
            return new CreateSessionResult { Session = await CreateSubscribedSessionAsync(authSessionId) };
        }
        // A hub error carries only a sentence, so a keyed failure, a keyed refusal such as a ban among them, is
        // answered with its key and values for the guest's own language.
        catch (ApiException ex) when (ex.StageKey is not null)
        {
            _logger.LogWarning("Failed to create {Hub} session for auth session {SessionId}: {Message}",
                HubDisplayName, authSessionId, ex.Message);
            return new CreateSessionResult { StageKey = ex.StageKey, Context = ex.Context };
        }
        // The HTTP routes answer the same timeout with this key.
        catch (DockerTimeoutException ex)
        {
            _logger.LogWarning("Failed to create {Hub} session for auth session {SessionId}: {Message}",
                HubDisplayName, authSessionId, ex.Message);
            return new CreateSessionResult { StageKey = "errors.prefill.dockerUnavailable" };
        }
        // A failure with no key of its own is answered with the key the HTTP routes give the same failure.
        catch (Exception ex) when (ex is InvalidOperationException or ServiceUnavailableException)
        {
            _logger.LogWarning("Failed to create {Hub} session for auth session {SessionId}: {Message}",
                HubDisplayName, authSessionId, ex.Message);
            return new CreateSessionResult { StageKey = "errors.http.unexpected" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating {Hub} session for auth session {SessionId}",
                HubDisplayName, authSessionId);
            return new CreateSessionResult { StageKey = "errors.http.unexpected" };
        }
    }

    // Creates the session, checks the caller's access again once it is up, and subscribes the caller to it.
    private async Task<DaemonSessionDto> CreateSubscribedSessionAsync(Guid authSessionId)
    {
        var httpContext = Context.GetHttpContext();
        var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
        var userAgent = httpContext?.Request.Headers["User-Agent"].FirstOrDefault();

        var rawToken = httpContext != null ? Security.SessionService.TokenFromCookie(httpContext) : null;
        var userSession = string.IsNullOrEmpty(rawToken) ? null : await _sessionService.ValidateSessionAsync(rawToken);
        if (userSession == null || userSession.Id != authSessionId
            || (!_sessionService.CanManage(userSession) && !(GetPrefillExpiry(userSession) > DateTime.UtcNow)))
            throw new ForbiddenException("Session access is no longer valid.") { StageKey = "errors.prefill.accessEnded" };
        var sessionType = userSession.SessionType;

        _logger.LogInformation("Creating {Hub} session for auth session {SessionId} (type {SessionType})",
            HubDisplayName, authSessionId, sessionType);
        var session = await _daemonService.CreateSessionAsync(authSessionId, ipAddress, userAgent, sessionType);

        var current = await _sessionService.GetSessionByIdAsync(authSessionId);
        if (current == null || current.IsRevoked || current.ExpiresAtUtc <= DateTime.UtcNow
            || current.SessionType != sessionType
            || (!_sessionService.CanManage(current) && !(GetPrefillExpiry(current) > DateTime.UtcNow))
            || !ReferenceEquals(_daemonService.GetSession(session.Id), session))
        {
            try
            {
                await _daemonService.TerminateSessionAsync(session.Id, "Session access is no longer valid", force: true,
                    reasonKey: PrefillDaemonServiceBase.AccessEndedReasonKey);
            }
            catch (Exception ex)
            {
                // The refusal below is the reason the guest sees; the unfinished removal stays registered for a retry.
                _logger.LogWarning(ex, "Could not remove the {Hub} session {SessionId} of an ended access", HubDisplayName, session.Id);
            }
            throw new ForbiddenException("Session access is no longer valid.") { StageKey = "errors.prefill.accessEnded" };
        }

        _daemonService.AddSubscriber(session.Id, Context.ConnectionId);

        return DaemonSessionDto.FromSession(session);
    }

    /// <summary>
    /// Subscribes to an existing session's events.
    /// </summary>
    public async Task SubscribeToSessionAsync(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session, allowPersistentAccountHolder: true);

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
            // standalone session), so a rejected credential here can't be the cross-session leak
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
    /// Cancels the session's current sign-in and resets auth state. Returns false when the sign-in had already finished
    /// signed in. Its one argument is what a browser still running an earlier version of the app sends.
    /// </summary>
    public Task<bool> CancelLoginAsync(string sessionId) => CancelLoginAttemptAsync(sessionId, null);

    /// <summary>
    /// Cancels the sign-in <paramref name="loginAttempt"/> names, so a stale dialog's timeout never ends a newer sign-in.
    /// Returns false when that attempt is no longer the session's current one or had already finished signed in.
    /// </summary>
    public async Task<bool> CancelLoginAttemptAsync(string sessionId, long? loginAttempt)
    {
        ValidateSessionAccess(sessionId, out _);

        _logger.LogInformation("Cancelling login for {Hub} session {SessionId}", HubDisplayName, sessionId);
        return await _daemonService.CancelLoginAsync(sessionId, loginAttempt: loginAttempt);
    }

    public async Task CancelPrefillRunAsync(string sessionId, Guid runId)
    {
        ValidateSessionAccess(sessionId, out _);
        await _daemonService.CancelPrefillRunAsync(sessionId, runId);
    }

    public IReadOnlyList<DaemonRunStatus> GetPrefillRuns(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _, allowPersistentAccountHolder: true);
        return _daemonService.GetRuns(sessionId);
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
    /// Terminates a session immediately (force kill). A removal the daemon host could not finish answers
    /// the key of the failure, because a hub error carries only a sentence; a page from the previous release
    /// ignores the answer.
    /// </summary>
    public async Task<EndSessionResult> EndSessionAsync(string sessionId)
    {
        // A session whose teardown failed has left the live list, so the caller's own guest session is
        // also found among the unfinished teardowns and pressing End session again reruns it.
        var authSessionId = GetSessionId();
        var session = _daemonService.GetAllSessions(includeTerminating: true)
            .FirstOrDefault((DaemonSession candidate) => candidate.Id == sessionId);
        if (session == null)
        {
            throw new HubException("Session not found");
        }
        if (session.IsPersistent || !authSessionId.HasValue || session.UserId != authSessionId.Value)
        {
            throw new HubException("Access denied");
        }

        _logger.LogInformation("Force ending {Hub} session {SessionId}", HubDisplayName, sessionId);
        try
        {
            await _daemonService.TerminateSessionAsync(sessionId, "User ended session", force: true,
                reasonKey: PrefillDaemonServiceBase.EndedByUserReasonKey);
            return new EndSessionResult();
        }
        catch (ServiceUnavailableException ex)
        {
            return new EndSessionResult { StageKey = ex.StageKey, Context = ex.Context };
        }
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

    protected void ValidateSessionAccess(
        string sessionId,
        out DaemonSession session,
        bool allowPersistentAccountHolder = false)
    {
        var authSessionId = GetSessionId();

        var s = _daemonService.GetSession(sessionId);
        if (s == null)
        {
            throw new HubException("Session not found");
        }

        var isAccountHolder = Context.Items.TryGetValue("IsAccountHolder", out var value)
            && value is true;
        var hasAccess = s.IsPersistent
            ? allowPersistentAccountHolder && isAccountHolder
            : authSessionId.HasValue && s.UserId == authSessionId.Value;
        if (!hasAccess)
        {
            throw new HubException("Access denied");
        }

        session = s;
    }
}

/// <summary>
/// The answer to a guest's create: the new session, or the translation key and values of the refusal.
/// </summary>
public sealed class CreateSessionResult
{
    public DaemonSessionDto? Session { get; init; }
    public string? StageKey { get; init; }
    public Dictionary<string, object?>? Context { get; init; }
}

/// <summary>
/// The answer to a guest's End session: the translation key and values of the failure, or no key when the
/// session ended.
/// </summary>
public sealed class EndSessionResult
{
    public string? StageKey { get; init; }
    public Dictionary<string, object?>? Context { get; init; }
}
