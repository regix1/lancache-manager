using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services.SteamPrefill;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Locks the one real unknown from the anonymous-service (BattleNet/Riot) scheduled-run
/// investigation: whether the step-2b live-status poll in
/// <c>ScheduledPrefillService.RunServiceAsync</c> (<c>status?.Status == "logged-in"</c>) would
/// wrongly skip anonymous scheduled runs. Per the daemon scout, BattleNet's and Riot's
/// <c>status</c> handler is hardcoded to report <c>IsLoggedIn=true</c> (there is no login concept
/// for an anonymous service). <see cref="SocketDaemonClient"/> is the SAME client class every
/// daemon type uses - there is no per-service branch in <see cref="SocketDaemonClient.GetStatusAsync"/>,
/// it just maps the generic <c>isLoggedIn</c> field to <c>Status = "logged-in"/"not-logged-in"</c>.
/// This test drives a real Unix-domain-socket fake daemon that answers exactly like an anonymous
/// daemon's <c>status</c> handler would, over the production length-prefixed JSON protocol, and
/// proves the mapped <c>Status</c> is "logged-in" - i.e. the live-status gate does NOT skip
/// anonymous scheduled runs. No production code changes: this is a verification test locking a
/// "no gap found" finding, not a regression fix.
/// </summary>
public class ScheduledPrefillAnonymousStatusPollTests
{
    [Fact]
    public async Task GetStatusAsync_AnonymousDaemonStatusShape_MapsToLoggedIn()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"fakedaemon_{Guid.NewGuid():N}.sock");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        var serverTask = RunFakeAnonymousDaemonAsync(listener);

        try
        {
            using var client = new SocketDaemonClient(socketPath, sharedSecret: null, logger: NullLogger<SocketDaemonClient>.Instance);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var status = await client.GetStatusAsync(cts.Token);

            Assert.NotNull(status);
            // The exact string ScheduledPrefillService.RunServiceAsync's step-2b poll checks:
            // `status?.Status == "logged-in"`.
            Assert.Equal("logged-in", status!.Status);

            await serverTask;
        }
        finally
        {
            try { listener.Close(); } catch { /* best-effort cleanup */ }
            try { File.Delete(socketPath); } catch { /* best-effort cleanup */ }
        }
    }

    // Emulates exactly what BattleNet's/Riot's HandleStatus does per daemon-findings.md
    // ("Anonymous -> always IsLoggedIn=true"), over the same length-prefixed JSON protocol every
    // daemon (Steam/Epic/Xbox/BattleNet/Riot) speaks through the shared SocketDaemonClient.
    private static async Task RunFakeAnonymousDaemonAsync(Socket listener)
    {
        using var connection = await listener.AcceptAsync();
        using var stream = new NetworkStream(connection, ownsSocket: false);

        var lengthBuffer = new byte[4];
        await ReadExactlyAsync(stream, lengthBuffer);
        var length = BitConverter.ToInt32(lengthBuffer, 0);
        var requestBuffer = new byte[length];
        await ReadExactlyAsync(stream, requestBuffer);

        using var request = JsonDocument.Parse(Encoding.UTF8.GetString(requestBuffer));
        var requestId = request.RootElement.GetProperty("id").GetString();

        var responseJson = JsonSerializer.Serialize(new
        {
            id = requestId,
            success = true,
            data = new { isLoggedIn = true, isInitialized = true },
            completedAt = DateTime.UtcNow
        });

        var responseBytes = Encoding.UTF8.GetBytes(responseJson);
        await stream.WriteAsync(BitConverter.GetBytes(responseBytes.Length));
        await stream.WriteAsync(responseBytes);
        await stream.FlushAsync();
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
            if (read == 0)
            {
                throw new IOException("Fake daemon connection closed before the response was sent.");
            }

            totalRead += read;
        }
    }
}
