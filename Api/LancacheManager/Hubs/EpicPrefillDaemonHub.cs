using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Epic Games Prefill daemon sessions.
/// Mirrors PrefillDaemonHub but uses EpicPrefillDaemonService.
/// </summary>
public class EpicPrefillDaemonHub : Hub
{
    private readonly EpicPrefillDaemonService _daemonService;
    private readonly SessionService _sessionService;
    private readonly ILogger<EpicPrefillDaemonHub> _logger;

    public EpicPrefillDaemonHub(
        EpicPrefillDaemonService daemonService,
        SessionService sessionService,
        ILogger<EpicPrefillDaemonHub> logger)
    {
        _daemonService = daemonService;
        _sessionService = sessionService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var rawToken = httpContext != null ? SessionService.GetSessionTokenFromCookie(httpContext) : null;

        if (string.IsNullOrEmpty(rawToken))
        {
            _logger.LogWarning("Epic prefill hub connection attempt without session from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        var session = await _sessionService.ValidateSessionAsync(rawToken);
        var isAdmin = session?.SessionType == "admin";
        var hasPrefillAccess = session?.PrefillExpiresAtUtc != null && session.PrefillExpiresAtUtc > DateTime.UtcNow;
        if (session == null || (!isAdmin && !hasPrefillAccess))
        {
            _logger.LogWarning("Epic prefill hub connection rejected - no prefill access: {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        Context.Items["SessionId"] = session.Id.ToString();

        _logger.LogDebug("Epic prefill hub connected: {ConnectionId}, SessionId: {SessionId}", Context.ConnectionId, session.Id);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _daemonService.RemoveSubscriber(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "Epic prefill hub disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("Epic prefill hub disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

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

            _logger.LogInformation("Creating Epic daemon session for auth session {SessionId}", authSessionId);
            var session = await _daemonService.CreateSessionAsync(authSessionId, ipAddress, userAgent);

            _daemonService.AddSubscriber(session.Id, Context.ConnectionId);

            return DaemonSessionDto.FromSession(session);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Failed to create Epic session for auth session {SessionId}: {Message}", authSessionId, ex.Message);
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating Epic session for auth session {SessionId}", authSessionId);
            throw new HubException("Failed to create Epic daemon session");
        }
    }

    public async Task SubscribeToSession(string sessionId)
    {
        var authSessionId = GetSessionId();

        var session = _daemonService.GetSession(sessionId);
        if (session == null)
            throw new HubException("Session not found");
        if (session.UserId != authSessionId)
            throw new HubException("Access denied");

        _daemonService.AddSubscriber(sessionId, Context.ConnectionId);
        await Clients.Caller.SendAsync("SessionSubscribed", DaemonSessionDto.FromSession(session));
    }

    public async Task<CredentialChallenge?> StartLogin(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Starting Epic login for session {SessionId}", sessionId);
        return await _daemonService.StartLoginAsync(sessionId, TimeSpan.FromSeconds(30));
    }

    public async Task ProvideCredential(string sessionId, CredentialChallenge challenge, string credential)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Providing {CredentialType} credential for Epic session {SessionId}",
            challenge.CredentialType, sessionId);
        await _daemonService.ProvideCredentialAsync(sessionId, challenge, credential);
    }

    public async Task<CredentialChallenge?> WaitForChallenge(string sessionId, int timeoutSeconds = 30)
    {
        ValidateSessionAccess(sessionId, out _);
        return await _daemonService.WaitForChallengeAsync(sessionId, TimeSpan.FromSeconds(timeoutSeconds));
    }

    public async Task CancelLogin(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Cancelling Epic login for session {SessionId}", sessionId);
        await _daemonService.CancelLoginAsync(sessionId);
    }

    public async Task CancelPrefill(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Cancelling Epic prefill for session {SessionId}", sessionId);
        await _daemonService.CancelPrefillAsync(sessionId);
    }

    public async Task<List<OwnedGame>> GetOwnedGames(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        return await _daemonService.GetOwnedGamesAsync(sessionId);
    }

    public async Task SetSelectedApps(string sessionId, List<string> appIds)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("SetSelectedApps called for Epic session {SessionId} with {Count} app IDs",
            sessionId, appIds?.Count ?? 0);
        await _daemonService.SetSelectedAppsAsync(sessionId, appIds ?? new List<string>());
    }

    public async Task<PrefillResult> StartPrefill(string sessionId, bool all = false, bool recent = false, bool force = false, string? operatingSystems = null)
    {
        try
        {
            ValidateSessionAccess(sessionId, out _);

            List<string>? osList = null;
            if (!string.IsNullOrEmpty(operatingSystems))
            {
                osList = operatingSystems.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            }

            _logger.LogInformation("Starting Epic prefill for session {SessionId} (all={All}, force={Force}, os={OS})",
                sessionId, all, force, operatingSystems ?? "default");

            return await _daemonService.PrefillAsync(sessionId, all: all, recent: recent, force: force, operatingSystems: osList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Epic StartPrefill failed for session {SessionId}", sessionId);
            throw;
        }
    }

    public async Task<ClearCacheResult> ClearCache(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Clearing Epic cache for session {SessionId}", sessionId);
        return await _daemonService.ClearCacheAsync(sessionId);
    }

    public async Task<SelectedAppsStatus> GetSelectedAppsStatus(string sessionId, List<string>? operatingSystems = null)
    {
        ValidateSessionAccess(sessionId, out _);
        return await _daemonService.GetSelectedAppsStatusAsync(sessionId, operatingSystems);
    }

    public async Task EndSession(string sessionId)
    {
        ValidateSessionAccess(sessionId, out _);
        _logger.LogInformation("Force ending Epic session {SessionId}", sessionId);
        await _daemonService.TerminateSessionAsync(sessionId, "User ended session", force: true);
    }

    public LastPrefillResultDto? GetLastPrefillResult(string sessionId)
    {
        ValidateSessionAccess(sessionId, out var session);

        if (!session.LastPrefillCompletedAt.HasValue)
            return null;

        return new LastPrefillResultDto
        {
            Status = session.LastPrefillStatus ?? "unknown",
            CompletedAt = session.LastPrefillCompletedAt.Value,
            DurationSeconds = session.LastPrefillDurationSeconds ?? 0
        };
    }

    public IEnumerable<DaemonSessionDto> GetMySessions()
    {
        var authSessionId = GetSessionId();
        if (string.IsNullOrEmpty(authSessionId))
            return Enumerable.Empty<DaemonSessionDto>();

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
            throw new HubException("Session not found");
        if (s.UserId != authSessionId)
            throw new HubException("Access denied");

        session = s;
    }
}
