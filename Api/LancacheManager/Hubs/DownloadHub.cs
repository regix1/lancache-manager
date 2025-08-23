using Microsoft.AspNetCore.SignalR;

namespace LancacheManager.Hubs;

public class DownloadHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected", Context.ConnectionId);
        await base.OnConnectedAsync();
    }
}