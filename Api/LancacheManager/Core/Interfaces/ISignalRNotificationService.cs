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
    /// Send a notification to a specific SignalR group.
    /// </summary>
    /// <param name="groupName">The SignalR group name</param>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyGroupAsync(string groupName, string eventName, object? data = null);

    /// <summary>
    /// Send a notification to a specific client by connection ID on the primary hub (DownloadHub).
    /// </summary>
    /// <param name="connectionId">The SignalR connection ID</param>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyClientAsync(string connectionId, string eventName, object? data = null);

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
    /// Send a notification to all clients on ALL hubs (DownloadHub, SteamDaemonHub, and EpicPrefillDaemonHub).
    /// Used for events that need to be broadcast to all connected clients regardless of which hub they're on.
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyAllBothHubsAsync(string eventName, object? data = null);
}
