using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the REAL SocketDaemonClient/TcpDaemonClient event-drain wiring (not a fake): a dispatched event is
/// tracked so DrainEventsAsync waits for it, EVERY multicast subscriber is awaited (not just the last -
/// the login-path escape), and events dispatched after draining begins are rejected. These feed a
/// synthetic frame through the internal ProcessMessage seam, so no socket connection is needed. Reverting
/// either client's tracker wiring or its multicast-safe dispatch makes these fail.
/// </summary>
public class DaemonClientEventDrainTests
{
    // status-update frame: the whole root deserializes to DaemonStatus and fans out to OnStatusUpdate.
    private const string StatusUpdateFrame = """{"type":"status-update","status":"logged-in"}""";

    private static SocketDaemonClient NewSocketClient() => new("/nonexistent.sock", "secret", null);
    private static TcpDaemonClient NewTcpClient() => new("127.0.0.1", 45555, "secret", null);

    [Fact]
    public Task SocketDaemonClient_TracksDispatchedEvent_DrainWaitsForIt()
    {
        var client = NewSocketClient();
        return AssertDrainWaitsForInFlightHandler(client, client.ProcessMessage);
    }

    [Fact]
    public Task TcpDaemonClient_TracksDispatchedEvent_DrainWaitsForIt()
    {
        var client = NewTcpClient();
        return AssertDrainWaitsForInFlightHandler(client, client.ProcessMessage);
    }

    [Fact]
    public Task SocketDaemonClient_DrainWaitsForEverySubscriber_NotJustTheLast()
    {
        var client = NewSocketClient();
        return AssertDrainWaitsForEverySubscriber(client, client.ProcessMessage);
    }

    [Fact]
    public Task TcpDaemonClient_DrainWaitsForEverySubscriber_NotJustTheLast()
    {
        var client = NewTcpClient();
        return AssertDrainWaitsForEverySubscriber(client, client.ProcessMessage);
    }

    [Fact]
    public Task SocketDaemonClient_RejectsEventDispatchedAfterDrainingBegins()
    {
        var client = NewSocketClient();
        return AssertRejectsPostDrainEvent(client, client.ProcessMessage);
    }

    [Fact]
    public Task TcpDaemonClient_RejectsEventDispatchedAfterDrainingBegins()
    {
        var client = NewTcpClient();
        return AssertRejectsPostDrainEvent(client, client.ProcessMessage);
    }

    private static async Task AssertDrainWaitsForInFlightHandler(IDaemonClient client, Action<string> processMessage)
    {
        var gate = new TaskCompletionSource();
        client.OnStatusUpdate += async _ => await gate.Task;

        processMessage(StatusUpdateFrame);

        var drain = client.DrainEventsAsync(TimeSpan.FromSeconds(5));
        Assert.False(drain.IsCompleted); // the client's tracker is holding the in-flight handler task

        gate.SetResult();
        await drain; // completes only once the handler finishes
    }

    private static async Task AssertDrainWaitsForEverySubscriber(IDaemonClient client, Action<string> processMessage)
    {
        var firstGate = new TaskCompletionSource();
        var firstCompleted = false;
        // First subscriber is the real, slow one; the last mirrors login's temporary fail-fast handler.
        client.OnStatusUpdate += async _ => { await firstGate.Task; firstCompleted = true; };
        client.OnStatusUpdate += _ => Task.CompletedTask;

        processMessage(StatusUpdateFrame);

        var drain = client.DrainEventsAsync(TimeSpan.FromSeconds(5));
        // A bare multicast Invoke returns only the last (already-complete) subscriber's task, so the drain
        // would finish immediately and the first subscriber would escape; the safe dispatcher awaits both.
        Assert.False(drain.IsCompleted);

        firstGate.SetResult();
        await drain;
        Assert.True(firstCompleted);
    }

    private static async Task AssertRejectsPostDrainEvent(IDaemonClient client, Action<string> processMessage)
    {
        var handlerRan = false;
        client.OnStatusUpdate += _ => { handlerRan = true; return Task.CompletedTask; };

        await client.DrainEventsAsync(TimeSpan.FromSeconds(5)); // draining begins (no in-flight tasks)
        processMessage(StatusUpdateFrame);

        Assert.False(handlerRan); // admission rejected the post-drain event; the handler never ran
    }
}
