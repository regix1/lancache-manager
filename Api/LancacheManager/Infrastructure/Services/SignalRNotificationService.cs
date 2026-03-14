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
        await NotifySpecificClientAsync(_steamHubContext.Clients, connectionId, eventName, data, "Steam prefill");
    }

    public async Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _steamHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    // ===== Epic Prefill Hub Methods =====

    public async Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifySpecificClientAsync(_epicHubContext.Clients, connectionId, eventName, data, "Epic prefill");
    }

    public async Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _epicHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    /// <summary>
    /// Shared per-client notification helper. Sends to a specific connection on the provided hub clients,
    /// with consistent debug logging and error handling. Does not rethrow on failure.
    /// </summary>
    private async Task NotifySpecificClientAsync(
        IHubClients hubClients,
        string connectionId,
        string eventName,
        object? data,
        string hubLabel)
    {
        try
        {
            await hubClients.Client(connectionId).SendAsync(eventName, data);
            _logger.LogDebug("SignalR {HubLabel} notification sent to client {ConnectionId}: {EventName}", hubLabel, connectionId, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR {HubLabel} notification to client {ConnectionId}: {EventName}", hubLabel, connectionId, eventName);
        }
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

    public async Task NotifyAllDownloadsAndSteamHubAsync(string eventName, object? data = null)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _steamHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + steam): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + steam): {EventName}", eventName);
        }
    }

    public async Task NotifyAllDownloadsAndEpicHubAsync(string eventName, object? data = null)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _epicHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + epic): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + epic): {EventName}", eventName);
        }
    }
}
