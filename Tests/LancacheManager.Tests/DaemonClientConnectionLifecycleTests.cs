using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class DaemonClientConnectionLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoteClose_FailsPendingCallAndReconnectsCleanlyAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var closeServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            await ReadRequestAsync(stream, timeout.Token);
        }, timeout.Token);

        await Assert.ThrowsAsync<IOException>(() => client.SendCommandAsync(
            "blocked",
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken: timeout.Token));
        await closeServer;
        await WaitUntilAsync(() => !IsConnected(client), timeout.Token);

        var responseWritten = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var closeReconnectedServer = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var responseServer = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            await WriteResponseAsync(stream, request.Id, success: true, request.Type, null, timeout.Token);
            responseWritten.TrySetResult();
            await closeReconnectedServer.Task.WaitAsync(timeout.Token);
        }, timeout.Token);

        try
        {
            var response = await client.SendCommandAsync(
                "after-reconnect",
                timeout: TimeSpan.FromSeconds(5),
                cancellationToken: timeout.Token);
            await responseWritten.Task.WaitAsync(timeout.Token);

            Assert.True(response.Success);
            Assert.Equal("after-reconnect", response.Message);
            Assert.True(IsConnected(client));
        }
        finally
        {
            closeReconnectedServer.TrySetResult();
        }

        await responseServer;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCalls_CorrelateResponsesThatArriveOutOfOrderAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var first = await ReadRequestAsync(stream, timeout.Token);
            var second = await ReadRequestAsync(stream, timeout.Token);

            await WriteResponseAsync(stream, second.Id, success: true, second.Type, null, timeout.Token);
            await WriteResponseAsync(stream, first.Id, success: true, first.Type, null, timeout.Token);
        }, timeout.Token);

        var firstCall = client.SendCommandAsync("first", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var secondCall = client.SendCommandAsync("second", timeout: TimeSpan.FromSeconds(5), cancellationToken: timeout.Token);
        var responses = await Task.WhenAll(firstCall, secondCall);

        Assert.Equal("first", responses[0].Message);
        Assert.Equal("second", responses[1].Message);
        await server;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPrefillAsync_RejectedResponseThrowsAsync(bool useTcp)
    {
        using var endpoint = LoopbackEndpoint.Create(useTcp);
        using var client = endpoint.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var server = Task.Run(async () =>
        {
            using var connection = await endpoint.AcceptAsync(timeout.Token);
            using var stream = new NetworkStream(connection, ownsSocket: false);
            var request = await ReadRequestAsync(stream, timeout.Token);
            Assert.Equal("cancel-prefill", request.Type);
            await WriteResponseAsync(stream, request.Id, success: false, null, "still running", timeout.Token);
        }, timeout.Token);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CancelPrefillAsync(timeout.Token));

        Assert.Contains("still running", exception.Message, StringComparison.Ordinal);
        await server;
    }

    private static bool IsConnected(IDaemonClient client)
        => client switch
        {
            SocketDaemonClient socketClient => socketClient.IsConnected,
            TcpDaemonClient tcpClient => tcpClient.IsConnected,
            _ => false
        };

    private static async Task<DaemonRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer, cancellationToken);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        Assert.InRange(length, 1, 10 * 1024 * 1024);

        var requestBuffer = new byte[length];
        await ReadExactlyAsync(stream, requestBuffer, cancellationToken);
        using var request = JsonDocument.Parse(requestBuffer);
        return new DaemonRequest(
            request.RootElement.GetProperty("id").GetString()!,
            request.RootElement.GetProperty("type").GetString()!);
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        string requestId,
        bool success,
        string? message,
        string? error,
        CancellationToken cancellationToken)
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            id = requestId,
            success,
            message,
            error,
            completedAt = DateTime.UtcNow
        });
        var responseBytes = Encoding.UTF8.GetBytes(responseJson);

        await stream.WriteAsync(BitConverter.GetBytes(responseBytes.Length), cancellationToken);
        await stream.WriteAsync(responseBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, buffer.Length - totalRead),
                cancellationToken);
            if (read == 0)
            {
                throw new IOException("Connection closed before a complete frame was received.");
            }

            totalRead += read;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed record DaemonRequest(string Id, string Type);

    private sealed class LoopbackEndpoint : IDisposable
    {
        private readonly Socket _listener;
        private readonly string? _socketPath;

        private LoopbackEndpoint(Socket listener, string? socketPath)
        {
            _listener = listener;
            _socketPath = socketPath;
        }

        public static LoopbackEndpoint Create(bool useTcp)
        {
            if (useTcp)
            {
                var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                listener.Listen(4);
                return new LoopbackEndpoint(listener, null);
            }

            var socketPath = Path.Combine(Path.GetTempPath(), $"lcm_{Guid.NewGuid():N}.sock");
            var unixListener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            unixListener.Bind(new UnixDomainSocketEndPoint(socketPath));
            unixListener.Listen(4);
            return new LoopbackEndpoint(unixListener, socketPath);
        }

        public IDaemonClient CreateClient()
        {
            if (_socketPath != null)
            {
                return new SocketDaemonClient(
                    _socketPath,
                    sharedSecret: null,
                    logger: NullLogger<SocketDaemonClient>.Instance);
            }

            var endpoint = (IPEndPoint)_listener.LocalEndPoint!;
            return new TcpDaemonClient(
                IPAddress.Loopback.ToString(),
                endpoint.Port,
                sharedSecret: null,
                logger: NullLogger<TcpDaemonClient>.Instance);
        }

        public ValueTask<Socket> AcceptAsync(CancellationToken cancellationToken)
            => _listener.AcceptAsync(cancellationToken);

        public void Dispose()
        {
            _listener.Dispose();
            if (_socketPath != null)
            {
                try
                {
                    File.Delete(_socketPath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }
}
