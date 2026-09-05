namespace LancacheManager.Core.Services;

/// <summary>
/// Singleton service to track SignalR connections by session ID.
/// Enables targeted messaging to specific connected clients.
/// The session id here refers to <c>UserSession.Id</c> (a <see cref="Guid"/>),
/// not the 16-char daemon-local id used by the prefill daemon sessions.
/// </summary>
public class ConnectionTrackingService
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Dictionary<string, Action>> _sessionToConnection = [];
    private readonly Dictionary<string, Guid> _connectionToSession = [];
    private readonly HashSet<Guid> _disconnectedSessions = [];
    private readonly ILogger<ConnectionTrackingService> _logger;

    public ConnectionTrackingService(ILogger<ConnectionTrackingService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Register a SignalR connection for a session. A shared unauthenticated session can belong to
    /// several browsers, so every connection is retained until it disconnects.
    /// </summary>
    public void RegisterConnection(Guid sessionId, string connectionId, Action? abort = null)
    {
        if (sessionId == Guid.Empty || string.IsNullOrEmpty(connectionId))
            return;

        var abortConnection = false;
        lock (_sync)
        {
            if (_disconnectedSessions.Contains(sessionId))
            {
                abortConnection = true;
            }
            else
            {
                if (_connectionToSession.TryGetValue(connectionId, out var oldSessionId)
                    && oldSessionId != sessionId
                    && _sessionToConnection.TryGetValue(oldSessionId, out var oldConnections))
                {
                    oldConnections.Remove(connectionId);
                    if (oldConnections.Count == 0)
                    {
                        _sessionToConnection.Remove(oldSessionId);
                    }
                }

                if (!_sessionToConnection.TryGetValue(sessionId, out var connections))
                {
                    connections = [];
                    _sessionToConnection.Add(sessionId, connections);
                }
                connections[connectionId] = abort ?? (static () => { });
                _connectionToSession[connectionId] = sessionId;
            }
        }

        if (abortConnection)
        {
            Abort(connectionId, abort);
            return;
        }

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

        Guid? sessionId = null;
        lock (_sync)
        {
            if (_connectionToSession.Remove(connectionId, out var removedSessionId))
            {
                sessionId = removedSessionId;
                if (_sessionToConnection.TryGetValue(removedSessionId, out var connections))
                {
                    connections.Remove(connectionId);
                    if (connections.Count == 0)
                    {
                        _sessionToConnection.Remove(removedSessionId);
                    }
                }
            }
        }

        if (sessionId is not null)
        {
            _logger.LogDebug("Unregistered SignalR connection {ConnectionId} for session {SessionId}",
                connectionId, sessionId);
        }
    }

    /// <summary>
    /// Abort every live connection authenticated by one session. Entries are removed first so a
    /// disconnect callback racing this method cannot remove a later registration.
    /// </summary>
    public int DisconnectSession(Guid sessionId)
    {
        List<KeyValuePair<string, Action>> connections;
        lock (_sync)
        {
            _disconnectedSessions.Add(sessionId);
            if (!_sessionToConnection.Remove(sessionId, out var registered))
            {
                return 0;
            }

            connections = [.. registered];
            foreach (var connection in connections)
            {
                _connectionToSession.Remove(connection.Key);
            }
        }

        foreach (var (connectionId, abort) in connections)
        {
            Abort(connectionId, abort);
        }

        _logger.LogInformation(
            "Disconnected {Count} SignalR connections for session {SessionId}",
            connections.Count,
            sessionId);
        return connections.Count;
    }

    public bool IsDisconnected(Guid sessionId)
    {
        lock (_sync)
        {
            return _disconnectedSessions.Contains(sessionId);
        }
    }

    private void Abort(string connectionId, Action? abort)
    {
        try
        {
            abort?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not abort SignalR connection {ConnectionId}", connectionId);
        }
    }

}
