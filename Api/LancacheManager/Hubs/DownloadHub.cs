using LancacheManager.Core.Services;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

public class DownloadHub : Hub
{
    private readonly ConnectionTrackingService _connectionTrackingService;
    private readonly ILogger<DownloadHub> _logger;

    // SignalR group for authenticated users only
    public const string AuthenticatedUsersGroup = "AuthenticatedUsers";

    public DownloadHub(
        ConnectionTrackingService connectionTrackingService,
        ILogger<DownloadHub> logger)
    {
        _connectionTrackingService = connectionTrackingService;
        _logger = logger;
    }

    /// <summary>
    /// Called by clients to join the AuthenticatedUsersGroup.
    /// Auth stripped — always succeeds.
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
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (!string.IsNullOrEmpty(deviceId))
        {
            _connectionTrackingService.RegisterConnection(deviceId, Context.ConnectionId);
        }

        // Always add to authenticated group (no auth check)
        await Groups.AddToGroupAsync(Context.ConnectionId, AuthenticatedUsersGroup);
        _logger.LogDebug("SignalR client connected: ConnectionId={ConnectionId}, DeviceId={DeviceId}",
            Context.ConnectionId, deviceId ?? "none");

        await base.OnConnectedAsync();
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
