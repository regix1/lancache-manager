using System.Collections.Concurrent;

namespace LancacheManager.Core.Services;

/// <summary>
/// Singleton service to track SignalR connections by session ID.
/// Enables targeted messaging to specific connected clients.
/// The session id here refers to <c>UserSession.Id</c> (a <see cref="Guid"/>),
/// not the 16-char daemon-local id used by the prefill daemon sessions.
/// </summary>
public class ConnectionTrackingService
{
    private readonly ConcurrentDictionary<Guid, string> _sessionToConnection = new();
    private readonly ConcurrentDictionary<string, Guid> _connectionToSession = new();
    private readonly ILogger<ConnectionTrackingService> _logger;

    public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a SignalR connection for a session.
    /// If the session already has a connection, the old one is replaced.
    /// </summary>
    public void RegisterConnection(Guid sessionId, string connectionId)
    {
        if (sessionId == Guid.Empty || string.IsNullOrEmpty(connectionId))
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

        _logger.LogDebug("Registered SignalR connection {ConnectionId} for session {SessionId}",
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

            _logger.LogDebug("Unregistered SignalR connection {ConnectionId} for session {SessionId}",
                connectionId, sessionId);
        }
    }

    /// <summary>
    /// Get the SignalR connection ID for a session, if connected.
    /// </summary>
    public string? GetConnectionId(Guid sessionId)
    {
        if (sessionId == Guid.Empty)
            return null;

        _sessionToConnection.TryGetValue(sessionId, out var connectionId);
        return connectionId;
    }

}
