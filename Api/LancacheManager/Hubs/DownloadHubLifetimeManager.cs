using System.Collections.Concurrent;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;

namespace LancacheManager.Hubs;

/// <summary>
/// Sends every download hub broadcast without waiting for any one browser. The default manager
/// returns a task that completes only when the slowest connection has flushed, so a browser that
/// stops reading would hold each later update back from every other browser.
/// </summary>
public sealed class DownloadHubLifetimeManager : DefaultHubLifetimeManager<DownloadHub>
{
    // Writes queue here only once a connection's 64 KB transport buffer is full, that is, once its
    // browser has stopped reading or reads slower than updates arrive. The largest burst one action
    // sends is Run All: one run for each of the 14 schedules, a few rows each, under 200 messages;
    // steady traffic is a few messages a second. A connection this many writes behind is minutes
    // behind, so it is asked to close and reload, and the server never holds more queued messages
    // than this for one browser.
    internal const int MaxQueuedWrites = 1024;

    private readonly ConcurrentDictionary<string, ConnectionQueue> _connections = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConnectionQueue>> _groups = new();
    private readonly ILogger<DownloadHubLifetimeManager> _logger;

    public DownloadHubLifetimeManager(
        ILogger<DefaultHubLifetimeManager<DownloadHub>> baseLogger,
        ILogger<DownloadHubLifetimeManager> logger)
        : base(baseLogger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync(HubConnectionContext connection)
    {
        await base.OnConnectedAsync(connection);
        _connections[connection.ConnectionId] = new ConnectionQueue(connection, _logger);
    }

    public override async Task OnDisconnectedAsync(HubConnectionContext connection)
    {
        _connections.TryRemove(connection.ConnectionId, out _);
        foreach (var members in _groups.Values)
        {
            members.TryRemove(connection.ConnectionId, out _);
        }

        await base.OnDisconnectedAsync(connection);
    }

    public override async Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
    {
        await base.AddToGroupAsync(connectionId, groupName, cancellationToken);

        // A hub method can still add a connection whose disconnect already removed it.
        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return;
        }

        var members = _groups.GetOrAdd(groupName, static _ => new ConcurrentDictionary<string, ConnectionQueue>());
        members[connectionId] = entry;

        // The base class's rule: a disconnect that raced this add has already cleared the groups.
        if (entry.Connection.ConnectionAborted.IsCancellationRequested)
        {
            members.TryRemove(connectionId, out _);
        }
    }

    // A removal racing an add would leave this class's group map and the base class's apart.
    public override Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The download hub never removes a connection from a group; it leaves them when it disconnects.");

    public override Task SendAllAsync(string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        Send(_connections.Values, methodName, args);
        return Task.CompletedTask;
    }

    public override Task SendGroupAsync(string groupName, string methodName, object?[] args, CancellationToken cancellationToken = default)
    {
        if (_groups.TryGetValue(groupName, out var members))
        {
            Send(members.Values, methodName, args);
        }

        return Task.CompletedTask;
    }

    // Each connection receives messages in the order Send reaches it: a send that returns before another
    // starts is ahead of it on every connection. Two sends from different threads at once have no set
    // order between them, as with the default manager; run rows keep their revision order because the
    // tracker sends them from one drain loop.
    private static void Send(IEnumerable<ConnectionQueue> targets, string methodName, object?[] args)
    {
        var message = new SerializedHubMessage(new InvocationMessage(methodName, args));
        foreach (var target in targets)
        {
            target.Write(message);
        }
    }

    private sealed class ConnectionQueue(HubConnectionContext connection, ILogger logger)
    {
        private int _queuedWrites;
        private int _closeRequested;

        public HubConnectionContext Connection { get; } = connection;

        public void Write(SerializedHubMessage message)
        {
            if (Volatile.Read(ref _closeRequested) == 1)
            {
                return;
            }

            // Serialize before taking a slot: a message the protocol cannot write throws here holding
            // none, and a write that waits behind a full buffer still sends the update as it was when
            // it was sent.
            message.GetSerializedMessage(Connection.Protocol);
            if (Interlocked.Increment(ref _queuedWrites) > MaxQueuedWrites)
            {
                Interlocked.Decrement(ref _queuedWrites);
                if (Interlocked.Exchange(ref _closeRequested, 1) == 0)
                {
                    logger.LogWarning("SignalR connection closed - more than {Limit} updates queued: ConnectionId={ConnectionId}",
                        MaxQueuedWrites, Connection.ConnectionId);
                    // This close lets the browser reconnect and reload its runs; Abort() would send a
                    // Close that forbids reconnecting.
                    Connection.Features.Get<IConnectionLifetimeNotificationFeature>()!.RequestClose();
                }

                return;
            }

            var write = Connection.WriteAsync(message);
            if (write.IsCompletedSuccessfully)
            {
                // Tells a pooled ValueTask its result was read, as the base class does.
                write.GetAwaiter().GetResult();
                Interlocked.Decrement(ref _queuedWrites);
            }
            else
            {
                _ = CompleteAsync(write);
            }
        }

        private async Task CompleteAsync(ValueTask write)
        {
            try
            {
                await write;
            }
            finally
            {
                Interlocked.Decrement(ref _queuedWrites);
            }
        }
    }
}
