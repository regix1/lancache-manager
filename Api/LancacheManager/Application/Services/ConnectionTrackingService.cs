using System.Collections.Concurrent;

namespace LancacheManager.Application.Services;

/// <summary>
/// Singleton service to track SignalR connections by device ID.
/// Enables targeted messaging to specific connected clients.
/// </summary>
public class ConnectionTrackingService
{
    private readonly ConcurrentDictionary<string, string> _deviceToConnection = new();
    private readonly ConcurrentDictionary<string, string> _connectionToDevice = new();
    private readonly ILogger<ConnectionTrackingService> _logger;

    public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a SignalR connection for a device.
    /// If the device already has a connection, the old one is replaced.
    /// </summary>
    public void RegisterConnection(string deviceId, string connectionId)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(connectionId))
            return;

        // If device already has a connection, unregister the old one
        if (_deviceToConnection.TryGetValue(deviceId, out var oldConnectionId))
        {
            _connectionToDevice.TryRemove(oldConnectionId, out _);
            _logger.LogDebug("Replaced existing connection {OldConnectionId} for device {DeviceId}",
                oldConnectionId, deviceId);
        }

        _deviceToConnection[deviceId] = connectionId;
        _connectionToDevice[connectionId] = deviceId;

        _logger.LogInformation("Registered SignalR connection {ConnectionId} for device {DeviceId}",
            connectionId, deviceId);
    }

    /// <summary>
    /// Unregister a SignalR connection when it disconnects.
    /// </summary>
    public void UnregisterConnection(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
            return;

        if (_connectionToDevice.TryRemove(connectionId, out var deviceId))
        {
            // Only remove device mapping if it still points to this connection
            // (another connection might have already replaced it)
            _deviceToConnection.TryRemove(deviceId, out var currentConnectionId);
            if (currentConnectionId != null && currentConnectionId != connectionId)
            {
                // Put it back - a newer connection replaced this one
                _deviceToConnection[deviceId] = currentConnectionId;
            }

            _logger.LogInformation("Unregistered SignalR connection {ConnectionId} for device {DeviceId}",
                connectionId, deviceId);
        }
    }

    /// <summary>
    /// Get the SignalR connection ID for a device, if connected.
    /// </summary>
    public string? GetConnectionId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return null;

        _deviceToConnection.TryGetValue(deviceId, out var connectionId);
        return connectionId;
    }

    /// <summary>
    /// Get the device ID for a SignalR connection.
    /// </summary>
    public string? GetDeviceId(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
            return null;

        _connectionToDevice.TryGetValue(connectionId, out var deviceId);
        return deviceId;
    }

    /// <summary>
    /// Check if a device is currently connected.
    /// </summary>
    public bool IsDeviceConnected(string deviceId)
    {
        return !string.IsNullOrEmpty(deviceId) && _deviceToConnection.ContainsKey(deviceId);
    }

    /// <summary>
    /// Get all currently connected device IDs.
    /// </summary>
    public IEnumerable<string> GetConnectedDeviceIds()
    {
        return _deviceToConnection.Keys.ToList();
    }

    /// <summary>
    /// Get the count of connected devices.
    /// </summary>
    public int ConnectedDeviceCount => _deviceToConnection.Count;
}
