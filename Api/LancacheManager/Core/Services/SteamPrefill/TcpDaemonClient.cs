using System.Net.Sockets;

namespace LancacheManager.Core.Services.SteamPrefill;

/// <summary>
/// TCP transport for <see cref="DaemonClientBase"/>. A listening endpoint cannot be observed the way a
/// socket file can, so connecting retries until the daemon accepts or the window expires.
/// </summary>
public sealed class TcpDaemonClient : DaemonClientBase
{
    private static readonly DaemonTransportLabels _labels = new(Connection: "TCP connection", Endpoint: "TCP endpoint");

    private readonly string _host;
    private readonly int _port;

    public TcpDaemonClient(string host, int port, string? sharedSecret = null, ILogger? logger = null)
        : base(sharedSecret, logger)
    {
        _host = host;
        _port = port;
    }

    protected override DaemonTransportLabels TransportLabels => _labels;

    protected override Socket CreateSocket()
        => new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    protected override Task ConnectSocketAsync(Socket socket, CancellationToken cancellationToken)
        => socket.ConnectAsync(_host, _port, cancellationToken).AsTask();

    /// <summary>
    /// Connects to the daemon TCP endpoint, retrying refused/broken attempts for up to 30 seconds.
    /// </summary>
    protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
    {
        await DisconnectCoreAsync();

        _logger?.LogInformation("Connecting to daemon TCP endpoint at {Host}:{Port}", _host, _port);

        var timeout = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await EstablishConnectionAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                await Task.Delay(200, cancellationToken);
            }
        }

        throw new SocketException((int)SocketError.ConnectionRefused);
    }
}
