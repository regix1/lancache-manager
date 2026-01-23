using Microsoft.AspNetCore.SignalR;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;

namespace LancacheManager.Infrastructure.Services;

/// <summary>
/// Centralized service for sending SignalR notifications to clients.
/// Provides error handling and logging for all SignalR communications.
/// Supports both DownloadHub (primary) and PrefillDaemonHub for prefill-specific notifications.
/// </summary>
public class SignalRNotificationService : ISignalRNotificationService
{
    private readonly IHubContext<DownloadHub> _downloadHubContext;
    private readonly IHubContext<PrefillDaemonHub> _prefillHubContext;
    private readonly ILogger<SignalRNotificationService> _logger;

    public SignalRNotificationService(
        IHubContext<DownloadHub> downloadHubContext,
        IHubContext<PrefillDaemonHub> prefillHubContext,
        ILogger<SignalRNotificationService> logger)
    {
        _downloadHubContext = downloadHubContext;
        _prefillHubContext = prefillHubContext;
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

    // ===== Prefill Hub Methods =====

    public async Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        try
        {
            await _prefillHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
            _logger.LogDebug("SignalR prefill notification sent to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR prefill notification to client {ConnectionId}: {EventName}", connectionId, eventName);
        }
    }

    public async Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _prefillHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    public async Task NotifyAllBothHubsAsync(string eventName, object? data = null)
    {
        try
        {
            // Send to both hubs in parallel
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _prefillHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent to all (both hubs): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to all (both hubs): {EventName}", eventName);
        }
    }
}
