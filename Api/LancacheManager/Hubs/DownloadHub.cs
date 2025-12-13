using LancacheManager.Application.Services;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

public class DownloadHub : Hub
{
    private readonly ConnectionTrackingService _connectionTrackingService;
    private readonly ILogger<DownloadHub> _logger;

    public DownloadHub(ConnectionTrackingService connectionTrackingService, ILogger<DownloadHub> logger)
    {
        _connectionTrackingService = connectionTrackingService;
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        // Extract deviceId from query string (passed by frontend)
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (!string.IsNullOrEmpty(deviceId))
        {
            _connectionTrackingService.RegisterConnection(deviceId, Context.ConnectionId);
            _logger.LogDebug("SignalR client connected: ConnectionId={ConnectionId}, DeviceId={DeviceId}",
                Context.ConnectionId, deviceId);
        }
        else
        {
            _logger.LogDebug("SignalR client connected without deviceId: ConnectionId={ConnectionId}",
                Context.ConnectionId);
        }

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
