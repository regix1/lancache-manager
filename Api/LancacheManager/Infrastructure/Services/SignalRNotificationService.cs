using Microsoft.AspNetCore.SignalR;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Utilities;
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
            // single chokepoint for downloads, log-processing, detection, cache-clear, and eviction
            // completion emitters. Resolved lazily to avoid a constructor DI cycle
            // (IDashboardBatchService is a singleton).
            if (eventName == SignalREvents.GameDetectionComplete)
            {
                // A successful run re-anchors the games-on-disk freshness baseline BEFORE the
                // invalidation below, so the refetch this event triggers compares live usage
                // against the run that just finished. Failed/cancelled runs keep the old
                // baseline: their summary still reflects the previous successful run.
                if (data is SignalRNotifications.GameDetectionComplete { Success: true, Cancelled: false })
                {
                    await _serviceProvider.GetRequiredService<CacheManagementService>()
                        .CaptureDetectionUsageBaselineAsync();
                }

                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateDetectionCache();
            }
            else if (eventName == SignalREvents.CacheClearingComplete &&
                     data is SignalRNotifications.CacheClearComplete { Success: true, Cancelled: false })
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateAllCache();
            }
            // Eviction scan/removal change detection projections and download/client totals across
            // every time range (same shape as a successful cache clear). Invalidate before broadcast
            // so the forced frontend refetch cannot hit a still-valid stale batch.
            else if (eventName == SignalREvents.EvictionScanComplete &&
                     data is EvictionScanComplete { Success: true })
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateAllCache();
            }
            else if (eventName == SignalREvents.EvictionRemovalComplete &&
                     data is EvictionRemovalComplete { Success: true, Cancelled: false })
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateAllCache();
            }
            // A group write changes how client stats rows are built - one summed row per nickname
            // versus one row per member address - so it restructures the table in every time range,
            // not just live. The live-only generation bump leaves historical batch keys unchanged,
            // which would hand the forced frontend refetch the identical stale entry.
            else if (eventName is SignalREvents.ClientGroupCreated or SignalREvents.ClientGroupUpdated
                     or SignalREvents.ClientGroupDeleted or SignalREvents.ClientGroupsCleared)
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateAllCache();
            }
            // Turning client hostnames on or off relabels every client-stats row, so it restructures
            // the table in every time range just as a group write does. A live-only generation bump
            // would leave historical batch keys untouched and hand the forced refetch the identical
            // stale entry.
            else if (eventName == SignalREvents.ClientHostnamesChanged)
            {
                _serviceProvider.GetRequiredService<IDashboardBatchService>().InvalidateAllCache();
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

    public Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
        => SendRawAsync(_steamHubContext.Clients, connectionId, eventName, data);

    // ===== Epic Prefill Hub Methods =====

    public async Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_epicHubContext.Clients, connectionId, eventName, data, "Epic prefill");
    }

    public Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
        => SendRawAsync(_epicHubContext.Clients, connectionId, eventName, data);

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

    /// <summary>
    /// Shared per-client send that deliberately does NOT catch. The caller needs the failure to
    /// detect a dead connection and drop it from its subscription list, so the exception must reach
    /// it. This is the counterpart to <see cref="NotifyClientAsync"/>, which swallows and logs -
    /// routing a raw send through that helper would silently turn a throwing path into a quiet one.
    /// </summary>
    private static async Task SendRawAsync(
        IHubClients hubClients,
        string connectionId,
        string eventName,
        object? data)
    {
        await hubClients.Client(connectionId).SendAsync(eventName, data);
    }

    /// <summary>
    /// Shared broadcast helper. Fans one event out to every client on the primary download hub and on
    /// a single daemon hub, with consistent debug logging and error handling. Does not rethrow on failure.
    /// </summary>
    private async Task NotifyDaemonHubAsync(
        IHubClients daemonClients,
        string eventName,
        object? data,
        string hubLabel)
    {
        try
        {
            await Task.WhenAll(
                _downloadHubContext.Clients.All.SendAsync(eventName, data),
                daemonClients.All.SendAsync(eventName, data)
            );
            _logger.LogDebug("SignalR notification sent (downloads + {HubLabel}): {EventName}", hubLabel, eventName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send SignalR notification (downloads + {HubLabel}): {EventName}", hubLabel, eventName);
        }
    }

    public Task NotifySteamHubAsync(string eventName, object? data = null)
        => NotifyDaemonHubAsync(_steamHubContext.Clients, eventName, data, "steam");

    public Task NotifyEpicHubAsync(string eventName, object? data = null)
        => NotifyDaemonHubAsync(_epicHubContext.Clients, eventName, data, "epic");

    // ===== Battle.net Prefill Hub Methods =====

    public async Task NotifyBattleNetPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_battleNetHubContext.Clients, connectionId, eventName, data, "Battle.net prefill");
    }

    public Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
        => SendRawAsync(_battleNetHubContext.Clients, connectionId, eventName, data);

    public Task NotifyBattleNetHubAsync(string eventName, object? data = null)
        => NotifyDaemonHubAsync(_battleNetHubContext.Clients, eventName, data, "battlenet");

    // ===== Riot Prefill Hub Methods =====

    public async Task NotifyRiotPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_riotHubContext.Clients, connectionId, eventName, data, "Riot prefill");
    }

    public Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
        => SendRawAsync(_riotHubContext.Clients, connectionId, eventName, data);

    public Task NotifyRiotHubAsync(string eventName, object? data = null)
        => NotifyDaemonHubAsync(_riotHubContext.Clients, eventName, data, "riot");

    // ===== Xbox Prefill Hub Methods =====

    public async Task NotifyXboxPrefillClientAsync(string connectionId, string eventName, object? data = null)
    {
        await NotifyClientAsync(_xboxHubContext.Clients, connectionId, eventName, data, "Xbox prefill");
    }

    public Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null)
        => SendRawAsync(_xboxHubContext.Clients, connectionId, eventName, data);

    public Task NotifyXboxHubAsync(string eventName, object? data = null)
        => NotifyDaemonHubAsync(_xboxHubContext.Clients, eventName, data, "xbox");

    // ===== DownloadHub Group Methods =====

    public Task NotifyAdminAsync(string eventName, object? data = null)
        => NotifyGroupAsync(DownloadHub.AdminGroup, eventName, data);

    public Task NotifyGuestAsync(string eventName, object? data = null)
        => NotifyGroupAsync(DownloadHub.GuestGroup, eventName, data);

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
