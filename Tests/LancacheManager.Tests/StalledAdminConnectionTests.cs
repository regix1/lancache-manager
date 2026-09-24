using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One admin's browser that stops reading, or reads slower than updates arrive, must never hold an
/// update back from any other browser. These run the real notification service, tracker and download
/// hub manager over in-memory connections whose reading the test controls byte for byte.
/// </summary>
public sealed class StalledAdminConnectionTests
{
    private const string Probe = "Probe";
    private const byte RecordSeparator = 0x1E;

    // Any unread byte holds the next flush, so each write waits until the one before it was read.
    private static readonly PipeOptions SlowReader = new(pauseWriterThreshold: 1, resumeWriterThreshold: 1);

    [Fact]
    public async Task AnUnreadBrowserHoldsBackNoRowFromAnotherAndGetsEveryRowInOrderOnceItReads()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var notifications = ActivatorUtilities.CreateInstance<SignalRNotificationService>(services);
        var healthy = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);
        var slow = await ConnectAsync(manager, DownloadHub.AdminGroup, SlowReader);
        var tracker = new UnifiedOperationTracker(new ProcessManager(NullLogger<ProcessManager>.Instance),
            NullLogger<UnifiedOperationTracker>.Instance, notifications);

        // Registration, two refreshes and the ending each publish one row.
        var id = tracker.RegisterOperation(OperationType.GameDetection, "Game Detection", new CancellationTokenSource());
        tracker.UpdateProgress(id, 50, "Scanning");
        tracker.RefreshRun(id);
        tracker.UpdateProgress(id, 75, "Scanning");
        tracker.RefreshRun(id);
        tracker.CompleteOperation(id, success: true);

        var sent = new List<long>();
        for (var row = 0; row < 4; row++)
        {
            sent.Add(Revision(await ReadMessageAsync(healthy.Received, deadline.Token)));
        }

        Assert.Equal([1L, 2L, 3L, 4L], sent);
        Assert.Equal(1, CountUnread(slow.Received));
        Assert.Equal(0, slow.CloseRequests.Count);

        var late = new List<long>();
        for (var row = 0; row < 4; row++)
        {
            late.Add(Revision(await ReadMessageAsync(slow.Received, deadline.Token)));
        }

        Assert.Equal(sent, late);
    }

    [Fact]
    public async Task ASlowBrowserNeverDelaysASendAndIsAskedToCloseOnceItFallsTooFarBehind()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var notifications = ActivatorUtilities.CreateInstance<SignalRNotificationService>(services);
        var healthy = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);
        var slow = await ConnectAsync(manager, DownloadHub.AdminGroup, SlowReader);

        const int sends = 2 * DownloadHubLifetimeManager.MaxQueuedWrites + 64;
        var readBeforeClose = 0;
        for (var i = 0; i < sends; i++)
        {
            var sent = notifications.NotifyAdminAsync(Probe, i);
            Assert.True(sent.IsCompletedSuccessfully, $"Send {i} waited for the slow browser.");
            Assert.Equal(i, Argument(await ReadMessageAsync(healthy.Received, deadline.Token)).GetInt32());

            if (i % 2 == 1 && slow.CloseRequests.Count == 0)
            {
                await ReadMessageAsync(slow.Received, deadline.Token);
                readBeforeClose++;

                // Wait for the next queued write to land, so no write is mid-copy when a send runs.
                var next = await slow.Received.ReadAsync(deadline.Token);
                slow.Received.AdvanceTo(next.Buffer.Start);
            }
        }

        Assert.Equal(1, slow.CloseRequests.Count);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (slow.Hub.ConnectionAborted.Register(() => aborted.TrySetResult()))
        {
            await aborted.Task.WaitAsync(deadline.Token);
        }

        // The close came through the request that allows reconnecting, not the transport abort.
        Assert.False(slow.Context.ConnectionClosed.IsCancellationRequested);
        var unread = CountUnread(slow.Received);
        Assert.InRange(readBeforeClose + unread, readBeforeClose, readBeforeClose + DownloadHubLifetimeManager.MaxQueuedWrites + 1);

        await notifications.NotifyAdminAsync(Probe, sends);
        Assert.Equal(sends, Argument(await ReadMessageAsync(healthy.Received, deadline.Token)).GetInt32());
        Assert.Equal(unread, CountUnread(slow.Received));
        Assert.Equal(1, slow.CloseRequests.Count);
    }

    [Fact]
    public async Task AGuestNeverReceivesAnAdminUpdateAndADisconnectedBrowserReceivesNothingMore()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var notifications = ActivatorUtilities.CreateInstance<SignalRNotificationService>(services);
        Assert.IsType<DownloadHubLifetimeManager>(manager);
        var admin = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);
        var guest = await ConnectAsync(manager, DownloadHub.GuestGroup, PipeOptions.Default);
        var leaving = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);

        await notifications.NotifyAdminAsync(Probe, "admin only");
        await notifications.NotifyAllAsync(Probe, "everyone");

        // Writes to a connection land in send order, so the guest's first message is the admin one
        // if it received it at all.
        Assert.Equal("everyone", Argument(await ReadMessageAsync(guest.Received, deadline.Token)).GetString());
        foreach (var expected in new[] { "admin only", "everyone" })
        {
            Assert.Equal(expected, Argument(await ReadMessageAsync(admin.Received, deadline.Token)).GetString());
            Assert.Equal(expected, Argument(await ReadMessageAsync(leaving.Received, deadline.Token)).GetString());
        }

        await manager.OnDisconnectedAsync(leaving.Hub);
        await notifications.NotifyAllAsync(Probe, "after leaving");
        await notifications.NotifyAdminAsync(Probe, "admin after leaving");
        Assert.Equal("after leaving", Argument(await ReadMessageAsync(admin.Received, deadline.Token)).GetString());
        Assert.Equal("admin after leaving", Argument(await ReadMessageAsync(admin.Received, deadline.Token)).GetString());
        Assert.Equal(0, CountUnread(leaving.Received));
    }

    [Fact]
    public async Task ASendThatCannotSerializeHoldsNoQueuedWrite()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var healthy = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);

        // The JSON protocol refuses NaN, so each of these sends throws.
        var errors = new List<Exception?>();
        for (var i = 0; i <= DownloadHubLifetimeManager.MaxQueuedWrites; i++)
        {
            errors.Add(await Record.ExceptionAsync(() => manager.SendAllAsync(Probe, [double.NaN])));
        }

        Assert.Equal(0, healthy.CloseRequests.Count);
        Assert.All(errors, Assert.NotNull);
        await manager.SendAllAsync(Probe, ["valid"]);
        Assert.Equal("valid", Argument(await ReadMessageAsync(healthy.Received, deadline.Token)).GetString());
    }

    // Pins the ordering the manager already gives: concurrent senders each keep their own order.
    [Fact]
    public async Task TwoSendersKeepEachSendersOwnOrder()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var slow = await ConnectAsync(manager, DownloadHub.AdminGroup, SlowReader);

        string[] senders = ["first", "second"];
        using var start = new Barrier(senders.Length);
        await Task.WhenAll(senders.Select(sender => Task.Run(async () =>
        {
            start.SignalAndWait();
            for (var index = 0; index < 200; index++)
            {
                await manager.SendAllAsync(Probe, [$"{sender} {index}"]);
            }
        })));

        var received = new List<string>();
        for (var i = 0; i < 400; i++)
        {
            received.Add(Argument(await ReadMessageAsync(slow.Received, deadline.Token)).GetString()!);
        }

        foreach (var sender in senders)
        {
            Assert.Equal(
                Enumerable.Range(0, 200).Select(index => $"{sender} {index}"),
                received.Where(tag => tag.StartsWith($"{sender} ", StringComparison.Ordinal)));
        }
    }

    [Fact]
    public async Task RemovingAConnectionFromAGroupIsRefused()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var services = CreateServices();
        var manager = services.GetRequiredService<HubLifetimeManager<DownloadHub>>();
        var admin = await ConnectAsync(manager, DownloadHub.AdminGroup, PipeOptions.Default);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => manager.RemoveFromGroupAsync(admin.Hub.ConnectionId, DownloadHub.AdminGroup));

        await manager.SendGroupAsync(DownloadHub.AdminGroup, Probe, ["one group"]);
        await manager.SendGroupsAsync([DownloadHub.AdminGroup], Probe, ["group list"]);
        Assert.Equal("one group", Argument(await ReadMessageAsync(admin.Received, deadline.Token)).GetString());
        Assert.Equal("group list", Argument(await ReadMessageAsync(admin.Received, deadline.Token)).GetString());
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddSignalR();
        // The download hub registration Program.cs makes.
        services.AddSingleton<HubLifetimeManager<DownloadHub>, DownloadHubLifetimeManager>();
        return services.BuildServiceProvider();
    }

    private static async Task<BrowserConnection> ConnectAsync(
        HubLifetimeManager<DownloadHub> manager, string group, PipeOptions toBrowserOptions)
    {
        var toBrowser = new Pipe(toBrowserOptions);
        var fromBrowser = new Pipe();
        var context = new DefaultConnectionContext(
            Guid.NewGuid().ToString("N"),
            new DuplexPipe(fromBrowser.Reader, toBrowser.Writer),
            new DuplexPipe(toBrowser.Reader, fromBrowser.Writer));

        // Every hub connection over HTTP carries this feature; the hub connection subscribes to it
        // when it is constructed, so it is set first.
        var closeRequests = new CloseRequestFeature();
        context.Features.Set<IConnectionLifetimeNotificationFeature>(closeRequests);
        var hub = new HubConnectionContext(context, new HubConnectionContextOptions(), NullLoggerFactory.Instance)
        {
            Protocol = new JsonHubProtocol()
        };

        await manager.OnConnectedAsync(hub);
        await manager.AddToGroupAsync(hub.ConnectionId, group);
        return new BrowserConnection(hub, context, toBrowser.Reader, closeRequests);
    }

    private static async Task<string> ReadMessageAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await reader.ReadAsync(cancellationToken);
            var buffer = result.Buffer;
            if (buffer.PositionOf(RecordSeparator) is SequencePosition end)
            {
                var message = Encoding.UTF8.GetString(buffer.Slice(0, end));
                reader.AdvanceTo(buffer.GetPosition(1, end));
                return message;
            }

            reader.AdvanceTo(buffer.Start, buffer.End);
        }
    }

    // Counts the messages waiting in the pipe and leaves them there.
    private static int CountUnread(PipeReader reader)
    {
        if (!reader.TryRead(out var result))
        {
            return 0;
        }

        var count = 0;
        foreach (var segment in result.Buffer)
        {
            count += segment.Span.Count(RecordSeparator);
        }

        reader.AdvanceTo(result.Buffer.Start);
        return count;
    }

    private static JsonElement Argument(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("arguments")[0].Clone();
    }

    private static long Revision(string message)
    {
        using var document = JsonDocument.Parse(message);
        Assert.Equal(SignalREvents.OperationUpdated, document.RootElement.GetProperty("target").GetString());
        return document.RootElement.GetProperty("arguments")[0].GetProperty("revision").GetInt64();
    }

    private sealed record DuplexPipe(PipeReader Input, PipeWriter Output) : IDuplexPipe;

    private sealed record BrowserConnection(
        HubConnectionContext Hub,
        DefaultConnectionContext Context,
        PipeReader Received,
        CloseRequestFeature CloseRequests);

    private sealed class CloseRequestFeature : IConnectionLifetimeNotificationFeature
    {
        private readonly CancellationTokenSource _requested = new();

        public CloseRequestFeature() => ConnectionClosedRequested = _requested.Token;

        public CancellationToken ConnectionClosedRequested { get; set; }

        public int Count { get; private set; }

        public void RequestClose()
        {
            Count++;
            _requested.Cancel();
        }
    }
}
