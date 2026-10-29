using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class PrefillSessionServiceTests
{
    // A Prefill History reset that deletes a running prefill's game rows while it saves progress does not
    // drop that save: the rows are written again and the run moves on.
    [Fact]
    public async Task ProgressSaveWritesItsGamesAgainAfterAHistoryResetDeletedThemAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        var run = await fixture.StartAsync("10");
        var first = Progress(run, run.Snapshot.Sequence + 1, 10);
        Assert.True((await fixture.History.SaveRunAsync(run, first.Snapshot, [first.Item], false, CancellationToken.None)).Applied);
        var reset = new BeforeFirstSave(async () =>
        {
            await using var context = new AppDbContext(fixture.Options);
            await context.PrefillHistoryEntries.ExecuteDeleteAsync();
        });
        var options = new DbContextOptionsBuilder<AppDbContext>(fixture.Options).AddInterceptors(reset).Options;
        var history = new PrefillSessionService(new TestDbContextFactory(options), NullLogger<PrefillSessionService>.Instance);
        var second = Progress(run, run.Snapshot.Sequence + 2, 40);

        var saved = await history.SaveRunAsync(run, second.Snapshot, [second.Item], false, CancellationToken.None);

        Assert.True(reset.Ran);
        Assert.True(saved.Applied);
        Assert.Equal("10", Assert.Single(saved.Items).AppId);
        await using var verify = new AppDbContext(fixture.Options);
        var entry = await verify.PrefillHistoryEntries.SingleAsync(row => row.RunId == run.PrefillRunId);
        Assert.Equal(40, entry.BytesDownloaded);
        Assert.Equal(second.Snapshot.Sequence, (await verify.PrefillRuns.SingleAsync(row => row.Id == run.PrefillRunId)).Sequence);
    }

    // A Prefill Sessions reset that deleted an ended session's row takes its running prefill's row with it;
    // the prefill still ends on its card when the daemon finishes it.
    [Fact]
    public async Task RunWhoseRowAResetDeletedEndsWhenTheDaemonFinishesItAsync()
    {
        await using var fixture = await RunFixture.CreateAsync(relational: true);
        var run = await fixture.StartAsync("10");
        await using (var context = new AppDbContext(fixture.Options))
        {
            await context.PrefillSessions.Where(row => row.SessionId == fixture.Session.Id).ExecuteDeleteAsync();
        }
        fixture.Client.Set(run, "completed", 100, "success");

        await fixture.RefreshAsync();

        var finished = await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("completed", finished.Snapshot.State);
        Assert.Equal(PrefillState.Completed, run.PrefillState);
    }

    // Reading the container list reads the daemon's status, which runs the recovery pass, and the page reads the
    // list again on a session update. A read that changed nothing sends no update, so the reads do not feed
    // each other.
    [Fact]
    public async Task StatusReadThatChangesNothingSendsNoSessionUpdateAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var recorder = Notifications(fixture);
        recorder.Invocations.Clear();

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        Assert.DoesNotContain(recorder.Invocations,
            call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
    }

    // A change another path made without sending its own update goes out with the next status read.
    [Fact]
    public async Task StatusReadSendsAChangeNoUpdateCarriedYetAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var recorder = Notifications(fixture);
        recorder.Invocations.Clear();
        fixture.Session.ContainerName = "renamed-container";

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        Assert.Single(recorder.Invocations,
            call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
    }

    // A dropped daemon socket sends the session as recovering. The read after the reconnect ends the
    // recovery, and that update reaches the page even though the pass sent the same state before the drop.
    [Fact]
    public async Task ReadAfterADroppedSocketSendsTheSessionOutOfRecoveryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var recorder = Notifications(fixture);
        fixture.Client.Offline = true;
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnDisconnectedAsync",
            fixture.Session, fixture.Session.Client);
        fixture.Client.Offline = false;

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        var last = recorder.Invocations.Last(call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.False(Assert.IsType<DaemonSessionDto>(last.Args[1]).Recovering);
    }

    // Two session updates in flight at once, the earlier one held in its send: the drop's update reaches the pages
    // last, so the read after the reconnect still sends the session out of recovery.
    [Fact]
    public async Task OverlappingSessionUpdatesStillLetTheReadAfterADropEndTheRecoveryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, HeldAdminSendProxy>();
        var held = (HeldAdminSendProxy)(object)notifications;
        typeof(PrefillDaemonServiceBase).GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Daemon, notifications);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.Hold = release.Task;
        var earlier = DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "NotifyHubAsync",
            fixture.Session, SignalREvents.DaemonSessionUpdated, DaemonSessionDto.FromSession(fixture.Session));
        fixture.Client.Offline = true;
        var drop = DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnDisconnectedAsync",
            fixture.Session, fixture.Session.Client);
        release.SetResult();
        await Task.WhenAll(earlier, drop).WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Client.Offline = false;

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        var last = held.Invocations.Last(call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.False(Assert.IsType<DaemonSessionDto>(last.Args[1]).Recovering);
    }

    // A status read that failed marks the session recovering without sending, and a page that opens meanwhile
    // reads it so. The read after it ends the recovery and sends that, though the last update said the same.
    [Fact]
    public async Task ReadAfterAFailedReadSendsTheSessionOutOfRecoveryAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var recorder = Notifications(fixture);
        fixture.Client.StatusRead = () => Task.FromException<DaemonStatus?>(new IOException("No answer"));
        await fixture.Daemon.RefreshRunsAsync(fixture.Session.Id);
        Assert.True(fixture.Session.Recovering);
        fixture.Client.StatusRead = null;
        recorder.Invocations.Clear();

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        var update = Assert.Single(recorder.Invocations,
            call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.False(Assert.IsType<DaemonSessionDto>(update.Args[1]).Recovering);
    }

    // A status read sent while a sign-in was still running answers "not signed in". When that sign-in has
    // finished signed in before the answer is applied, the container stays signed in and is not marked as
    // logged off.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NotSignedInAnswerFromBeforeASignInFinishedLeavesTheContainerSignedInAsync(bool sweep)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        fixture.Session.AuthState = DaemonAuthState.NotAuthenticated;
        var signedOut = RunClient.Capabilities(fixture.Client.InstanceId);
        signedOut.Status = "not-logged-in";
        var readSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<DaemonStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.StatusRead = () =>
        {
            readSent.TrySetResult();
            return answer.Task;
        };
        Task read = sweep
            ? fixture.Daemon.RefreshRunsAsync(fixture.Session.Id)
            : fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        await readSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "ApplyStatusAsync",
            fixture.Session, RunClient.Capabilities(fixture.Client.InstanceId), null);
        Assert.Equal(DaemonAuthState.Authenticated, fixture.Session.AuthState);

        answer.SetResult(signedOut);
        await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonAuthState.Authenticated, fixture.Session.AuthState);
        Assert.False(fixture.Session.SignedOut);
    }

    // A Steam log-off pushed while a status read is in flight signs the container out. The read's "signed in"
    // answer, applied after the log-off, is older than it and leaves the container signed out.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SignedInAnswerFromBeforeALogOffLeavesTheContainerSignedOutAsync(bool sweep)
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        var readSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<DaemonStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.StatusRead = () =>
        {
            readSent.TrySetResult();
            return answer.Task;
        };
        Task read = sweep
            ? fixture.Daemon.RefreshRunsAsync(fixture.Session.Id)
            : fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        await readSent.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var loggedOff = RunClient.Capabilities(fixture.Client.InstanceId);
        loggedOff.Status = "awaiting-login";
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "ApplyStatusAsync",
            fixture.Session, loggedOff, null);
        Assert.Equal(DaemonAuthState.NotAuthenticated, fixture.Session.AuthState);

        answer.SetResult(RunClient.Capabilities(fixture.Client.InstanceId));
        await read.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(DaemonAuthState.NotAuthenticated, fixture.Session.AuthState);
        Assert.True(fixture.Session.SignedOut);
    }

    // A daemon socket that drops after the status read a recovery pass works from leaves the session recovering:
    // the pass does not end the recovery from that older read while the daemon is unreachable.
    [Fact]
    public async Task PassFromAReadBeforeADropLeavesTheSessionRecoveringAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var status = RunClient.Capabilities(fixture.Client.InstanceId);
        using var dropped = new SocketDaemonClient(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.sock"))
        {
            SignInLostStageKey = "errors.steam.signInLost"
        };
        fixture.Session.Client = dropped;
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnDisconnectedAsync", fixture.Session, dropped);
        Assert.True(fixture.Session.Recovering);
        var recorder = Notifications(fixture);
        recorder.Invocations.Clear();

        await fixture.Daemon.RecoverRunsAsync(fixture.Session, status, CancellationToken.None);

        Assert.True(fixture.Session.Recovering);
        Assert.DoesNotContain(recorder.Invocations, call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated)
            && !Assert.IsType<DaemonSessionDto>(call.Args[1]).Recovering);
    }

    // A session update built before the daemon socket dropped and sent after the drop's own update does not
    // reach the pages last with the older state: the page and the account holders see the session recovering.
    [Fact]
    public async Task SessionUpdateBuiltBeforeADropDoesNotReachThePagesAfterTheDropsUpdateAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        fixture.Daemon.AddSubscriber(fixture.Session.Id, "page-1");
        var recorder = Notifications(fixture);
        var earlier = DaemonSessionDto.FromSession(fixture.Session);
        fixture.Client.Offline = true;
        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnDisconnectedAsync",
            fixture.Session, fixture.Session.Client);
        Assert.True(fixture.Session.Recovering);

        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "NotifyHubAsync",
            fixture.Session, SignalREvents.DaemonSessionUpdated, earlier);

        var holders = recorder.Invocations.Last(call => call.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
            && Equals(call.Args[0], SignalREvents.DaemonSessionUpdated));
        Assert.True(Assert.IsType<DaemonSessionDto>(holders.Args[1]).Recovering);
        var page = recorder.Invocations.Last(call => call.Method == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync)
            && Equals(call.Args[1], SignalREvents.DaemonSessionUpdated));
        Assert.True(Assert.IsType<DaemonSessionDto>(page.Args[2]).Recovering);
    }

    // The session is marked recovering and cleared again while a session update is still being sent, and a page
    // that opened in between read it recovering. The next status read sends the session again, so that page
    // does not keep its Start disabled.
    [Fact]
    public async Task RecoveringSetAndClearedWhileAnUpdateIsSentGoesOutWithTheNextReadAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var held = HoldNextAdminSend(fixture);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.Hold = release.Task;
        var update = DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "NotifyHubAsync",
            fixture.Session, SignalREvents.DaemonSessionUpdated, DaemonSessionDto.FromSession(fixture.Session));
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fixture.Session.Recovering = true;
        Assert.True(DaemonSessionDto.FromSession(fixture.Session).Recovering);
        fixture.Session.Recovering = false;
        release.SetResult();
        await update.WaitAsync(TimeSpan.FromSeconds(10));
        held.Invocations.Clear();

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        var sent = Assert.Single(held.Invocations, call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.False(Assert.IsType<DaemonSessionDto>(sent.Args[1]).Recovering);
    }

    // The container restarts in place while a status read's pass still holds the recovery lease. The read of the
    // new daemon that found the lease taken is not lost: the session ends on the new daemon's instance, so a
    // side-by-side prefill is not sent with the old one.
    [Fact]
    public async Task ReadOfARestartedDaemonThatFoundThePassBusyIsAppliedAfterItAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var held = HoldNextAdminSend(fixture);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        held.Hold = release.Task;
        fixture.Session.ContainerName = "renamed-container";
        var oldInstance = fixture.Client.InstanceId;
        var pass = fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        await held.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(oldInstance, fixture.Session.Capabilities!.DaemonInstanceId);
        fixture.Client.InstanceId = Guid.NewGuid().ToString();
        await fixture.Daemon.RefreshRunsAsync(fixture.Session.Id);
        release.SetResult();
        await pass.WaitAsync(TimeSpan.FromSeconds(10));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (fixture.Session.Capabilities!.DaemonInstanceId != fixture.Client.InstanceId && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        Assert.Equal(fixture.Client.InstanceId, fixture.Session.Capabilities!.DaemonInstanceId);
    }

    // A side-by-side container with no prefills drops its socket while another task holds the recovery lease, as
    // an image update does. The read after the reconnect finds the lease taken, and the next sweep still ends the
    // recovery and sends it, so the page offers Start again.
    [Fact]
    public async Task SweepEndsTheRecoveryOfASessionWithNoRunsAfterItsReadFoundTheLeaseTakenAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        var recorder = Notifications(fixture);
        await fixture.Session.RecoveryWork.WaitAsync();
        try
        {
            await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnDisconnectedAsync",
                fixture.Session, fixture.Session.Client);
            Assert.True(fixture.Session.Recovering);
        }
        finally { fixture.Session.RecoveryWork.Release(); }
        recorder.Invocations.Clear();

        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.False(fixture.Session.Recovering);
        var last = recorder.Invocations.Last(call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.False(Assert.IsType<DaemonSessionDto>(last.Args[1]).Recovering);
    }

    // A prefill ends in a recovery pass while its session update, or the account holders' notice of its ending,
    // waits past its 5-second bound. The ending stays saved and the session is not left recovering, so the next
    // Start is accepted.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunEndingWhoseNoticeOutlastsItsBoundLeavesTheSessionReadyAsync(bool sessionUpdateWaits)
    {
        var clock = new CacheStatusClock(DateTimeOffset.UtcNow);
        await using var fixture = await RunFixture.CreateAsync(cacheStatusClock: clock);
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", 100, "success");
        var held = HoldNextAdminSend(fixture);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (sessionUpdateWaits) await fixture.Session.SessionUpdateSend.WaitAsync();
        else held.Hold = release.Task;
        var refresh = fixture.Daemon.RefreshRunsAsync(fixture.Session.Id);
        try
        {
            // The bound's timer starts only when the ending reaches the waiting send, so the clock moves after it.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (clock.ActiveTimerCount == 0 && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.Equal(1, clock.ActiveTimerCount);
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.Equal("completed", (await run.Completion.Task.WaitAsync(TimeSpan.FromSeconds(1))).Snapshot.State);
        }
        finally
        {
            if (sessionUpdateWaits) fixture.Session.SessionUpdateSend.Release();
            release.TrySetResult();
        }
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(fixture.Session.Recovering);
        var next = await fixture.StartAsync("20");
        Assert.NotEqual(run.PrefillRunId, next.PrefillRunId);
    }

    // A pass that found the recovery lease taken left its request, and the next read gets no answer. The request
    // stays and the session stays recovering, so a Start is refused instead of being sent with the old daemon's
    // instance, and the next sweep applies the restarted daemon's instance.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestWhoseReadGetsNoAnswerStaysForTheNextSweepAsync(bool readThrows)
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);
        Assert.False(fixture.Session.Recovering);
        fixture.Session.RecoveryRequested = true;
        if (readThrows) fixture.Client.StatusRead = () => Task.FromException<DaemonStatus?>(new IOException("daemon closed the socket"));
        else fixture.Client.Offline = true;
        var recorder = Notifications(fixture);
        recorder.Invocations.Clear();

        await fixture.Daemon.RefreshRunsAsync(fixture.Session.Id);

        Assert.True(fixture.Session.RecoveryRequested);
        Assert.True(fixture.Session.Recovering);
        // The page enables Start from the last update it received, so the flag must reach it.
        var update = Assert.Single(recorder.Invocations,
            call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.True(Assert.IsType<DaemonSessionDto>(update.Args[1]).Recovering);
        await Assert.ThrowsAsync<DaemonCommandException>(() => fixture.StartAsync("10"));

        fixture.Client.StatusRead = null;
        fixture.Client.Offline = false;
        fixture.Client.InstanceId = Guid.NewGuid().ToString();
        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(fixture.Client.InstanceId, fixture.Session.Capabilities!.DaemonInstanceId);
        Assert.False(fixture.Session.Recovering);
        Assert.False(fixture.Session.RecoveryRequested);
    }

    // A read of the daemon taken before a restart in place reaches the recovery lease after the pass that read the
    // restarted daemon. It leaves the newer instance applied, so the run started on that instance keeps going and
    // the next Start is not refused for the old one. Both branches of the pass write the capabilities.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadTakenBeforeARestartIsNotAppliedOverANewerReadAsync(bool olderDaemon)
    {
        await using var fixture = await RunFixture.CreateAsync();
        var session = fixture.Session;
        var oldInstance = fixture.Client.InstanceId;
        var stale = RunClient.Capabilities(oldInstance);
        stale.ReadStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (olderDaemon) stale.Features = [];
        fixture.Client.InstanceId = Guid.NewGuid().ToString();
        await fixture.RefreshAsync();
        var run = await fixture.StartAsync("10");
        Assert.Equal(fixture.Client.InstanceId, run.DaemonInstanceId);

        await fixture.Daemon.RecoverRunsAsync(session, stale, CancellationToken.None);

        Assert.Equal(fixture.Client.InstanceId, session.Capabilities!.DaemonInstanceId);
        Assert.Equal(0, run.TerminalCompletedFlag);
        Assert.False(session.Recovering);
        fixture.Client.InstanceId = Guid.NewGuid().ToString();
        await fixture.RefreshAsync();
        Assert.Equal(fixture.Client.InstanceId, session.Capabilities!.DaemonInstanceId);
    }

    // The first Start on a session whose capabilities are not known yet reads the daemon while another pass holds
    // the recovery lease and is about to apply its own read. The Start waits for that pass and then starts its run.
    [Fact]
    public async Task FirstStartWaitsForThePassHoldingTheRecoveryLeaseAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var session = fixture.Session;
        session.Capabilities = null;
        await session.RecoveryWork.WaitAsync();
        session.Recovering = true;
        Task<PrefillResult> start;
        try
        {
            start = fixture.Daemon.PrefillAsync(session.Id, appIds: ["10"], notificationMode: "silent");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!session.RecoveryRequested && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            await Task.WhenAny(start, Task.Delay(500));

            Assert.False(start.IsCompleted);
            session.Capabilities = RunClient.Capabilities(fixture.Client.InstanceId);
            session.Recovering = false;
        }
        finally { session.RecoveryWork.Release(); }
        var result = await start.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(result.RunId);
    }

    // A status read that gets no answer for a session with a run turns on recovering, and the page that enables
    // Start from the last update it received is told.
    [Fact]
    public async Task UnansweredStatusReadSendsTheRecoveringFlagToThePagesAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        await fixture.StartAsync("10");
        fixture.Client.Offline = true;
        var recorder = Notifications(fixture);
        recorder.Invocations.Clear();

        await fixture.Daemon.GetSessionStatusAsync(fixture.Session.Id);

        Assert.True(fixture.Session.Recovering);
        var update = Assert.Single(recorder.Invocations,
            call => Equals(call.Args.FirstOrDefault(), SignalREvents.DaemonSessionUpdated));
        Assert.True(Assert.IsType<DaemonSessionDto>(update.Args[1]).Recovering);
    }

    // A pass that finds the recovery lease taken writes its request while the holder releases the lease and checks
    // for one. In every order the pass's newer read is applied, or the holder sees the request and reads again.
    [Fact]
    public async Task SkippedReadIsAppliedOrSeenByTheHolderInEveryOrderAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var session = fixture.Session;
        // A rerun reads nothing, so only the skipping pass itself can apply the newer read.
        fixture.Client.Offline = true;
        var older = session.Capabilities!;
        // An older daemon's read is applied without database work, so a round takes microseconds.
        var newer = RunClient.Capabilities(Guid.NewGuid().ToString());
        newer.Features = [];
        const int rounds = 200_000;
        var lost = 0;
        var sink = 0;
        using var start = new Barrier(2);
        using var end = new Barrier(2);
        var holder = new Thread(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                session.Capabilities = older;
                session.RecoveryRequested = false;
                session.RecoveryWork.Wait();
                start.SignalAndWait();
                for (var spin = 0; spin < round % 400; spin++)
                    Volatile.Write(ref sink, spin);
                session.RecoveryWork.Release();
                var sawRequest = session.RecoveryRequested;
                end.SignalAndWait();
                if (!ReferenceEquals(session.Capabilities, newer) && !sawRequest)
                    lost++;
            }
        });
        var skipper = new Thread(() =>
        {
            for (var round = 0; round < rounds; round++)
            {
                start.SignalAndWait();
                fixture.Daemon.RecoverRunsAsync(session, newer, CancellationToken.None).GetAwaiter().GetResult();
                end.SignalAndWait();
            }
        });
        holder.Start();
        skipper.Start();
        await Task.Run(() => { holder.Join(); skipper.Join(); });

        Assert.Equal(0, lost);
    }

    // The host stops while the minute sweep waits on a daemon that cannot be reached. The wait ends with the host,
    // so the prefill services that stop guest containers on shutdown are not held behind it.
    [Fact]
    public async Task HostStopEndsTheSweepsWaitOnAnUnreachableDaemonAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeReconnectDaemonClient
        {
            StatusHandler = async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return null;
            }
        };
        fixture.Session.Client = client;
        fixture.Session.Recovering = true;
        await using var services = new ServiceCollection().AddSingleton(fixture.Daemon).BuildServiceProvider();
        var sweep = new PersistentSessionExpiryService(NullLogger<PersistentSessionExpiryService>.Instance,
            new ConfigurationBuilder().Build(), services.GetRequiredService<IServiceScopeFactory>());
        using var stop = new CancellationTokenSource();
        var work = (Task)typeof(PersistentSessionExpiryService)
            .GetMethod("ExecuteWorkAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(sweep, [stop.Token])!;
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await stop.CancelAsync();
        var ended = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(2)));
        release.TrySetResult();

        Assert.Same(work, ended);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
    }

    // Two sessions with no prefills lost their daemons. One sweep reads both at once, so one unreachable daemon does
    // not hold the other's read, and a sweep that follows within seconds does not read them again.
    [Fact]
    public async Task SweepReadsUnreachableSessionsTogetherAndSpacesTheirNextReadAsync()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var answer = new TaskCompletionSource<DaemonStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = new int[1];
        Task<DaemonStatus?> ReadAsync()
        {
            if (Interlocked.Increment(ref reads[0]) == 2) bothStarted.TrySetResult();
            return answer.Task;
        }
        var otherClient = DispatchProxy.Create<IDaemonClient, RunClient>();
        ((RunClient)(object)otherClient).StatusRead = ReadAsync;
        var other = new DaemonSession
        {
            Id = Guid.NewGuid().ToString("N")[..16],
            UserId = Guid.NewGuid(),
            Status = DaemonSessionStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1),
            Client = otherClient
        };
        ((TestableSteamDaemonService)fixture.Daemon).InjectSession(other);
        fixture.Client.StatusRead = ReadAsync;
        fixture.Session.Recovering = true;
        other.Recovering = true;

        var sweep = fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow.AddMinutes(1));
        var started = await Task.WhenAny(bothStarted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        answer.SetResult(null);
        await sweep.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Same(bothStarted.Task, started);
        Assert.True(fixture.Session.NextRecoveryAtUtc > DateTime.UtcNow);
        Assert.True(other.NextRecoveryAtUtc > DateTime.UtcNow);
        await fixture.Daemon.ProcessSessionExpiryAsync(DateTime.UtcNow);
        Assert.Equal(2, reads[0]);
    }

    private static HeldAdminSendProxy HoldNextAdminSend(RunFixture fixture)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, HeldAdminSendProxy>();
        typeof(PrefillDaemonServiceBase).GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(fixture.Daemon, notifications);
        return (HeldAdminSendProxy)(object)notifications;
    }

    private static RecordingNotificationProxy Notifications(RunFixture fixture) =>
        (RecordingNotificationProxy)typeof(PrefillDaemonServiceBase)
            .GetField("_notifications", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Daemon)!;

    private static (DaemonRunSnapshot Snapshot, DaemonRunItem Item) Progress(DaemonRun run, long sequence, long bytes)
    {
        var snapshot = run.Snapshot with
        {
            Sequence = sequence,
            BytesTransferred = bytes,
            State = "downloading",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return (snapshot, new DaemonRunItem { AppId = "10", Sequence = sequence, BytesTransferred = bytes, State = "downloading" });
    }

    // Records every send and holds the next admin send until the test releases it.
    private class HeldAdminSendProxy : RecordingNotificationProxy
    {
        public Task? Hold { get; set; }

        /// <summary>Completes when a send has reached the hold.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var result = base.Invoke(targetMethod, args);
            if (targetMethod?.Name != nameof(ISignalRNotificationService.NotifyAdminAsync) || Hold is not { } hold)
                return result;
            Hold = null;
            Entered.TrySetResult();
            return hold;
        }
    }

    private sealed class BeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
    {
        public bool Ran { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData saving,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran)
            {
                Ran = true;
                await action();
            }
            return result;
        }
    }
}
