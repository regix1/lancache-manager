using LancacheManager.Application.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

/// <summary>
/// SignalR hub for Steam Prefill terminal sessions.
/// Handles bidirectional communication between browser xterm.js and Docker containers.
/// </summary>
public class PrefillTerminalHub : Hub
{
    private readonly PrefillSessionService _sessionService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly ILogger<PrefillTerminalHub> _logger;

    public PrefillTerminalHub(
        PrefillSessionService sessionService,
        DeviceAuthService deviceAuthService,
        ILogger<PrefillTerminalHub> logger)
    {
        _sessionService = sessionService;
        _deviceAuthService = deviceAuthService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (string.IsNullOrEmpty(deviceId) || !_deviceAuthService.ValidateDevice(deviceId))
        {
            _logger.LogWarning("Unauthorized prefill terminal connection attempt from {ConnectionId}", Context.ConnectionId);
            Context.Abort();
            return;
        }

        _logger.LogDebug("Prefill terminal connected: {ConnectionId}, DeviceId: {DeviceId}", Context.ConnectionId, deviceId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _sessionService.RemoveSubscriber(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "Prefill terminal disconnected with error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("Prefill terminal disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Creates a new prefill session and returns session info
    /// </summary>
    public async Task<PrefillSessionDto> CreateSession()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (string.IsNullOrEmpty(deviceId))
        {
            throw new HubException("Device ID required");
        }

        try
        {
            _logger.LogInformation("Creating prefill session for device {DeviceId}", deviceId);
            var session = await _sessionService.CreateSessionAsync(deviceId);
            return PrefillSessionDto.FromSession(session);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Failed to create session for device {DeviceId}", deviceId);
            throw new HubException(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating session for device {DeviceId}", deviceId);
            throw new HubException("Failed to create prefill session. Check if Docker is running.");
        }
    }

    /// <summary>
    /// Attaches to an existing session and starts receiving output
    /// </summary>
    public async Task AttachSession(string sessionId)
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            throw new HubException("Session not found");
        }

        // Verify the session belongs to this user
        if (session.UserId != deviceId)
        {
            _logger.LogWarning("User {DeviceId} attempted to attach to session {SessionId} owned by {OwnerId}",
                deviceId, sessionId, session.UserId);
            throw new HubException("Access denied");
        }

        _logger.LogInformation("Attaching to session {SessionId} from connection {ConnectionId}", sessionId, Context.ConnectionId);

        await _sessionService.StartOutputStreamAsync(sessionId, Context.ConnectionId);

        // Send initial session info
        await Clients.Caller.SendAsync("SessionAttached", PrefillSessionDto.FromSession(session));
    }

    /// <summary>
    /// Sends terminal input to the container
    /// </summary>
    public async Task SendInput(string sessionId, string data)
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        var session = _sessionService.GetSession(sessionId);
        if (session == null || session.UserId != deviceId)
        {
            throw new HubException("Session not found or access denied");
        }

        await _sessionService.SendInputAsync(sessionId, data);
    }

    /// <summary>
    /// Handles terminal resize events
    /// </summary>
    public async Task ResizeTerminal(string sessionId, int cols, int rows)
    {
        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return;
        }

        await _sessionService.ResizeTerminalAsync(sessionId, cols, rows);
    }

    /// <summary>
    /// Terminates the session and destroys the container
    /// </summary>
    public async Task EndSession(string sessionId)
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        var session = _sessionService.GetSession(sessionId);
        if (session == null)
        {
            return;
        }

        // Only allow owner to end session
        if (session.UserId != deviceId)
        {
            throw new HubException("Access denied");
        }

        _logger.LogInformation("User {DeviceId} ending session {SessionId}", deviceId, sessionId);
        await _sessionService.TerminateSessionAsync(sessionId, "User ended session");
    }

    /// <summary>
    /// Gets info about the current session
    /// </summary>
    public PrefillSessionDto? GetSessionInfo(string sessionId)
    {
        var session = _sessionService.GetSession(sessionId);
        return session != null ? PrefillSessionDto.FromSession(session) : null;
    }

    /// <summary>
    /// Gets all sessions for the current user
    /// </summary>
    public IEnumerable<PrefillSessionDto> GetMySessions()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (string.IsNullOrEmpty(deviceId))
        {
            return Enumerable.Empty<PrefillSessionDto>();
        }

        return _sessionService.GetUserSessions(deviceId)
            .Select(PrefillSessionDto.FromSession);
    }
}
