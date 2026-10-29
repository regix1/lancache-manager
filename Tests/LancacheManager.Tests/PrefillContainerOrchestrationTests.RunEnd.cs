using System.Reflection;
using System.Text.Json;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Core.Services.SteamPrefill;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Data;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// How a run's end, a cancel and a recovery pass reach the session state and the pages that show the run.
/// </summary>
public sealed partial class PrefillContainerOrchestrationTests
{
    /// <summary>
    /// A cancel whose request is aborted while the daemon has not answered is the caller leaving, not a lost
    /// connection: the session stays ready for the next Start, and the next recovery pass sends the cancel again.
    /// </summary>
    [Fact]
    public async Task ACancelWhoseCallerLeavesKeepsTheSessionReady()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = (CallerAbortRunClient)(object)DispatchProxy.Create<IDaemonClient, CallerAbortRunClient>();
        client.InstanceId = fixture.Client.InstanceId;
        fixture.Session.Client = (IDaemonClient)(object)client;
        var run = await fixture.StartAsync("10");
        using var caller = new CancellationTokenSource();
        client.AbortCancelFor = caller;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Daemon.CancelPrefillRunAsync(fixture.Session.Id, run.PrefillRunId, caller.Token));

        Assert.False(fixture.Session.Recovering);
        Assert.False(run.Recovering);
        var second = await fixture.StartAsync("20");
        Assert.Equal(0, second.TerminalCompletedFlag);
        await fixture.RefreshAsync();
        Assert.Equal([run.PrefillRunId], client.Cancelled);
        await fixture.RefreshAsync();
        Assert.Equal("cancelled", run.Snapshot.State);
    }

    /// <summary>
    /// A fourth tab on a session does not cut the first tab off: every connected tab still gets the run's end.
    /// </summary>
    [Fact]
    public async Task AFourthTabKeepsTheFirstTabGettingRunEvents()
    {
        await using var fixture = await RunFixture.CreateAsync();
        foreach (var tab in new[] { "tab-1", "tab-2", "tab-3", "tab-4" })
            fixture.Daemon.AddSubscriber(fixture.Session.Id, tab);
        var run = await fixture.StartAsync("10");

        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();

        Assert.Equal(2, run.TerminalCompletedFlag);
        Assert.Contains(fixture.Recorder.Invocations, invocation =>
            invocation.Method == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync)
            && (string?)invocation.Args[0] == "tab-1"
            && (string?)invocation.Args[1] == SignalREvents.PrefillStateChanged);
    }

    /// <summary>
    /// A Stop whose container removal fails still ends the session's runs, and the owner's open page learns it.
    /// </summary>
    [Fact]
    public async Task AStopWhoseRemovalFailsSendsTheRunEndToTheOpenPage()
    {
        var (daemon, session, run, gateway, _, recorder) = await StoppableSessionWithARunAsync();
        using var owned = daemon;
        daemon.AddSubscriber(session.Id, "page");
        gateway.FailNextRemoveContainer(new IOException("simulated remove failure"));

        await Assert.ThrowsAsync<ServiceUnavailableException>(() => daemon.StopPersistentSessionAsync(session.Id, "admin"));

        Assert.Equal("cancelled", run.Snapshot.State);
        Assert.Contains(recorder.Invocations, invocation =>
            invocation.Method == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync)
            && (string?)invocation.Args[0] == "page"
            && (string?)invocation.Args[1] == SignalREvents.PrefillProgress
            && (invocation.Args[2]?.GetType().GetProperty("progress")?.GetValue(invocation.Args[2]) as PrefillProgress)?.State
                == "cancelled");
    }

    /// <summary>
    /// A ban stores its own reason on the run it cancels and on that run's history row.
    /// </summary>
    [Fact]
    public async Task ABanStoresItsOwnReasonOnTheRunItEnds()
    {
        var (daemon, session, run, _, database, _) = await StoppableSessionWithARunAsync();
        using var owned = daemon;

        await daemon.TerminateSessionAsync(session.Id, "Banned by admin", true,
            reasonKey: PrefillDaemonServiceBase.BannedReasonKey);

        Assert.Equal(PrefillDaemonServiceBase.BannedReasonKey, run.Snapshot.Reason);
        await using var context = await database.CreateDbContextAsync();
        var row = await context.PrefillHistoryEntries.SingleAsync(entry => entry.RunId == run.PrefillRunId);
        Assert.Equal(PrefillDaemonServiceBase.BannedReasonKey, row.Reason);
    }

    /// <summary>
    /// The session update a run's end sends no longer counts that run, so the admin row stops showing it as
    /// prefilling when the daemon reports the end.
    /// </summary>
    [Fact]
    public async Task TheLastSessionUpdateAfterARunEndsCountsNoRun()
    {
        await using var fixture = await RunFixture.CreateAsync();
        fixture.Daemon.AddSubscriber(fixture.Session.Id, "page");
        var run = await fixture.StartAsync("10");
        fixture.Client.Set(run, "completed", 100, "success");

        await DaemonTestMethods.InvokePrivateHandlerAsync(fixture.Daemon, "OnProgressChangeAsync", fixture.Session,
            new SocketPrefillProgress
            {
                OperationId = run.PrefillRunId.ToString(),
                DaemonInstanceId = run.DaemonInstanceId,
                Sequence = run.Snapshot.Sequence + 1
            });

        Assert.Equal(2, run.TerminalCompletedFlag);
        var adminUpdate = fixture.Recorder.Invocations.Last(invocation =>
            invocation.Method == nameof(ISignalRNotificationService.NotifyAdminAsync)
            && (string?)invocation.Args[0] == SignalREvents.DaemonSessionUpdated);
        Assert.Equal(0, Assert.IsType<DaemonSessionDto>(adminUpdate.Args[1]).ActiveRunCount);
        var pageUpdate = fixture.Recorder.Invocations.Last(invocation =>
            invocation.Method == nameof(ISignalRNotificationService.SendToPrefillClientRawAsync)
            && (string?)invocation.Args[0] == "page"
            && (string?)invocation.Args[1] == SignalREvents.DaemonSessionUpdated);
        Assert.Equal(0, Assert.IsType<DaemonSessionDto>(pageUpdate.Args[2]).ActiveRunCount);
    }

    /// <summary>
    /// A run whose operation page the daemon answers with a bad identity does not stop the recovery pass: the
    /// session's other runs are reconciled and the session is no longer recovering.
    /// </summary>
    [Fact]
    public async Task ARunWhosePageFailsLeavesTheOtherRunsReconciled()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var failing = await fixture.StartAsync("10");
        var finishing = await fixture.StartAsync("20");
        fixture.Client.Set(finishing, "completed", 100, "success");
        fixture.Client.ReadPage = (runId, _) => runId == failing.PrefillRunId
            ? throw new JsonException("Operation page has a different run identity.")
            : fixture.Client.Pages[runId];

        await fixture.RefreshAsync();

        Assert.Equal(2, finishing.TerminalCompletedFlag);
        Assert.Equal(0, failing.TerminalCompletedFlag);
        Assert.False(fixture.Session.Recovering);
    }

    /// <summary>
    /// A run whose operation page the daemon does not answer in time leaves the session recovering, so no Start
    /// is sent to a daemon that has stopped answering.
    /// </summary>
    [Fact]
    public async Task ARunWhosePageTimesOutKeepsTheSessionRecovering()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var waiting = await fixture.StartAsync("10");
        fixture.Client.ReadPage = (runId, _) => runId == waiting.PrefillRunId
            ? throw new TaskCanceledException("The daemon did not answer in time.")
            : fixture.Client.Pages[runId];

        await fixture.RefreshAsync();

        Assert.True(fixture.Session.Recovering);
    }

    /// <summary>
    /// A stall whose session starts recovering while the stall is being recorded sends no cancel, so the sweep
    /// does not count it as a failed stall; the recovery pass sends the recorded cancel.
    /// </summary>
    [Fact]
    public async Task AStallWhoseSessionStartsRecoveringCountsNoFailure()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_stall_{Guid.NewGuid():N}").Options;
        var database = new BlockingDbContexts(options);
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        using var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true)),
            new RecordingContainerGateway());
        var client = (RunClient)(object)DispatchProxy.Create<IDaemonClient, RunClient>();
        var session = InjectedPersistentSession(daemon, (IDaemonClient)(object)client);
        session.AuthState = DaemonAuthState.Authenticated;
        session.Capabilities = RunClient.Capabilities(client.InstanceId);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var run = new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = session.Id,
            DaemonInstanceId = client.InstanceId,
            Options = new DaemonRunOptions { AppIds = ["10"] },
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = client.InstanceId,
                StartedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-10),
                State = "downloading"
            }
        };
        run.LastProgressTicksUtc = DateTime.UtcNow.AddMinutes(-10).Ticks;
        session.Runs[runId] = run;

        var stall = daemon.FailStalledSessionAsync(session, runId, DateTime.UtcNow, TimeSpan.FromMinutes(1));
        await database.FirstCreateStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Recovering = true;
        database.ReleaseFirstCreate.TrySetResult(true);

        Assert.False(await stall.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Empty(client.Cancelled);
        Assert.True(run.CancelRequested);
        Assert.Equal("stalled", run.CancelReason);
    }

    /// <summary>
    /// A Start the daemon refuses because it is not signed in stores the sign-in reason on the run, so the run's
    /// row says why it ended.
    /// </summary>
    [Fact]
    public async Task AStartRefusedForSignInStoresTheSignInReason()
    {
        await using var fixture = await RunFixture.CreateAsync();
        var client = (SignedOutRunClient)(object)DispatchProxy.Create<IDaemonClient, SignedOutRunClient>();
        client.InstanceId = fixture.Client.InstanceId;
        fixture.Session.Client = (IDaemonClient)(object)client;

        await Assert.ThrowsAsync<DaemonCommandException>(
            () => fixture.Daemon.PrefillAsync(fixture.Session.Id, appIds: ["10"]));

        var run = Assert.Single(fixture.Session.Runs.Values);
        Assert.Equal("failed", run.Snapshot.State);
        Assert.Equal("auth-lost", run.Snapshot.Reason);
    }

    private async Task<(TestSteamDaemon Daemon, DaemonSession Session, DaemonRun Run, RecordingContainerGateway Gateway,
        PooledDbFactory Database, RecordingNotificationProxy Recorder)> StoppableSessionWithARunAsync()
    {
        // Ending a run saves it inside a transaction, which the in-memory store only warns about.
        var database = new PooledDbFactory(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"prefill_orchestration_{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var sessionService = new PrefillSessionService(database, NullLogger<PrefillSessionService>.Instance);
        var gateway = new RecordingContainerGateway();
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotificationProxy>();
        var daemon = new TestSteamDaemon(
            MakeDeps(database, sessionService, Config(PersistenceMode.FullPersistence, steamEnabled: true),
                notifications: notifications),
            gateway);
        var session = InjectedPersistentSession(daemon, new FakeReconnectDaemonClient());
        session.ContainerId = "container-a";
        gateway.AddContainer(new FakeContainer { Id = session.ContainerId, Name = SteamPersistentContainerName, Running = true });
        await sessionService.CreateSessionAsync(
            session.Id, session.UserId, session.ContainerId, SteamPersistentContainerName, session.ExpiresAt, DateTime.UtcNow);
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var run = new DaemonRun
        {
            PrefillRunId = runId,
            SessionId = session.Id,
            DaemonInstanceId = "instance",
            Options = new DaemonRunOptions(),
            Snapshot = new DaemonRunSnapshot
            {
                OperationId = runId.ToString(),
                DaemonInstanceId = "instance",
                StartedAt = now,
                UpdatedAt = now,
                State = "running"
            }
        };
        await sessionService.CreateRunAsync(run, "manual", CancellationToken.None);
        session.Runs[runId] = run;
        await sessionService.SaveRunAsync(run, run.Snapshot with { Sequence = 1, UpdatedAt = now },
            [new DaemonRunItem { AppId = "10", Sequence = 1, State = "downloading" }], false, CancellationToken.None);
        return (daemon, session, run, gateway, database, (RecordingNotificationProxy)(object)notifications);
    }
}

/// <summary>
/// A daemon client whose next cancel is aborted by its caller before the daemon answers, as when the browser
/// request that sent the cancel is closed.
/// </summary>
internal class CallerAbortRunClient : RunClient
{
    public CancellationTokenSource? AbortCancelFor { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IDaemonClient.CancelPrefillAsync) && AbortCancelFor is { } caller)
        {
            AbortCancelFor = null;
            caller.Cancel();
            return Task.FromCanceled<DaemonRunSnapshot>(caller.Token);
        }
        return base.Invoke(targetMethod, args);
    }
}

/// <summary>
/// A daemon client that refuses every Start the way a daemon answers a command before its login: no error code,
/// only the sign-in flag.
/// </summary>
internal class SignedOutRunClient : RunClient
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == nameof(IDaemonClient.PrefillAsync)
            ? Task.FromResult(new PrefillResult { Success = false, RequiresLogin = true })
            : base.Invoke(targetMethod, args);
}
