using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A browser stays connected to the download hub over a real loopback socket but stops reading it, as a
/// suspended laptop or a frozen tab does, while a second browser keeps reading. The server sends live
/// updates the size of a download speed update through the notification service, in bursts, until the stalled
/// browser's socket and buffers are full and beyond. Every send must return at once, and the reading
/// browser must still get the last update. The host has Program.cs's hub setup: its SignalR options and
/// JSON protocol, its download hub manager, and the real download hub with a real session; only its two
/// hub filters, which wrap hub method calls and not sends, are left out.
/// </summary>
public sealed class UnreadSocketBroadcastTests(ITestOutputHelper output)
{
    /// <summary>A download speed update with a dozen games in it serializes to about this many bytes.</summary>
    private const int UpdateBytes = 2048;

    /// <summary>About 16 MB: well past the loopback socket buffers and the transport's 64 KB pipe.</summary>
    private const int Updates = 8000;

    /// <summary>
    /// Updates go out in bursts this size, the size of the largest burst one action sends (Run All). Between
    /// bursts the reading browser reads up to the burst's last update, as it does between real actions; that
    /// wait is not timed. Without it a reader on a loaded test machine can fall a whole queue behind in one
    /// burst of 8000 and be closed, which the app does on purpose.
    /// </summary>
    private const int Burst = 200;

    /// <summary>A send that takes longer than this is held by the stalled browser.</summary>
    private static readonly TimeSpan HeldSend = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ABrowserThatStopsReadingItsSocket_HoldsNoBroadcastBack()
    {
        var measured = await MeasureAsync(appManager: true);
        output.WriteLine(measured.Summary);
        Assert.True(measured.HeldAt is null,
            $"Send {measured.HeldAt} of {Updates} through NotifyAllAsync was still waiting after {HeldSend.TotalSeconds} s " +
            $"behind the browser that stopped reading ({measured.Summary})");
        Assert.True(measured.LastUpdateReached,
            $"The browser that kept reading never got the last update ({measured.Summary})");
    }

    /// <summary>
    /// The same run on SignalR's own hub manager, which a broadcast waits on for every connection, shows the
    /// socket above does fill: without it a pass above could mean only that the buffers never filled.
    /// </summary>
    [Fact]
    public async Task OnTheLibrarysOwnManager_TheSameUnreadSocketHoldsABroadcast()
    {
        var measured = await MeasureAsync(appManager: false);
        output.WriteLine(measured.Summary);
        Assert.True(measured.HeldAt is not null,
            $"The unread socket never held a send on SignalR's own manager ({measured.Summary}), so this run cannot " +
            "tell whether the download hub manager keeps sends from waiting");
    }

    private sealed record Measurement(int? HeldAt, bool LastUpdateReached, string Summary);

    private static async Task<Measurement> MeasureAsync(bool appManager)
    {
        var root = Path.Combine(Path.GetTempPath(), "unread-socket-broadcast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AuthCredentialFormatTests.TempDirPathResolver(root);
        var keys = new ApiKeyService(NullLogger<ApiKeyService>.Instance, new ConfigurationBuilder().Build(), paths);
        var sessions = new SessionService(
            new TestDbContextFactory(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase($"unread_socket_{Guid.NewGuid():N}").Options),
            keys,
            NullLogger<SessionService>.Instance,
            StateTestMethods.CreateStateService(Path.Combine(root, "state")),
            DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>(),
            new ConfigurationBuilder().Build());
        var token = (await sessions.CreateAdminSessionAsync(keys.GetApiKey(), new DefaultHttpContext()))!.Value.RawToken;

        var setup = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        setup.Logging.ClearProviders();
        setup.WebHost.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0));
        // Program.cs's AddSignalR call, without its two hub filters.
        setup.Services.AddSignalR(options =>
        {
            options.KeepAliveInterval = TimeSpan.FromSeconds(10);
            options.ClientTimeoutInterval = TimeSpan.FromMinutes(10);
            options.HandshakeTimeout = TimeSpan.FromSeconds(30);
            options.EnableDetailedErrors = false;
        }).AddJsonProtocol(options =>
        {
            options.PayloadSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            options.PayloadSerializerOptions.DictionaryKeyPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
        });
        if (appManager)
        {
            setup.Services.AddSingleton<HubLifetimeManager<DownloadHub>, DownloadHubLifetimeManager>();
        }
        // The hub's [Authorize] passes; the hub itself still checks the session cookie.
        setup.Services.AddAuthentication(SignedIn.Name).AddScheme<AuthenticationSchemeOptions, SignedIn>(SignedIn.Name, null);
        setup.Services.AddAuthorization();
        setup.Services.AddSingleton(new ConnectionTrackingService(NullLogger<ConnectionTrackingService>.Instance));
        setup.Services.AddSingleton(sessions);
        setup.Services.AddSingleton(DispatchProxy.Create<IActivityRegistry, NullReturningProxy>());
        await using var app = setup.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHub<DownloadHub>("/hubs/downloads");
        await app.StartAsync();

        RawHubSocket? stalled = null;
        RawHubSocket? reading = null;
        try
        {
            var port = new Uri(app.Urls.First()).Port;
            var notifications = ActivatorUtilities.CreateInstance<SignalRNotificationService>(app.Services);
            stalled = await RawHubSocket.ConnectAsync(port, token, receiveBufferBytes: 4096);
            reading = await RawHubSocket.ConnectAsync(port, token, receiveBufferBytes: 65536);
            reading.KeepReading();

            // Both browsers are in the hub once each has read an update; then the first stops reading for good.
            await RepeatUntilAsync(notifications, "ready", stalled.ReadUntilAsync);
            await RepeatUntilAsync(notifications, "ready-reading", reading.WaitForAsync);

            var times = new List<double>(Updates);
            int? heldAt = null;
            int? readerBehindAt = null;
            for (var update = 0; update < Updates; update++)
            {
                var burstEnds = (update + 1) % Burst == 0;
                var marker = burstEnds ? $"burst-{update + 1}-end" : "traffic";
                var caughtUp = burstEnds ? reading.WaitForAsync(marker) : null;
                var clock = Stopwatch.StartNew();
                var send = notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, Update(update, marker));
                if (await Task.WhenAny(send, Task.Delay(HeldSend)) != send)
                {
                    heldAt = update + 1;
                    break;
                }

                await send;
                times.Add(clock.Elapsed.TotalMilliseconds);
                if (caughtUp is not null && await Task.WhenAny(caughtUp, Task.Delay(Timeout)) != caughtUp)
                {
                    readerBehindAt = update + 1;
                    break;
                }
            }

            var lastUpdateReached = false;
            if (heldAt is null && readerBehindAt is null)
            {
                var last = reading.WaitForAsync("last-update");
                await notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, Update(Updates, "last-update"));
                lastUpdateReached = await Task.WhenAny(last, Task.Delay(Timeout)) == last;
            }

            times.Sort();
            var summary = $"{(appManager ? "download hub manager" : "SignalR's own manager")}: {times.Count} of {Updates} sends of " +
                $"{UpdateBytes} bytes returned ({times.Count * (long)UpdateBytes / 1024} KB), " +
                $"in bursts of {Burst}, " +
                (heldAt is null ? "none held" : $"send {heldAt} held over {HeldSend.TotalSeconds} s") +
                (readerBehindAt is null ? string.Empty : $", the reading browser never read send {readerBehindAt}") +
                (times.Count == 0
                    ? string.Empty
                    : $"; per send median {times[times.Count / 2]:F3} ms, 99th {times[(int)(times.Count * 0.99)]:F3} ms, " +
                      $"max {times[^1]:F3} ms");
            return new Measurement(heldAt, lastUpdateReached, summary);
        }
        finally
        {
            // Closing the stalled socket ends a send that still waits on it.
            stalled?.Dispose();
            reading?.Dispose();
            using var stop = new CancellationTokenSource(Timeout);
            await app.StopAsync(stop.Token);
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Sends the marker as an update until <paramref name="seen"/> reports the browser read it.</summary>
    private static async Task RepeatUntilAsync(
        SignalRNotificationService notifications, string marker, Func<string, Task> seen)
    {
        var read = seen(marker);
        var until = DateTime.UtcNow + Timeout;
        while (!read.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < until, $"No browser read the {marker} update, so it never joined the hub");
            await notifications.NotifyAllAsync(SignalREvents.DownloadSpeedUpdate, Update(0, marker));
            await Task.WhenAny(read, Task.Delay(50));
        }

        await read;
    }

    private static object Update(int sequence, string marker) =>
        new { sequence, marker, games = new string('g', UpdateBytes - 64) };

    /// <summary>Signs every request in, so the hub's [Authorize] attribute passes.</summary>
    private sealed class SignedIn(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "SignedIn";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(Name)), Name)));
    }

    /// <summary>
    /// A browser's download hub connection over a raw socket: the WebSocket upgrade with the session cookie and
    /// the SignalR handshake, then reads only when told to. Frames from the server are not masked, so a marker
    /// in a text message shows up as its own bytes in what is read.
    /// </summary>
    private sealed class RawHubSocket : IDisposable
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly StringBuilder _read = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _waiting = new();

        private RawHubSocket(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RawHubSocket> ConnectAsync(int port, string token, int receiveBufferBytes)
        {
            var client = new TcpClient { ReceiveBufferSize = receiveBufferBytes };
            await client.ConnectAsync(IPAddress.Loopback, port);
            var socket = new RawHubSocket(client);
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            await socket._stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"GET /hubs/downloads HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                $"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\nCookie: LancacheManager.Session={token}\r\n\r\n"));
            await socket.ReadUntilAsync("\r\n\r\n");
            Assert.Contains("101", socket.Taken());
            await socket.SendTextAsync("{\"protocol\":\"json\",\"version\":1}\u001e");
            return socket;
        }

        private string Taken()
        {
            lock (_read)
            {
                var text = _read.ToString();
                _read.Clear();
                return text;
            }
        }

        /// <summary>Reads only until <paramref name="marker"/> has been read, then reads nothing more.</summary>
        public async Task ReadUntilAsync(string marker)
        {
            var buffer = new byte[1024];
            using var deadline = new CancellationTokenSource(Timeout);
            while (true)
            {
                lock (_read)
                {
                    if (_read.ToString().Contains(marker, StringComparison.Ordinal))
                    {
                        return;
                    }
                }

                var count = await _stream.ReadAsync(buffer, deadline.Token);
                Assert.True(count > 0, $"The server closed the connection before {marker} arrived");
                lock (_read) _read.Append(Encoding.Latin1.GetString(buffer, 0, count));
            }
        }

        /// <summary>Reads everything the server sends from now on, as a browser that keeps up does.</summary>
        public void KeepReading() => _ = Task.Run(async () =>
        {
            var buffer = new byte[65536];
            try
            {
                int count;
                while ((count = await _stream.ReadAsync(buffer)) > 0)
                {
                    string text;
                    lock (_read)
                    {
                        _read.Append(Encoding.Latin1.GetString(buffer, 0, count));
                        // Only the tail can still hold a marker cut across two reads.
                        if (_read.Length > 8192) _read.Remove(0, _read.Length - 8192);
                        text = _read.ToString();
                    }

                    foreach (var (marker, waiter) in _waiting)
                    {
                        if (text.Contains(marker, StringComparison.Ordinal)) waiter.TrySetResult();
                    }
                }
            }
            catch (IOException)
            {
                // The test closed the socket.
            }
            catch (ObjectDisposedException)
            {
                // The test closed the socket.
            }
        });

        /// <summary>Completes when the reading loop of <see cref="KeepReading"/> has read <paramref name="marker"/>.</summary>
        public Task WaitForAsync(string marker) =>
            _waiting.GetOrAdd(marker, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        private async Task SendTextAsync(string text)
        {
            var body = Encoding.UTF8.GetBytes(text);
            var mask = RandomNumberGenerator.GetBytes(4);
            var frame = new byte[2 + 4 + body.Length];
            frame[0] = 0x81;
            frame[1] = (byte)(0x80 | body.Length);
            mask.CopyTo(frame, 2);
            for (var index = 0; index < body.Length; index++)
            {
                frame[6 + index] = (byte)(body[index] ^ mask[index % 4]);
            }

            await _stream.WriteAsync(frame);
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }
}
