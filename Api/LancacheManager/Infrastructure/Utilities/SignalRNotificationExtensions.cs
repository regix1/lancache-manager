using LancacheManager.Core.Interfaces;
using LancacheManager.Models;

namespace LancacheManager.Infrastructure.Utilities;

/// <summary>
/// Extension methods for ISignalRNotificationService that provide standardized
/// operation completion notification payloads. Consolidates the common pattern of
/// sending OperationId + Success + Status + Message + Cancelled across services.
/// </summary>
/// <remarks>
/// NOTE: DepotMappingComplete uses different property names/casing and is intentionally excluded.
/// </remarks>
public static class SignalRNotificationExtensions
{
    /// <summary>
    /// Sends a standardized operation completion notification with consistent field names.
    /// The common fields (OperationId, Success, Status, Message, Cancelled) are always included.
    /// Additional service-specific data can be merged via extraData.
    /// </summary>
    /// <param name="notifications">The notification service</param>
    /// <param name="eventName">SignalR event name (e.g., SignalREvents.LogProcessingComplete)</param>
    /// <param name="operationId">The operation tracker ID</param>
    /// <param name="success">Whether the operation succeeded</param>
    /// <param name="message">Human-readable completion message</param>
    /// <param name="cancelled">Whether the operation was cancelled</param>
    /// <param name="extraData">Optional additional properties to merge into the notification payload</param>
    public static Task SendOperationCompleteAsync(
        this ISignalRNotificationService notifications,
        string eventName,
        string? operationId,
        bool success,
        string message,
        bool cancelled,
        object? extraData = null)
    {
        var status = cancelled ? OperationStatus.Cancelled
                   : success  ? OperationStatus.Completed
                              : OperationStatus.Failed;

        // Build the payload by combining common fields with any extra data
        // Using a dictionary allows merging the extra properties dynamically
        var payload = new Dictionary<string, object?>
        {
            ["OperationId"] = operationId,
            ["Success"] = success,
            ["Status"] = status,
            ["Message"] = message,
            ["Cancelled"] = cancelled
        };

        if (extraData != null)
        {
            // Merge extra properties from the anonymous object
            foreach (var prop in extraData.GetType().GetProperties())
            {
                payload[prop.Name] = prop.GetValue(extraData);
            }
        }

        return notifications.NotifyAllAsync(eventName, payload);
    }
}
