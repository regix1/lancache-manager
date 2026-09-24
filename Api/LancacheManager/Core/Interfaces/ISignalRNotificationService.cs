
namespace LancacheManager.Core.Interfaces;

/// <summary>
/// Centralized service for sending SignalR notifications to clients.
/// Use SignalREvents constants for event names to ensure type safety.
/// </summary>
public interface ISignalRNotificationService
{
    /// <summary>
    /// Send a notification on the primary hub (DownloadHub). A run's start, progress and end and the
    /// schedule state reach account holders only, except the completions a guest's dashboard refetches
    /// on; every other event reaches every connected client.
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    Task NotifyAllAsync(string eventName, object? data = null);

    /// <summary>
    /// Fire-and-forget notification to the same audience as <see cref="NotifyAllAsync"/> (does not await).
    /// Use for notifications where you don't need to wait for completion.
    /// </summary>
    /// <param name="eventName">Use SignalREvents constants</param>
    /// <param name="data">Optional payload data</param>
    void NotifyAllFireAndForget(string eventName, object? data = null);

    /// <summary>
    /// Uniform "operation failed" terminal broadcast. Emits the caller-built terminal
    /// <paramref name="failedEvent"/> on <paramref name="eventName"/> through the single
    /// <see cref="NotifyAllAsync"/> choke point and logs the failure centrally, so a Rust
    /// <c>Success=false</c> result (or any service exception) ALWAYS reaches the notification registry
    /// instead of each caller hand-rolling (or forgetting) the failure broadcast.
    ///
    /// <paramref name="failedEvent"/> is the operation's own <c>*Complete</c> record (typed via the
    /// shared <see cref="IOperationComplete"/> contract) and MUST represent a failure terminal:
    /// <c>Success=false, Status=Failed, Cancelled=false, Error=&lt;message&gt;</c>. Cancellation is a
    /// distinct terminal and must NOT be sent through this method.
    /// </summary>
    /// <param name="eventName">The SignalR event name (use SignalREvents constants, e.g. CacheClearComplete).</param>
    /// <param name="failedEvent">The per-operation terminal record carrying the failure state and reason.</param>
    Task NotifyOperationFailedAsync(string eventName, IOperationComplete failedEvent);

    // ===== Steam Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client by connection ID on the SteamDaemonHub.
    /// Throws exceptions on failure so the caller can handle them
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
    /// Throws exceptions on failure so caller can handle them.
    /// </summary>
    Task SendToEpicPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

    // ===== Battle.net Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client on the BattleNetDaemonHub.
    /// Throws exceptions on failure so caller can handle them.
    /// </summary>
    Task SendToBattleNetPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

    // ===== Riot Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client on the RiotDaemonHub.
    /// Throws exceptions on failure so caller can handle them.
    /// </summary>
    Task SendToRiotPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

    // ===== Xbox Prefill Hub Methods =====

    /// <summary>
    /// Send a notification to a specific client on the XboxPrefillDaemonHub.
    /// Throws exceptions on failure so caller can handle them.
    /// </summary>
    Task SendToXboxPrefillClientRawAsync(string connectionId, string eventName, object? data = null);

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
