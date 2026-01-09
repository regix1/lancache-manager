using LancacheManager.Core.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

public class DownloadHub : Hub
{
    private readonly ConnectionTrackingService _connectionTrackingService;
    private readonly DeviceAuthService _deviceAuthService;
    private readonly ILogger<DownloadHub> _logger;

    // SignalR group for authenticated users only
    public const string AuthenticatedUsersGroup = "AuthenticatedUsers";

    public DownloadHub(
        ConnectionTrackingService connectionTrackingService,
        DeviceAuthService deviceAuthService,
        ILogger<DownloadHub> logger)
    {
        _connectionTrackingService = connectionTrackingService;
        _deviceAuthService = deviceAuthService;
        _logger = logger;
    }

    /// <summary>
    /// Called by authenticated clients to join the AuthenticatedUsersGroup.
    /// This handles the case where the SignalR connection was established before auth was validated.
    /// </summary>
    public async Task JoinAuthenticatedGroup()
    {
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (!string.IsNullOrEmpty(deviceId) && _deviceAuthService.ValidateDevice(deviceId))
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, AuthenticatedUsersGroup);
            _logger.LogInformation("SignalR client joined AuthenticatedUsersGroup: ConnectionId={ConnectionId}, DeviceId={DeviceId}",
                Context.ConnectionId, deviceId);
        }
        else
        {
            _logger.LogWarning("SignalR client attempted to join AuthenticatedUsersGroup but is not authenticated: ConnectionId={ConnectionId}, DeviceId={DeviceId}",
                Context.ConnectionId, deviceId ?? "null");
        }
    }

    public override async Task OnConnectedAsync()
    {
        // Extract deviceId from query string (passed by frontend)
        var httpContext = Context.GetHttpContext();
        var deviceId = httpContext?.Request.Query["deviceId"].FirstOrDefault();

        if (!string.IsNullOrEmpty(deviceId))
        {
            _connectionTrackingService.RegisterConnection(deviceId, Context.ConnectionId);

            // Check if this is an authenticated user and add to group
            if (_deviceAuthService.ValidateDevice(deviceId))
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, AuthenticatedUsersGroup);
                _logger.LogDebug("SignalR client connected (authenticated): ConnectionId={ConnectionId}, DeviceId={DeviceId}",
                    Context.ConnectionId, deviceId);
            }
            else
            {
                _logger.LogDebug("SignalR client connected (guest): ConnectionId={ConnectionId}, DeviceId={DeviceId}",
                    Context.ConnectionId, deviceId);
            }
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
