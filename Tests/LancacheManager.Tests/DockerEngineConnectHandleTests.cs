using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LancacheManager.Infrastructure.Services;

namespace LancacheManager.Tests;

/// <summary>
/// Counts the process's open handles, so no other test may open or close any while one runs.
/// </summary>
[CollectionDefinition(nameof(EngineConnectHandleCollection), DisableParallelization = true)]
public sealed class EngineConnectHandleCollection;

/// <summary>
/// Docker answers the gateway's connect and then stops, so every later registry lookup fails to connect to the
/// local engine. A failed connect must close its socket at once instead of leaving it open until the garbage
/// collector gets to it.
/// </summary>
[Collection(nameof(EngineConnectHandleCollection))]
public sealed class DockerEngineConnectHandleTests
{
    [Theory]
    [InlineData("tcp")]
    [InlineData("unix")]
    public async Task DigestLookupsAfterDockerStopsLeaveNoSocketOpenAsync(string scheme)
    {
        const int lookups = 64;
        using var gateway = new DockerPrefillContainerGateway();
        var socketPath = Path.Combine(Path.GetTempPath(), $"engine-{Guid.NewGuid():N}.sock");
        try
        {
            Uri dockerUri;
            using (var engine = scheme == "unix"
                ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                if (scheme == "unix")
                {
                    engine.Bind(new UnixDomainSocketEndPoint(socketPath));
                    dockerUri = new Uri($"unix://{socketPath}");
                }
                else
                {
                    engine.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                    dockerUri = new Uri($"tcp://127.0.0.1:{((IPEndPoint)engine.LocalEndPoint!).Port}");
                }
                engine.Listen();
                var answered = AnswerVersionOnceAsync(engine);
                await gateway.ConnectAsync(dockerUri, CancellationToken.None);
                await answered.WaitAsync(TimeSpan.FromSeconds(10));
            }

            // Docker has stopped: nothing listens at its address any more.
            File.Delete(socketPath);
            var before = OpenHandleCount();
            for (var lookup = 0; lookup < lookups; lookup++)
            {
                await Assert.ThrowsAnyAsync<Exception>(
                    () => gateway.GetDistributionDigestAsync("example/prefill:latest", CancellationToken.None));
            }
            var leftOpen = OpenHandleCount() - before;

            Assert.True(leftOpen < lookups / 2,
                $"{leftOpen} handles were still open after {lookups} registry lookups failed to connect to a stopped "
                + $"Docker over {scheme}.");
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    private static int OpenHandleCount()
    {
        if (OperatingSystem.IsLinux())
        {
            return Directory.GetFileSystemEntries("/proc/self/fd").Length;
        }

        using var process = Process.GetCurrentProcess();
        return process.HandleCount;
    }

    // Reads the version request and answers it, so the gateway keeps its connection to this engine.
    private static async Task AnswerVersionOnceAsync(Socket engine)
    {
        using var connection = await engine.AcceptAsync();
        var request = new StringBuilder();
        var buffer = new byte[1024];
        // Docker.DotNet ends its last header line with a bare line feed on Linux, so the end is "\n\r\n" there.
        while (!request.ToString().Contains("\n\r\n", StringComparison.Ordinal))
        {
            var read = await connection.ReceiveAsync(buffer, SocketFlags.None);
            if (read == 0)
            {
                return;
            }

            request.Append(Encoding.ASCII.GetString(buffer, 0, read));
        }

        const string body = "{\"Version\":\"test-engine\"}";
        var answer = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nConnection: close\r\n"
            + $"Content-Length: {body.Length}\r\n\r\n{body}";
        await connection.SendAsync(Encoding.ASCII.GetBytes(answer), SocketFlags.None);
    }
}
