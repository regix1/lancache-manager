using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// Proves that a Run Now (TriggerImmediateRun) pending at loop start is honored immediately rather than
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
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task TriggerImmediateRun_DuringFirstSkipWindow_RunsWorkPromptlyAsync()
    {
        using var service = new ManualRunProbeService();

        // Pending at loop start, before the loop ever creates its interruptible-delay source - the case
        // the old skip-first branch missed, deferring the run by a full (here, one hour) interval.
        service.TriggerImmediateRun();

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

    private static async Task StartAndTriggerDuringRunAsync(GatedManualRunProbeService service)
    {
        // First trigger starts run #1 (the gated probe blocks inside it until released).
        service.TriggerImmediateRun();
        await service.StartAsync(CancellationToken.None);

        try
        {
            await service.FirstRunStarted.WaitAsync(Timeout);

            // Second trigger lands while run #1 is still executing - the regression scenario.
            service.TriggerImmediateRun();
            service.ReleaseFirstRun();

            await service.SecondRunCompleted.WaitAsync(Timeout);
        }
        finally
        {
            service.ReleaseFirstRun();
            await service.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ManualRunProbeService : ScheduledBackgroundService
    {
        private readonly TaskCompletionSource _firstExecution =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualRunProbeService()
            : base(NullLogger<ManualRunProbeService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public Task FirstExecution => _firstExecution.Task;

        protected override string ServiceName => "ManualRunProbe";

        // A long interval so a deferred first run would not complete within the test's timeout.
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            _firstExecution.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Probe that blocks inside its first work run until released, so a test can fire a second
    /// TriggerImmediateRun while the first run is in progress, then observe how promptly (and under
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

    private static async Task StartAndTriggerDuringRunAsync(GatedConfigurableProbeService service)
    {
        service.TriggerImmediateRun();
        await service.StartAsync(CancellationToken.None);

        try
        {
            await service.FirstRunStarted.WaitAsync(Timeout);

            service.TriggerImmediateRun();
            service.ReleaseFirstRun();

            await service.SecondRunCompleted.WaitAsync(Timeout);
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

        public GatedConfigurableProbeService(TimeSpan interval)
            : base(NullLogger<GatedConfigurableProbeService>.Instance, interval)
        {
        }

        public Task FirstRunStarted => _firstRunStarted.Task;
        public Task SecondRunCompleted => _secondRunCompleted.Task;
        public RunTrigger? SecondRunTrigger { get; private set; }

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
