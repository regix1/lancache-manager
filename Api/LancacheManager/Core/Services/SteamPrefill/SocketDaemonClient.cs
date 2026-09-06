using System.Net.Sockets;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// Unix Domain Socket transport for <see cref="DaemonClientBase"/>. The daemon publishes its socket
/// file only once it is ready to accept, so connecting waits for that file to appear rather than
/// retrying a connect.
/// </summary>
public sealed class SocketDaemonClient : DaemonClientBase
{
    private static readonly DaemonTransportLabels _labels = new(Connection: "socket", Endpoint: "socket");

    private readonly string _socketPath;

    public SocketDaemonClient(string socketPath, string? sharedSecret = null, ILogger? logger = null)
        : base(sharedSecret, logger)
    {
        _socketPath = socketPath;
    }

    protected override DaemonTransportLabels TransportLabels => _labels;

    protected override Socket CreateSocket()
        => new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

    protected override Task ConnectSocketAsync(Socket socket, CancellationToken cancellationToken)
        => socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken).AsTask();

    /// <summary>
    /// Connects to the daemon socket, waiting up to 30 seconds for the socket file to be published.
    /// </summary>
    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await DisconnectCoreAsync();

        _logger?.LogInformation("Connecting to daemon socket at {SocketPath}", _socketPath);

        // Waiting is only meaningful while the directory the daemon publishes into still exists: that
        // is the "container is still starting" case. When the directory itself is gone the manager's
        // bind mount was deleted or remounted under a running container, so no socket can ever appear
        // there and the full timeout would only postpone the same failure once per container.
        var socketDirectory = Path.GetDirectoryName(_socketPath);
        var mountPresent = string.IsNullOrEmpty(socketDirectory) || Directory.Exists(socketDirectory);

        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (mountPresent && !File.Exists(_socketPath) && DateTime.UtcNow < timeout)
        {
            await Task.Delay(100, cancellationToken);
        }

        if (!File.Exists(_socketPath))
        {
            // The path travels as FileName, not only inside the message, so a caller can report
            // which socket was missing without parsing prose.
            throw new FileNotFoundException(
                mountPresent
                    ? $"Daemon socket not found at {_socketPath}"
                    : $"Daemon socket directory {socketDirectory} no longer exists, so no socket can appear at {_socketPath}",
                _socketPath);
        }

        await EstablishConnectionAsync(cancellationToken);
    }

    protected override void OnConnectAttemptFailed(Exception exception)
        => _logger?.LogError(exception, "Failed to connect to daemon socket at {SocketPath}", _socketPath);
}
