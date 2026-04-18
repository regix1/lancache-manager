using LancacheManager.Core.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

[Authorize]
public class DownloadHub : Hub
{
    private readonly ConnectionTrackingService _connectionTrackingService;
    private readonly SessionService _sessionService;
    private readonly ILogger<DownloadHub> _logger;

    // SignalR groups for role-based messaging
    public const string AuthenticatedUsersGroup = "AuthenticatedUsers";
    public const string AdminGroup = "AdminUsers";
    public const string GuestGroup = "GuestUsers";

    public DownloadHub(
        ConnectionTrackingService connectionTrackingService,
        SessionService sessionService,
        ILogger<DownloadHub> logger)
    {
        _connectionTrackingService = connectionTrackingService;
        _sessionService = sessionService;
        _logger = logger;
    }

    /// <summary>
    /// Called by admin clients to explicitly join the AdminGroup.
    /// Validates that the caller's session has the admin session type before allowing join.
    /// </summary>
    public async Task JoinAuthenticatedGroupAsync()
    {
        var httpContext = Context.GetHttpContext();
        var rawToken = httpContext != null ? SessionService.GetSessionTokenFromRequest(httpContext) : null;

        if (string.IsNullOrEmpty(rawToken))
        {
            _logger.LogWarning("SignalR JoinAuthenticatedGroupAsync rejected - no token: ConnectionId={ConnectionId}", Context.ConnectionId);
            return;
        }

        var session = await _sessionService.ValidateSessionAsync(rawToken);
        if (session == null || session.SessionType != "admin")
        {
            _logger.LogWarning("SignalR JoinAuthenticatedGroupAsync rejected - not admin: ConnectionId={ConnectionId}, SessionType={SessionType}",
                Context.ConnectionId, session?.SessionType ?? "none");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
        _logger.LogDebug("SignalR client joined AdminGroup: ConnectionId={ConnectionId}", Context.ConnectionId);
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();

        // Validate session from the WebSocket handshake cookie
        var rawToken = httpContext != null ? SessionService.GetSessionTokenFromRequest(httpContext) : null;

        if (!string.IsNullOrEmpty(rawToken))
        {
            var session = await _sessionService.ValidateSessionAsync(rawToken);
            if (session != null)
            {
                // Register connection with session ID
                _connectionTrackingService.RegisterConnection(session.Id, Context.ConnectionId);

                // Add to appropriate groups
                await Groups.AddToGroupAsync(Context.ConnectionId, AuthenticatedUsersGroup);

                if (session.SessionType == "admin")
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, AdminGroup);
                }
                else
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, GuestGroup);
                }

                _logger.LogDebug("SignalR client connected: ConnectionId={ConnectionId}, SessionType={SessionType}",
                    Context.ConnectionId, session.SessionType);

                await base.OnConnectedAsync();
                return;
            }
        }

        // No valid session - abort connection
        _logger.LogWarning("SignalR connection rejected - no valid session: ConnectionId={ConnectionId}", Context.ConnectionId);
        Context.Abort();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _connectionTrackingService.UnregisterConnection(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "SignalR client disconnected with error: ConnectionId={ConnectionId}",
                Context.ConnectionId);
        }
        else
        {
            _logger.LogDebug("SignalR client disconnected: ConnectionId={ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }
}
