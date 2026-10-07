using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves that a Run Now pending at loop start is honored immediately rather than
/// being deferred by a full interval. The first loop pass normally skips execution and sleeps one
/// interval; a manual run pending at that point must pre-empt the skip and run work now.
///
/// Also proves that a Run Now arriving <em>while ExecuteWorkAsync is running</em> is honored promptly:
/// at that moment the trigger cancels the delay source of the already-finished prior sleep, so the loop
/// must re-check the pending flag after work completes instead of sleeping a full interval (or, for a
/// paused service, forever) and mislabelling the deferred run as Manual.
/// </summary>
public class ScheduledBackgroundServiceManualRunTests
{
    [Fact]
    public async Task ConsumedNoticesStaySeparateFromLaterPendingRequests()
    {
        using var service = new GatedManualRunProbeService(TimeSpan.FromHours(1));
        var first = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        var deferred = new RunNotice(NotificationMode.Manual, RunTrigger.Startup);
        await service.TryTriggerImmediateRunAsync(first);
        // An owed run arrives the production way: held for a download, then released with the loop idle.
        await service.HoldRunAsync(deferred, _ => { });
        service.ReleaseHeldRun(_ => null);
        var consumed = await service.TakePendingManualRunAsync();
        Assert.NotNull(consumed);
        var coalesced = await service.TakePendingDeferredRunAsync();
        Assert.NotNull(coalesced);
        Assert.Same(deferred, coalesced);
        var second = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var later = new RunNotice(NotificationMode.Silent, RunTrigger.Startup);
        await service.TryTriggerImmediateRunAsync(second);
        // The taken run would do a released hold's work itself; canceled, it leaves the hold owed.
        first.Cancel(null, Guid.Empty);
        await service.HoldRunAsync(later, _ => { });
        service.ReleaseHeldRun(_ => null);
        service.SelectNotice(consumed);
        Assert.Same(first, service.CurrentRunNotice);
        var pending = await service.TakePendingManualRunAsync();
        Assert.NotNull(pending);
        Assert.Same(second, pending);
        var pendingDeferred = await service.TakePendingDeferredRunAsync();
        Assert.NotNull(pendingDeferred);
        Assert.Same(later, pendingDeferred);
    }

    [Fact]
    public async Task PendingRunAllPromotedToManualTakesTheManualActor()
    {
        using var service = new GatedManualRunProbeService(TimeSpan.FromHours(1));
        var firstActor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "run-all-user");
        var manualActor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "run-now-user");
        var pending = new RunNotice(NotificationMode.All, RunTrigger.RunAll, firstActor);
        await service.TryTriggerImmediateRunAsync(pending);

        var retained = (await service.TryTriggerImmediateRunAsync(
            new RunNotice(NotificationMode.Silent, RunTrigger.Manual, manualActor))).Retained;

        Assert.Same(pending, retained);
        Assert.Equal(RunTrigger.Manual, retained.Trigger);
        Assert.Same(manualActor, retained.Actor);
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TriggerImmediateRun_DuringFirstSkipWindow_RunsWorkPromptlyAsync()
    {
        using var service = new ManualRunProbeService();

        // Pending at loop start, before the loop ever creates its interruptible-delay source - the case
        // the old skip-first branch missed, deferring the run by a full (here, one hour) interval.
        await service.TryTriggerImmediateRunAsync(new RunNotice(service.EffectiveNotificationMode, RunTrigger.Manual));

        await service.StartAsync(CancellationToken.None);

        try
        {
            await service.FirstExecution.WaitAsync(Timeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(service.FirstExecution.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TriggerImmediateRun_DuringInProgressRun_RunsFollowUpPromptlyAsManualAsync()
    {
        // Long interval: a deferred follow-up run would not complete within the test timeout, so a
        // prompt second run proves the loop did not sleep a full interval before honoring the trigger.
        using var service = new GatedManualRunProbeService(TimeSpan.FromHours(1));

        await StartAndTriggerDuringRunAsync(service);

        Assert.True(service.SecondRunCompleted.IsCompletedSuccessfully);
        Assert.Equal(RunTrigger.Manual, service.SecondRunTrigger);
    }

    [Fact]
    public async Task TriggerImmediateRun_DuringInProgressRun_WhilePaused_RunsFollowUpPromptlyAsync()
    {
        // Interval zero = paused/disabled: after a run the loop sleeps indefinitely, so a trigger that
        // arrives during the run would be dropped forever unless the loop re-checks the pending flag.
        using var service = new GatedManualRunProbeService(TimeSpan.Zero);

        await StartAndTriggerDuringRunAsync(service);

        Assert.True(service.SecondRunCompleted.IsCompletedSuccessfully);
        Assert.Equal(RunTrigger.Manual, service.SecondRunTrigger);
    }

    // Disabled (0) and Startup only (-1) do no work after the app starts unless someone asks.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ADisabledOrStartupOnlyInterval_DoesNoWorkAfterStart(int hours)
    {
        using var service = new ManualRunProbeService();
        service.SetInterval(TimeSpan.FromHours(hours));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500);
            Assert.Equal(0, service.Runs);

            // The loop is still there to take a Run Now.
            await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
            await service.FirstExecution.WaitAsync(Timeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A default interval of zero from configuration is Disabled too: only a service that runs one long
    // pass at zero (the speed tracker) works at start.
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ADefaultIntervalOfZero_RunsOnlyAContinuousService(bool continuousPass, int expectedRuns)
    {
        using var service = new ManualRunProbeService { DefaultInterval = TimeSpan.Zero, ContinuousPass = continuousPass };
        await service.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(500);
            Assert.Equal(expectedRuns, service.Runs);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now that fails on a Disabled schedule shows its failure once and is not run again.
    [Fact]
    public async Task AFailedRunNowOnADisabledSchedule_IsNotRetried()
    {
        using var service = new ManualRunProbeService { FailFirstRun = true, RetryDelay = TimeSpan.FromMilliseconds(50) };
        service.SetInterval(TimeSpan.Zero);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
            await service.FirstExecution.WaitAsync(Timeout);
            await Task.Delay(500);
            Assert.Equal(1, service.Runs);
            Assert.Null(service.NextRunUtc);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now accepted while a run works is for the run that follows it. When that run fails, the back-off
    // waits out its delay instead of retrying at once as a scheduled run.
    [Fact]
    public async Task AFailedRunAfterAQueuedRunNow_WaitsOutTheBackOff()
    {
        using var service = new FailSecondRunProbeService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
            await service.FirstRunStarted.WaitAsync(Timeout);
            var followUp = await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
            Assert.True(followUp.FollowUpQueued);
            service.ReleaseFirstRun();
            await service.SecondRunFailed.WaitAsync(Timeout);
            await Task.Delay(500);
            Assert.Equal(2, service.Runs);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now dismissed before the loop takes it wakes the loop but runs nothing; the next scheduled
    // run stays at the time it was already due.
    [Fact]
    public async Task ADismissedRunNow_KeepsTheNextRunWhereItWas()
    {
        using var service = new ManualRunProbeService();
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (service.NextRunUtc is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "The loop never set its next run");
                await Task.Delay(10);
            }

            var due = service.NextRunUtc;
            await Task.Delay(300);
            var dismissed = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
            dismissed.Cancel(null, Guid.Empty);
            Assert.True((await service.TryTriggerImmediateRunAsync(dismissed)).Admitted);
            await Task.Delay(300);

            Assert.Equal(0, service.Runs);
            Assert.Equal(due, service.NextRunUtc);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now during the one-minute back-off after a failed run starts at once.
    [Fact]
    public async Task ARunNowDuringTheErrorBackOff_RunsPromptly()
    {
        using var service = new ManualRunProbeService { FailFirstRun = true };
        await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual));
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.FirstExecution.WaitAsync(Timeout);
            var deadline = DateTime.UtcNow + Timeout;
            while (!(await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.Silent, RunTrigger.Manual))).Admitted)
            {
                Assert.True(DateTime.UtcNow < deadline, "The Run Now was never admitted");
                await Task.Delay(10);
            }

            await service.SecondRun.WaitAsync(Timeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task StartAndTriggerDuringRunAsync(GatedManualRunProbeService service)
    {
        // First trigger starts run #1 (the gated probe blocks inside it until released).
        var first = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        await service.TryTriggerImmediateRunAsync(first);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await service.FirstRunStarted.WaitAsync(Timeout);

            // Second trigger lands while run #1 is still executing - the regression scenario.
            Assert.Same(first, service.CurrentRunNotice);
            var second = new RunNotice(NotificationMode.Manual, RunTrigger.Manual);
            Assert.Same(second, (await service.TryTriggerImmediateRunAsync(second)).Retained);
            Assert.Same(second, (await service.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual))).Retained);
            Assert.Same(first, service.CurrentRunNotice);
            service.ReleaseFirstRun();

            await service.SecondRunCompleted.WaitAsync(Timeout);
            Assert.Same(second, service.CurrentRunNotice);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    // The first run blocks until released; the second throws, which starts the failure back-off.
    private sealed class FailSecondRunProbeService : ScheduledBackgroundService
    {
        private readonly TaskCompletionSource _firstRunStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRunFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public FailSecondRunProbeService()
            : base(NullLogger<FailSecondRunProbeService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public int Runs => Volatile.Read(ref _runs);
        public Task FirstRunStarted => _firstRunStarted.Task;
        public Task SecondRunFailed => _secondRunFailed.Task;
        public void ReleaseFirstRun() => _releaseFirstRun.TrySetResult();

        protected override string ServiceName => "FailSecondRunProbe";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan ErrorRetryDelay => TimeSpan.FromSeconds(30);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            var run = Interlocked.Increment(ref _runs);
            if (run == 1)
            {
                _firstRunStarted.TrySetResult();
                await _releaseFirstRun.Task.WaitAsync(stoppingToken);
            }
            else if (run == 2)
            {
                _secondRunFailed.TrySetResult();
                throw new InvalidOperationException("The second run fails");
            }
        }
    }

    // Counts its work runs; the first can be made to fail, which starts the default one-minute back-off.
    private sealed class ManualRunProbeService : ScheduledBackgroundService
    {
        private readonly TaskCompletionSource _firstExecution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runs;

        public ManualRunProbeService()
            : base(NullLogger<ManualRunProbeService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public bool FailFirstRun { get; init; }
        public TimeSpan? RetryDelay { get; init; }
        // The service's own default interval, as a configured default or an override of it would set it.
        public TimeSpan DefaultInterval { get; init; } = TimeSpan.FromHours(1);
        public bool ContinuousPass { get; init; }
        public int Runs => Volatile.Read(ref _runs);
        public Task FirstExecution => _firstExecution.Task;
        public Task SecondRun => _secondRun.Task;

        protected override string ServiceName => "ManualRunProbe";

        // A long interval so a deferred first run would not complete within the test's timeout.
        protected override TimeSpan Interval => DefaultInterval;
        protected override bool RunsContinuously => ContinuousPass;
        protected override TimeSpan ErrorRetryDelay => RetryDelay ?? base.ErrorRetryDelay;
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            var run = Interlocked.Increment(ref _runs);
            if (run == 2) _secondRun.TrySetResult();
            if (run == 1)
            {
                _firstExecution.TrySetResult();
                if (FailFirstRun) throw new InvalidOperationException("The first run fails");
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Probe that blocks inside its first work run until released, so a test can fire a second
    /// Run Now while the first run is in progress, then observe how promptly (and under
    /// which trigger) the follow-up run executes.
    /// </summary>
    private sealed class GatedManualRunProbeService : ScheduledBackgroundService
    {
        private readonly TimeSpan _interval;
        private readonly TaskCompletionSource _firstRunStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRun =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRunCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public GatedManualRunProbeService(TimeSpan interval)
            : base(NullLogger<GatedManualRunProbeService>.Instance, new ConfigurationBuilder().Build())
        {
            _interval = interval;
        }

        public Task FirstRunStarted => _firstRunStarted.Task;
        public Task SecondRunCompleted => _secondRunCompleted.Task;
        public RunTrigger? SecondRunTrigger { get; private set; }
        public Task<RunNotice?> TakePendingManualRunAsync() => ConsumePendingManualRunAsync();
        public Task<RunNotice?> TakePendingDeferredRunAsync() => ConsumePendingDeferredRunAsync();
        public void SelectNotice(RunNotice? notice) => SelectRunNotice(RunTrigger.Manual, notice);

        public void ReleaseFirstRun() => _releaseFirstRun.TrySetResult();

        protected override string ServiceName => "GatedManualRunProbe";
        protected override TimeSpan Interval => _interval;
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            var run = Interlocked.Increment(ref _runCount);
            if (run == 1)
            {
                _firstRunStarted.TrySetResult();
                await _releaseFirstRun.Task.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            else if (run == 2)
            {
                SecondRunTrigger = CurrentRunTrigger;
                _secondRunCompleted.TrySetResult();
            }
        }
    }
}

/// <summary>
/// Same regression coverage as <see cref="ScheduledBackgroundServiceManualRunTests"/> but for the
/// runtime-configurable base class, whose loop shares the same "trigger during an in-progress run
/// races the next sleep" hazard.
/// </summary>
public class ConfigurableScheduledServiceManualRunTests
{
    [Fact]
    public async Task ManualRunAdmission_RejectsPendingStartingAndExecutingDuplicates()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.Zero, queueManualRuns: false);
        var firstActor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "first-user");
        var secondActor = new ScheduleActor(ScheduleActorKind.Account, Guid.NewGuid(), "second-user");
        var first = new RunNotice(NotificationMode.Silent, RunTrigger.Manual, firstActor);
        var acknowledgments = 0;
        var admission = await service.TryTriggerImmediateRunAsync(first, (_, _) => acknowledgments++);
        Assert.True(admission.Admitted);
        Assert.Same(first, admission.Retained);
        Assert.False(admission.FollowUpQueued);
        admission = await service.TryTriggerImmediateRunAsync(new RunNotice(
            NotificationMode.All, RunTrigger.Manual, secondActor), (_, _) => acknowledgments++);
        Assert.False(admission.Admitted);
        Assert.Same(first, admission.Retained);
        Assert.Same(firstActor, admission.Retained.Actor);
        Assert.False(admission.FollowUpQueued);
        var consumed = await service.TakePendingManualRunAsync();
        Assert.NotNull(consumed);
        await service.TryTriggerImmediateRunAsync(new RunNotice(service.EffectiveNotificationMode, RunTrigger.Manual));
        Assert.Same(first, (await service.TryTriggerImmediateRunAsync(
            new RunNotice(NotificationMode.All, RunTrigger.Manual))).Retained);
        Assert.False(await service.HasPendingRunAsync());

        var run = service.InvokeRunAsync(consumed);
        await service.FirstRunStarted.WaitAsync(Timeout);
        admission = await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual),
            (_, _) => acknowledgments++);
        Assert.False(admission.Admitted);
        Assert.Same(first, admission.Retained);
        Assert.False(admission.FollowUpQueued);
        service.ReleaseFirstRun();
        await run.WaitAsync(Timeout);
        Assert.False(await service.HasPendingRunAsync());
        Assert.Equal(1, service.RunCount);
        Assert.Equal(1, acknowledgments);
        admission = await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
        Assert.True(admission.Admitted);
        Assert.False(admission.FollowUpQueued);
    }

    [Fact]
    public async Task NaturalRun_AbsorbsPendingManualNoticeAndCancellationReleasesClaim()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.Zero, queueManualRuns: false);
        var notice = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        await service.TryTriggerImmediateRunAsync(notice);
        service.ReleaseFirstRun();
        await service.InvokeRunAsync(null);
        Assert.Same(notice, service.CurrentRunNotice);
        Assert.Equal(RunTrigger.Manual, service.CurrentRunNotice.Trigger);
        Assert.False(await service.HasPendingRunAsync());

        var cancelled = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        await service.TryTriggerImmediateRunAsync(cancelled);
        // Drops the request the way a dismissed card does, leaving the notice itself uncanceled.
        service.DismissCard(Guid.Empty, cancelled);
        Assert.False(await service.HasPendingRunAsync());
        Assert.True((await service.TryTriggerImmediateRunAsync(cancelled)).Admitted);
        var consumed = await service.TakePendingManualRunAsync();
        Assert.NotNull(consumed);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await service.InvokeRunAsync(consumed, stop.Token);
        Assert.True((await service.TryTriggerImmediateRunAsync(notice)).Admitted);
    }

    [Fact]
    public async Task GenericManualRun_ReportsFollowUpWhileConsumedNoticeHasNotStarted()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.Zero);
        await service.TryTriggerImmediateRunAsync(new RunNotice(service.EffectiveNotificationMode, RunTrigger.Manual));
        Assert.NotNull(await service.TakePendingManualRunAsync());
        var admission = await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual));
        Assert.True(admission.Admitted);
        Assert.True(admission.FollowUpQueued);
        Assert.True(await service.HasPendingRunAsync());
    }

    [Fact]
    public async Task ConsumedNoticesStaySeparateFromLaterPendingRequests()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(1));
        var first = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        var deferred = new RunNotice(NotificationMode.Manual, RunTrigger.Startup);
        await service.TryTriggerImmediateRunAsync(first);
        // An owed run arrives the production way: held for a download, then released with the loop idle.
        await service.HoldRunAsync(deferred, _ => { });
        service.ReleaseHeldRun(_ => null);
        var consumed = await service.TakePendingManualRunAsync();
        Assert.NotNull(consumed);
        var coalesced = await service.TakePendingDeferredRunAsync();
        Assert.NotNull(coalesced);
        Assert.Same(deferred, coalesced);
        var second = new RunNotice(NotificationMode.All, RunTrigger.Manual);
        var later = new RunNotice(NotificationMode.Silent, RunTrigger.Startup);
        await service.TryTriggerImmediateRunAsync(second);
        // The taken run would do a released hold's work itself; canceled, it leaves the hold owed.
        first.Cancel(null, Guid.Empty);
        await service.HoldRunAsync(later, _ => { });
        service.ReleaseHeldRun(_ => null);
        service.SelectNotice(consumed);
        Assert.Same(first, service.CurrentRunNotice);
        var pending = await service.TakePendingManualRunAsync();
        Assert.NotNull(pending);
        Assert.Same(second, pending);
        var pendingDeferred = await service.TakePendingDeferredRunAsync();
        Assert.NotNull(pendingDeferred);
        Assert.Same(later, pendingDeferred);
    }

    [Theory]
    [InlineData(3_600_000)]
    [InlineData(-1)]
    public async Task ARequestPostedBetweenThePeekAndTheSleep_StartsAtOnce(int sleepMilliseconds)
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(1));
        using var stop = new CancellationTokenSource();
        // The loop's peek finds nothing, then a Run Now lands before its sleep starts.
        Assert.False(await service.HasPendingRunAsync());
        Assert.True((await service.TryTriggerImmediateRunAsync(new RunNotice(NotificationMode.All, RunTrigger.Manual))).Admitted);

        Assert.True(await service.SleepAsync(TimeSpan.FromMilliseconds(sleepMilliseconds), stop.Token)
            .WaitAsync(TimeSpan.FromSeconds(1)));

        // The recorded wake was used up, so the next sleep waits its full time.
        var waited = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await service.SleepAsync(TimeSpan.FromMilliseconds(200), stop.Token));
        Assert.True(waited.ElapsedMilliseconds >= 150, $"The second sleep returned after {waited.ElapsedMilliseconds} ms");
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TriggerImmediateRun_DuringInProgressRun_RunsFollowUpPromptlyAsManualAsync()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(1));

        await StartAndTriggerDuringRunAsync(service);

        Assert.True(service.SecondRunCompleted.IsCompletedSuccessfully);
        Assert.Equal(RunTrigger.Manual, service.SecondRunTrigger);
    }

    [Fact]
    public async Task TriggerImmediateRun_DuringInProgressRun_WhilePaused_RunsFollowUpPromptlyAsync()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.Zero);

        await StartAndTriggerDuringRunAsync(service);

        Assert.True(service.SecondRunCompleted.IsCompletedSuccessfully);
        Assert.Equal(RunTrigger.Manual, service.SecondRunTrigger);
    }

    // The retry of a failed startup run is a scheduled run.
    [Fact]
    public async Task TheRetryOfAFailedStartupRun_IsScheduled()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(1)) { FailFirstRun = true };
        service.SetRunOnStartup(true);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.SecondRunCompleted.WaitAsync(Timeout);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(RunTrigger.Scheduled, service.SecondRunTrigger);
    }

    // Startup only (an interval of -1 hour) with run on startup on does its one pass at start.
    [Fact]
    public async Task AStartupOnlySchedule_RunsItsStartupPass()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(-1));
        service.SetRunOnStartup(true);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.FirstRunStarted.WaitAsync(Timeout);
            Assert.Equal(1, service.RunCount);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    // A Run Now dismissed before the loop takes it keeps the next scheduled run where it was.
    [Fact]
    public async Task ADismissedRunNow_KeepsTheNextRunWhereItWas()
    {
        using var service = new GatedConfigurableProbeService(TimeSpan.FromHours(1));
        await service.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (service.NextRunUtc is null)
            {
                Assert.True(DateTime.UtcNow < deadline, "The loop never set its next run");
                await Task.Delay(10);
            }

            var due = service.NextRunUtc;
            await Task.Delay(300);
            var dismissed = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
            dismissed.Cancel(null, Guid.Empty);
            Assert.True((await service.TryTriggerImmediateRunAsync(dismissed)).Admitted);
            await Task.Delay(300);

            Assert.Equal(0, service.RunCount);
            Assert.Equal(due, service.NextRunUtc);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task StartAndTriggerDuringRunAsync(GatedConfigurableProbeService service)
    {
        var first = new RunNotice(NotificationMode.Silent, RunTrigger.Manual);
        await service.TryTriggerImmediateRunAsync(first);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await service.FirstRunStarted.WaitAsync(Timeout);

            Assert.Same(first, service.CurrentRunNotice);
            var second = new RunNotice(NotificationMode.Manual, RunTrigger.Manual);
            Assert.Same(second, (await service.TryTriggerImmediateRunAsync(second)).Retained);
            Assert.Same(second, (await service.TryTriggerImmediateRunAsync(
                new RunNotice(NotificationMode.All, RunTrigger.Manual))).Retained);
            Assert.Same(first, service.CurrentRunNotice);
            service.ReleaseFirstRun();

            await service.SecondRunCompleted.WaitAsync(Timeout);
            Assert.Same(second, service.CurrentRunNotice);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class GatedConfigurableProbeService : ConfigurableScheduledService
    {
        private readonly TaskCompletionSource _firstRunStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstRun =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondRunCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public GatedConfigurableProbeService(TimeSpan interval, bool queueManualRuns = true)
            : base(NullLogger<GatedConfigurableProbeService>.Instance, interval)
        {
            QueueManualRuns = queueManualRuns;
        }

        protected override bool QueueManualRuns { get; }
        // Set, the first run fails at once and its retry follows without a back-off.
        public bool FailFirstRun { get; init; }
        protected override TimeSpan ErrorRetryDelay => FailFirstRun ? TimeSpan.Zero : base.ErrorRetryDelay;
        public new Task<bool> HasPendingRunAsync() => base.HasPendingRunAsync();
        public Task<bool> SleepAsync(TimeSpan delay, CancellationToken token) => InterruptibleDelayAsync(delay, token);
        public int RunCount => Volatile.Read(ref _runCount);
        public Task<(bool ShuttingDown, bool RunFailed)> InvokeRunAsync(
            RunNotice? notice, CancellationToken token = default)
            => RunScheduledWorkAsync(ServiceName, RunTrigger.Scheduled, ExecuteWorkAsync, token,
                "{ServiceName} failed", () => { }, notice);

        public Task FirstRunStarted => _firstRunStarted.Task;
        public Task SecondRunCompleted => _secondRunCompleted.Task;
        public RunTrigger? SecondRunTrigger { get; private set; }
        public Task<RunNotice?> TakePendingManualRunAsync() => ConsumePendingManualRunAsync();
        public Task<RunNotice?> TakePendingDeferredRunAsync() => ConsumePendingDeferredRunAsync();
        public void SelectNotice(RunNotice? notice) => SelectRunNotice(RunTrigger.Manual, notice);

        public void ReleaseFirstRun() => _releaseFirstRun.TrySetResult();

        protected override string ServiceName => "GatedConfigurableProbe";
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override async Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            var run = Interlocked.Increment(ref _runCount);
            if (run == 1)
            {
                _firstRunStarted.TrySetResult();
                if (FailFirstRun) throw new InvalidOperationException("The first run fails");
                await _releaseFirstRun.Task.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
            }
            else if (run == 2)
            {
                SecondRunTrigger = CurrentRunTrigger;
                _secondRunCompleted.TrySetResult();
            }
        }
    }
}
