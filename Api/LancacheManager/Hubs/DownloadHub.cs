using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

public class DownloadHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        // Connection is established - no need to send explicit notification
        // Clients can check connection.connectionId directly if needed
        await base.OnConnectedAsync();
    }
}