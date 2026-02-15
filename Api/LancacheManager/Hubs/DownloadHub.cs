using LancacheManager.Core.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

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
    /// Called by clients to join the AuthenticatedUsersGroup.
    /// Validates session before allowing join.
    /// </summary>
    public async Task JoinAuthenticatedGroup()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, AuthenticatedUsersGroup);
        _logger.LogDebug("SignalR client joined AuthenticatedUsersGroup: ConnectionId={ConnectionId}",
            Context.ConnectionId);
    }

    public override async Task OnConnectedAsync()
    {
        var httpContext = Context.GetHttpContext();

        // Validate session from the WebSocket handshake cookie
        var rawToken = httpContext != null ? SessionService.GetSessionTokenFromCookie(httpContext) : null;

        if (!string.IsNullOrEmpty(rawToken))
        {
            var session = await _sessionService.ValidateSessionAsync(rawToken);
            if (session != null)
            {
                // Register connection with session ID
                _connectionTrackingService.RegisterConnection(session.Id.ToString(), Context.ConnectionId);

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
