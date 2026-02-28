using Microsoft.AspNetCore.SignalR;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Centralized service for sending SignalR notifications to clients.
/// Provides error handling and logging for all SignalR communications.
/// Supports DownloadHub (primary), SteamDaemonHub for Steam prefill-specific notifications,
/// and EpicPrefillDaemonHub for Epic prefill-specific notifications.
/// </summary>
public class SignalRNotificationService : ISignalRNotificationService
{
    private readonly IHubContext<DownloadHub> _downloadHubContext;
    private readonly IHubContext<SteamDaemonHub> _steamHubContext;
    private readonly IHubContext<EpicPrefillDaemonHub> _epicHubContext;
    private readonly ILogger<SignalRNotificationService> _logger;

    public SignalRNotificationService(
        IHubContext<DownloadHub> downloadHubContext,
        IHubContext<SteamDaemonHub> steamHubContext,
        IHubContext<EpicPrefillDaemonHub> epicHubContext,
        ILogger<SignalRNotificationService> logger)
    {
        _downloadHubContext = downloadHubContext;
        _steamHubContext = steamHubContext;
        _epicHubContext = epicHubContext;
        _logger = logger;
    }

    public async Task NotifyAllAsync(string eventName, object? data = null)
    {
        try
        {
            await _downloadHubContext.Clients.All.SendAsync(eventName, data);
            _logger.LogDebug("SignalR notification sent to all: {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to all: {EventName}", eventName);
        }
    }

    public async Task NotifyGroupAsync(string groupName, string eventName, object? data = null)
    {
        try
        {
            await _downloadHubContext.Clients.Group(groupName).SendAsync(eventName, data);
            _logger.LogDebug("SignalR notification sent to group {GroupName}: {EventName}", groupName, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to group {GroupName}: {EventName}", groupName, eventName);
        }
    }

    public async Task NotifyClientAsync(string connectionId, string eventName, object? data = null)
    {
        try
        {
            await _downloadHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
            _logger.LogDebug("SignalR notification sent to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
    }

    public void NotifyAllFireAndForget(string eventName, object? data = null)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _downloadHubContext.Clients.All.SendAsync(eventName, data);
                _logger.LogDebug("SignalR fire-and-forget notification sent: {EventName}", eventName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send SignalR fire-and-forget notification: {EventName}", eventName);
            }
        });
    }

    // ===== Steam Prefill Hub Methods =====

    public async Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        try
        {
            await _steamHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
            _logger.LogDebug("SignalR Steam prefill notification sent to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR Steam prefill notification to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
    }

    public async Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _steamHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    // ===== Epic Prefill Hub Methods =====

    public async Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        try
        {
            await _epicHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
            _logger.LogDebug("SignalR Epic prefill notification sent to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR Epic prefill notification to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
    }

    public async Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _epicHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    public async Task NotifyAllBothHubsAsync(string eventName, object? data = null)
    {
        try
        {
            // Send to all hubs in parallel
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _steamHubContext.Clients.All.SendAsync(eventName, data),
                _epicHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent to all (all hubs): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to all (all hubs): {EventName}", eventName);
        }
    }
}
