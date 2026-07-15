using LancacheManager.Core.Services.SteamPrefill;

namespace LancacheManager.Tests;

/// <summary>
/// Pins the REAL SocketDaemonClient/TcpDaemonClient shared-challenge-waiter ownership: the login
/// command and the challenge poll share ONE waiter slot, so without ownership a concurrent poll
/// takes over the slot and receives the login's challenge while the login orphans to its timeout.
/// While a login command's waiter is installed, a poll must report "no challenge" instead. Feeds a
/// synthetic credential-challenge frame through the internal ProcessMessage seam (the same idiom as
/// <see cref="DaemonClientEventDrainTests"/>), so no socket connection is needed.
/// </summary>
public class DaemonClientChallengeWaiterTests
{
    private const string ChallengeFrame =
        """{"type":"credential-challenge","data":{"challengeId":"chal-waiter-1","credentialType":"username"}}""";

    private static SocketDaemonClient NewSocketClient() => new("/nonexistent.sock", "secret", null);
    private static TcpDaemonClient NewTcpClient() => new("127.0.0.1", 45556, "secret", null);

    [Fact]
    public Task SocketDaemonClient_PollCannotTakeOverLoginWaiter()
    {
        var client = NewSocketClient();
        return AssertPollCannotTakeOverLoginWaiter(client, client.ProcessMessage);
    }

    [Fact]
    public Task TcpDaemonClient_PollCannotTakeOverLoginWaiter()
    {
        var client = NewTcpClient();
        return AssertPollCannotTakeOverLoginWaiter(client, client.ProcessMessage);
    }

    [Fact]
    public Task SocketDaemonClient_LoginSupersedesEarlierPoll_PollCleanupDoesNotStrandTheLoginChallenge()
    {
        var client = NewSocketClient();
        return AssertLoginSupersedesEarlierPoll(client, client.ProcessMessage);
    }

    [Fact]
    public Task TcpDaemonClient_LoginSupersedesEarlierPoll_PollCleanupDoesNotStrandTheLoginChallenge()
    {
        var client = NewTcpClient();
        return AssertLoginSupersedesEarlierPoll(client, client.ProcessMessage);
    }

    // The ownership HANDOFF direction: a poll that was already waiting is superseded by a login
    // command (StartLoginAsync's ClearPendingChallenges cancels the poll's waiter, then the login
    // installs its own - both in the call's synchronous prefix). The superseded poll's cleanup must
    // release only its OWN waiter: unconditionally clearing the shared slot would wipe the login's
    // waiter and strand the login's challenge in the queue until the login timed out.
    private static async Task AssertLoginSupersedesEarlierPoll(IDaemonClient client, Action<string> processMessage)
    {
        // The poll enters FIRST and installs its waiter (no login in flight, so the ownership guard
        // does not apply to it).
        var pollTask = client.WaitForChallengeAsync(TimeSpan.FromSeconds(5));

        // The login supersedes the poll's waiter with its own.
        var loginTask = client.StartLoginAsync(TimeSpan.FromSeconds(5));

        // Drain the superseded poll COMPLETELY, so its finally block has run before the challenge
        // arrives - this is the window where an unconditional slot-clear would destroy the login's
        // waiter.
        var poll = await pollTask;
        Assert.Null(poll);

        // The daemon's challenge must reach the login command's waiter, not be lost to a wiped slot.
        processMessage(ChallengeFrame);
        var login = await loginTask;
        Assert.NotNull(login);
        Assert.Equal("chal-waiter-1", login!.ChallengeId);

        client.Dispose();
    }

    private static async Task AssertPollCannotTakeOverLoginWaiter(IDaemonClient client, Action<string> processMessage)
    {
        // The login command installs the shared waiter synchronously before its first await, so the
        // waiter is in place once the call returns its task (the command send itself fails silently
        // here - no live socket - without touching the waiter).
        var loginTask = client.StartLoginAsync(TimeSpan.FromSeconds(5));

        // A concurrent poll (the UI/reconcile challenge GET surface) must not take over the slot; it
        // reports no-challenge instead of waiting on (and later stealing) the login's answer.
        var pollTask = client.WaitForChallengeAsync(TimeSpan.FromSeconds(5));

        // The daemon's challenge completes whoever holds the slot. ProcessMessage dispatches the
        // frame; with ownership intact that is the login command's waiter.
        processMessage(ChallengeFrame);

        var poll = await pollTask;
        Assert.Null(poll); // the poll never received the login's challenge

        var login = await loginTask;
        Assert.NotNull(login); // the login command did
        Assert.Equal("chal-waiter-1", login!.ChallengeId);

        client.Dispose();
    }
}
