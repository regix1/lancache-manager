using System.Collections.Concurrent;

namespace LancacheManager.Core.Services;

/// <summary>
/// Singleton service to track SignalR connections by session ID.
/// Enables targeted messaging to specific connected clients.
/// </summary>
public class ConnectionTrackingService
{
    private readonly ConcurrentDictionary<string, string> _sessionToConnection = new();
    private readonly ConcurrentDictionary<string, string> _connectionToSession = new();
    private readonly ILogger<ConnectionTrackingService> _logger;

    public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a SignalR connection for a session.
    /// If the session already has a connection, the old one is replaced.
    /// </summary>
    public void RegisterConnection(string sessionId, string connectionId)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(connectionId))
            return;

        // If session already has a connection, unregister the old one
        if (_sessionToConnection.TryGetValue(sessionId, out var oldConnectionId))
        {
            _connectionToSession.TryRemove(oldConnectionId, out _);
            _logger.LogDebug("Replaced existing connection {OldConnectionId} for session {SessionId}",
                oldConnectionId, sessionId);
        }

        _sessionToConnection[sessionId] = connectionId;
        _connectionToSession[connectionId] = sessionId;

        _logger.LogInformation("Registered SignalR connection {ConnectionId} for session {SessionId}",
            connectionId, sessionId);
    }

    /// <summary>
    /// Unregister a SignalR connection when it disconnects.
    /// </summary>
    public void UnregisterConnection(string connectionId)
    {
        if (string.IsNullOrEmpty(connectionId))
            return;

        if (_connectionToSession.TryRemove(connectionId, out var sessionId))
        {
            // Only remove session mapping if it still points to this connection
            // (another connection might have already replaced it)
            _sessionToConnection.TryRemove(sessionId, out var currentConnectionId);
            if (currentConnectionId != null && currentConnectionId != connectionId)
            {
                // Put it back - a newer connection replaced this one
                _sessionToConnection[sessionId] = currentConnectionId;
            }

            _logger.LogInformation("Unregistered SignalR connection {ConnectionId} for session {SessionId}",
                connectionId, sessionId);
        }
    }

    /// <summary>
    /// Get the SignalR connection ID for a session, if connected.
    /// </summary>
    public string? GetConnectionId(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId))
            return null;

        _sessionToConnection.TryGetValue(sessionId, out var connectionId);
        return connectionId;
    }

}
