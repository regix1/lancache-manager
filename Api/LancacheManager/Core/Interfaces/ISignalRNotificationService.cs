namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Centralized service for sending SignalR notifications to clients.
/// Use SignalREvents constants for event names to ensure type safety.
/// </summary>
public interface ISignalRNotificationService
{
    /// <summary>
    /// Send a notification to all connected clients on the primary hub (DownloadHub).
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyAllAsync(string eventName, object? data = null);

    /// <summary>
    /// Fire-and-forget notification to all clients (does not await).
    /// Use for notifications where you don't need to wait for completion.
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    void NotifyAllFireAndForget(string eventName, object? data = null);

    // ===== Steam Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client by connection ID on the SteamDaemonHub.
    /// Used for prefill session-specific notifications like auth challenges, progress, etc.
    /// Errors are caught and logged; does not throw.
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID</param>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyPrefillClientAsync(string connectionId, string eventName, object? data = null);

    /// <summary>
    /// Send a notification to a specific client by connection ID on the SteamDaemonHub.
    /// Unlike NotifyPrefillClientAsync, this throws exceptions on failure so the caller can handle them
    /// (e.g., to remove dead connections from a subscription list).
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID</param>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    /// <exception cref="Exception">Throws if the notification fails</exception>
    Task SendToPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

    // ===== Epic Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client on the EpicPrefillDaemonHub.
    /// Errors are caught and logged; does not throw.
    /// </summary>
    Task NotifyEpicPrefillClientAsync(string connectionId, string eventName, object? data = null);

    /// <summary>
    /// Send a notification to a specific client on the EpicPrefillDaemonHub.
    /// Throws exceptions on failure so caller can handle them.
    /// </summary>
    Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

    /// <summary>
    /// Send a notification to all clients on the DownloadHub and the Steam daemon hub only.
    /// Used for Steam-specific daemon events that should not be sent to the Epic hub.
    /// </summary>
    Task NotifyAllDownloadsAndSteamHubAsync(string eventName, object? data = null);

    /// <summary>
    /// Send a notification to all clients on the DownloadHub and the Epic daemon hub only.
    /// Used for Epic-specific daemon events that should not be sent to the Steam hub.
    /// </summary>
    Task NotifyAllDownloadsAndEpicHubAsync(string eventName, object? data = null);

    // ===== DownloadHub Group Methods =====

    /// <summary>
    /// Send a notification to admin users only on the DownloadHub (AdminUsers group).
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyAdminAsync(string eventName, object? data = null);

    /// <summary>
    /// Send a notification to guest users only on the DownloadHub (GuestUsers group).
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyGuestAsync(string eventName, object? data = null);

    /// <summary>
    /// Send a notification to a named SignalR group on the DownloadHub.
    /// </summary>
    /// <param name="groupName">The SignalR group name (e.g. DownloadHub.AdminGroup)</param>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyGroupAsync(string groupName, string eventName, object? data = null);

}
