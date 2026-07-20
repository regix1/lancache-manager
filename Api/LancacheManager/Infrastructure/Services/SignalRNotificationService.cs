using Microsoft.AspNetCore.SignalR;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Models;

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
    private readonly IHubContext<BattleNetDaemonHub> _battleNetHubContext;
    private readonly IHubContext<RiotDaemonHub> _riotHubContext;
    private readonly IHubContext<XboxPrefillDaemonHub> _xboxHubContext;
    private readonly ILogger<SignalRNotificationService> _logger;
    private readonly IServiceProvider _serviceProvider;

    public SignalRNotificationService(
        IHubContext<DownloadHub> downloadHubContext,
        IHubContext<SteamDaemonHub> steamHubContext,
        IHubContext<EpicPrefillDaemonHub> epicHubContext,
        IHubContext<BattleNetDaemonHub> battleNetHubContext,
        IHubContext<RiotDaemonHub> riotHubContext,
        IHubContext<XboxPrefillDaemonHub> xboxHubContext,
        ILogger<SignalRNotificationService> logger,
        IServiceProvider serviceProvider)
    {
        _downloadHubContext = downloadHubContext;
        _steamHubContext = steamHubContext;
        _epicHubContext = epicHubContext;
        _battleNetHubContext = battleNetHubContext;
        _riotHubContext = riotHubContext;
        _xboxHubContext = xboxHubContext;
        _logger = logger;
        _serviceProvider = serviceProvider;
    }

    public async Task NotifyAllAsync(string eventName, object? data = null)
    {
        try
        {
            // Events that make the frontend refetch GET /api/dashboard/batch must first invalidate
            // the affected batch variants, or the refetch can receive a stale snapshot. This is the
            // single chokepoint for downloads, log-processing, and detection completion emitters.
            // Resolved lazily to avoid a constructor DI cycle (IDashboardBatchService is a singleton).
            if (eventName == SignalREvents.GameDetectionComplete)
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateDetectionCache();
            }
            else if (eventName is SignalREvents.DownloadsRefresh or SignalREvents.LogProcessingComplete)
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateLiveCache();
            }

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

    public Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent)
    {
        // Central visibility for every operation failure that reaches the notification registry.
        // LogWarning (not LogError): a surfaced, tolerated operation failure — the real exception,
        // where one exists, is logged at its origin. Routes through NotifyAllAsync so the send itself
        // is subject to the same swallow+log resilience as every other broadcast.
        _logger.LogWarning(
            "Operation failed broadcast: {EventName} (operationId={OperationId}): {Error}",
            eventName, failedEvent.OperationId, failedEvent.Error);

        return NotifyAllAsync(eventName, failedEvent);
    }

    // ===== Steam Prefill Hub Methods =====

    public async Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_steamHubContext.Clients, connectionId, eventName, data, "Steam prefill");
    }

    public async Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _steamHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    // ===== Epic Prefill Hub Methods =====

    public async Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_epicHubContext.Clients, connectionId, eventName, data, "Epic prefill");
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
    private async Task NotifyClientAsync(
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

    public async Task NotifySteamHubAsync(string eventName, object? data = null)
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

    public async Task NotifyEpicHubAsync(string eventName, object? data = null)
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

    // ===== Battle.net Prefill Hub Methods =====

    public async Task NotifyBattleNetPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_battleNetHubContext.Clients, connectionId, eventName, data, "Battle.net prefill");
    }

    public async Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _battleNetHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    public async Task NotifyBattleNetHubAsync(string eventName, object? data = null)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _battleNetHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + battlenet): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + battlenet): {EventName}", eventName);
        }
    }

    // ===== Riot Prefill Hub Methods =====

    public async Task NotifyRiotPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_riotHubContext.Clients, connectionId, eventName, data, "Riot prefill");
    }

    public async Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _riotHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    public async Task NotifyRiotHubAsync(string eventName, object? data = null)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _riotHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + riot): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + riot): {EventName}", eventName);
        }
    }

    // ===== Xbox Prefill Hub Methods =====

    public async Task NotifyXboxPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_xboxHubContext.Clients, connectionId, eventName, data, "Xbox prefill");
    }

    public async Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
    {
        // This method throws on failure - caller is responsible for handling exceptions
        await _xboxHubContext.Clients.Client(connectionId).SendAsync(eventName, data);
    }

    public async Task NotifyXboxHubAsync(string eventName, object? data = null)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                _xboxHubContext.Clients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + xbox): {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + xbox): {EventName}", eventName);
        }
    }

    // ===== DownloadHub Group Methods =====

    public async Task NotifyAdminAsync(string eventName, object? data = null)
    {
        try
        {
            await _downloadHubContext.Clients.Group(DownloadHub.AdminGroup).SendAsync(eventName, data);
            _logger.LogDebug("SignalR notification sent to admin group: {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to admin group: {EventName}", eventName);
        }
    }

    public async Task NotifyGuestAsync(string eventName, object? data = null)
    {
        try
        {
            await _downloadHubContext.Clients.Group(DownloadHub.GuestGroup).SendAsync(eventName, data);
            _logger.LogDebug("SignalR notification sent to guest group: {EventName}", eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification to guest group: {EventName}", eventName);
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
}
