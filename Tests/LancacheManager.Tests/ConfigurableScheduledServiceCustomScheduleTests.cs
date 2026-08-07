using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Platform;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// A generic schedule can run on a cron expression instead of a plain interval. The loop keeps its
/// shape - it still computes one next-run time and sleeps until it - so these tests pin the three
/// things the change is actually for: the next run comes from the expression rather than from the
/// interval, it lands on the same wall-clock instant after a restart, and a schedule that can never
/// fire idles quietly instead of running work or spinning.
/// </summary>
public class ConfigurableScheduledServiceCustomScheduleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    // ---- The next run comes from the expression, not the interval ----

    [Fact]
    public async Task NextRunUtc_WithACustomSchedule_ComesFromTheExpressionNotTheIntervalAsync()
    {
        // Six-hour interval against a daily 02:00 schedule: the two answers are far enough apart that
        // one cannot be mistaken for the other.
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        service.UpdateCustomSchedule(DailyAtTwo());

        await RunOnceAsync(service);

        var expected = ScheduleTiming.ComputeNextRun(DailyAtTwo(), DateTime.UtcNow);
        Assert.NotNull(service.NextRunUtc);
        Assert.NotNull(expected);
        Assert.True(
            (service.NextRunUtc!.Value - expected!.Value).Duration() < TimeSpan.FromMinutes(1),
            $"next run {service.NextRunUtc} should be the schedule's occurrence {expected}");
    }

    [Fact]
    public async Task NextRunUtc_WithNoCustomSchedule_IsStillTheIntervalAsync()
    {
        // Control for the test above: the same probe with no schedule must keep the old behaviour, or
        // the schedule test would pass for every service whether or not it had one.
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));

        await RunOnceAsync(service);

        Assert.NotNull(service.NextRunUtc);
        var fromNow = service.NextRunUtc!.Value - DateTime.UtcNow;
        Assert.True(
            (fromNow - TimeSpan.FromHours(6)).Duration() < TimeSpan.FromMinutes(1),
            $"next run {service.NextRunUtc} should be six hours out, was {fromNow}");
    }

    [Fact]
    public async Task NextRunUtc_AfterTheScheduleIsCleared_IsBackOnTheIntervalAsync()
    {
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        service.UpdateCustomSchedule(DailyAtTwo());

        // Cleared while the loop is still running, which is what the Schedules page does: the sleep it
        // is sitting in was computed from the schedule and has to be recomputed from the interval.
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.FirstRun.WaitAsync(Timeout);
            await SettleAsync();

            service.UpdateCustomSchedule(null);
            await SettleAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.NotNull(service.NextRunUtc);
        var fromNow = service.NextRunUtc!.Value - DateTime.UtcNow;
        Assert.True(
            (fromNow - TimeSpan.FromHours(6)).Duration() < TimeSpan.FromMinutes(1),
            $"clearing the schedule should restore the six-hour interval, next run was {fromNow} out");
    }

    // ---- A restart leaves a schedule exactly where it was ----

    [Fact]
    public async Task NextRunUtc_AcrossARestart_LandsOnTheSameInstantAsync()
    {
        // The bug this removes: an interval counts from process start, so restarting the app at 09:00
        // moves a 6-hourly service's next run to 15:00 no matter when it was previously due. A cron
        // time is absolute, so the second process must arrive at the same instant as the first.
        DateTime? beforeRestart;
        using (var first = new CustomScheduleProbeService(TimeSpan.FromHours(6)))
        {
            first.UpdateCustomSchedule(DailyAtTwo());
            await RunOnceAsync(first);
            beforeRestart = first.NextRunUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        DateTime? afterRestart;
        using (var second = new CustomScheduleProbeService(TimeSpan.FromHours(6)))
        {
            second.UpdateCustomSchedule(DailyAtTwo());
            await RunOnceAsync(second);
            afterRestart = second.NextRunUtc;
        }

        Assert.Equal(beforeRestart, afterRestart);
    }

    [Fact]
    public async Task NextRunUtc_AcrossARestart_OnAPlainInterval_ShiftsByTheDowntimeAsync()
    {
        // The control run for the test above: without a schedule the same two processes DO disagree,
        // which is what makes the equality assertion there evidence rather than a coincidence.
        DateTime? beforeRestart;
        using (var first = new CustomScheduleProbeService(TimeSpan.FromHours(6)))
        {
            await RunOnceAsync(first);
            beforeRestart = first.NextRunUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        DateTime? afterRestart;
        using (var second = new CustomScheduleProbeService(TimeSpan.FromHours(6)))
        {
            await RunOnceAsync(second);
            afterRestart = second.NextRunUtc;
        }

        Assert.NotNull(beforeRestart);
        Assert.NotNull(afterRestart);
        Assert.True(
            afterRestart!.Value - beforeRestart!.Value >= TimeSpan.FromMilliseconds(300),
            $"an interval should have shifted by the downtime, moved {afterRestart.Value - beforeRestart.Value}");
    }

    // ---- A schedule that can never fire ----

    [Fact]
    public async Task CustomScheduleThatCanNeverFire_LeavesNoNextRunAndDoesNoWorkAsync()
    {
        // 15:00 daily can never start inside a 22:00-06:00 window, so the schedule has no next run at
        // all. The loop must idle on its ordinary interval rather than either running the work or
        // recomputing the same null answer in a tight loop.
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(1));
        service.UpdateCustomSchedule(UnreachableSchedule());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Null(service.NextRunUtc);
        Assert.Equal(0, service.RunCount);
    }

    [Fact]
    public async Task CustomScheduleThatCanNeverFire_WarnsOnceNotOnEveryWakeAsync()
    {
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(1));
        service.UpdateCustomSchedule(UnreachableSchedule());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync();

            // Each interval change wakes the loop, which re-reaches the same unreachable schedule.
            // Three more wakes must not produce three more warnings.
            for (var wake = 0; wake < 3; wake++)
            {
                service.ChangeInterval(TimeSpan.FromHours(2 + wake));
                await SettleAsync();
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, service.Logger.CountWarningsContaining("has no next run"));
    }

    [Fact]
    public async Task ANewUnreachableSchedule_WarnsAgainAsync()
    {
        // The warning is suppressed per schedule, not per service: an admin who edits a broken
        // schedule into another broken one has to be told again, or the log says the problem was
        // fixed when it was not.
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(1));
        service.UpdateCustomSchedule(UnreachableSchedule());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync();

            service.UpdateCustomSchedule(new CustomSchedule
            {
                Expression = "0 12 * * *",
                TimeZoneId = "UTC",
                WindowStart = new TimeOnly(22, 0),
                WindowEnd = new TimeOnly(23, 0),
            });
            await SettleAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, service.Logger.CountWarningsContaining("has no next run"));
    }

    // ---- The other scheduling base honours a schedule the same way ----

    [Fact]
    public async Task OtherBase_NextRunUtc_WithACustomSchedule_ComesFromTheExpressionAsync()
    {
        // The eight maintenance schedules sit on this loop, which reaches its first sleep through a
        // different branch than the configurable one, so it needs its own coverage rather than an
        // assumption that the sibling test covers both.
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(6));
        service.UpdateCustomSchedule(DailyAtTwo());

        await StartAndSettleAsync(service);

        var expected = ScheduleTiming.ComputeNextRun(DailyAtTwo(), DateTime.UtcNow);
        Assert.NotNull(service.NextRunUtc);
        Assert.NotNull(expected);
        Assert.True(
            (service.NextRunUtc!.Value - expected!.Value).Duration() < TimeSpan.FromMinutes(1),
            $"next run {service.NextRunUtc} should be the schedule's occurrence {expected}");
    }

    [Fact]
    public async Task OtherBase_NextRunUtc_WithNoCustomSchedule_IsStillTheIntervalAsync()
    {
        // Control run: without a schedule this loop must behave exactly as it did before.
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(6));

        await StartAndSettleAsync(service);

        Assert.NotNull(service.NextRunUtc);
        var fromNow = service.NextRunUtc!.Value - DateTime.UtcNow;
        Assert.True(
            (fromNow - TimeSpan.FromHours(6)).Duration() < TimeSpan.FromMinutes(1),
            $"next run {service.NextRunUtc} should be six hours out, was {fromNow}");
    }

    [Fact]
    public async Task OtherBase_CustomScheduleThatCanNeverFire_LeavesNoNextRunAndDoesNoWorkAsync()
    {
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(1));
        service.UpdateCustomSchedule(UnreachableSchedule());

        await StartAndSettleAsync(service);

        Assert.Null(service.NextRunUtc);
        Assert.Equal(0, service.RunCount);
        Assert.Equal(1, service.Logger.CountWarningsContaining("has no next run"));
    }

    [Fact]
    public async Task OtherBase_CustomScheduleThatCanNeverFire_WarnsOnceNotOnEveryWakeAsync()
    {
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(1));
        service.UpdateCustomSchedule(UnreachableSchedule());

        await service.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync();

            // Each interval change wakes the loop, which re-reaches the same unreachable schedule.
            for (var wake = 0; wake < 3; wake++)
            {
                service.SetInterval(TimeSpan.FromHours(2 + wake));
                await SettleAsync();
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Equal(1, service.Logger.CountWarningsContaining("has no next run"));
        Assert.Equal(0, service.RunCount);
    }

    [Fact]
    public async Task OtherBase_NextRunUtc_AcrossARestart_LandsOnTheSameInstantAsync()
    {
        DateTime? beforeRestart;
        using (var first = new OtherBaseProbeService(TimeSpan.FromHours(6)))
        {
            first.UpdateCustomSchedule(DailyAtTwo());
            await StartAndSettleAsync(first);
            beforeRestart = first.NextRunUtc;
        }

        await Task.Delay(TimeSpan.FromMilliseconds(400));

        DateTime? afterRestart;
        using (var second = new OtherBaseProbeService(TimeSpan.FromHours(6)))
        {
            second.UpdateCustomSchedule(DailyAtTwo());
            await StartAndSettleAsync(second);
            afterRestart = second.NextRunUtc;
        }

        Assert.Equal(beforeRestart, afterRestart);
    }

    // ---- The registry stores it, and refuses it where it would not work ----

    [Fact]
    public void SetCustomSchedule_OnAConfigurableService_ReachesBothTheServiceAndTheStore()
    {
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);

        var accepted = registry.SetCustomSchedule(nameof(CustomScheduleProbeService), DailyAtTwo());

        Assert.True(accepted);
        Assert.NotNull(service.ConfiguredCustomSchedule);
        Assert.Equal("0 2 * * *", service.ConfiguredCustomSchedule!.Expression);
        Assert.Equal("0 2 * * *", recorder.Schedules[nameof(CustomScheduleProbeService)].Expression);
    }

    [Fact]
    public void SetCustomSchedule_WithNull_ClearsTheServiceAndTheStore()
    {
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);
        registry.SetCustomSchedule(nameof(CustomScheduleProbeService), DailyAtTwo());

        var accepted = registry.SetCustomSchedule(nameof(CustomScheduleProbeService), null);

        Assert.True(accepted);
        Assert.Null(service.ConfiguredCustomSchedule);
        Assert.Empty(recorder.Schedules);
    }

    [Fact]
    public void SetCustomSchedule_OnTheOtherSchedulingBase_ReachesBothTheServiceAndTheStore()
    {
        // The eight maintenance schedules (cache scan, game detection, log rotation and the rest) run
        // on the sibling loop, so a schedule has to reach them the same way or most of the Schedules
        // page could not use the feature at all.
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);

        var accepted = registry.SetCustomSchedule("gameDetection", DailyAtTwo());

        Assert.True(accepted);
        Assert.NotNull(service.ConfiguredCustomSchedule);
        Assert.Equal("0 2 * * *", service.ConfiguredCustomSchedule!.Expression);
        Assert.Equal("0 2 * * *", recorder.Schedules["gameDetection"].Expression);
    }

    [Fact]
    public void Get_ForAnOtherBaseServiceOnACustomSchedule_CarriesTheScheduleToTheFrontend()
    {
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);
        registry.SetCustomSchedule("gameDetection", DailyAtTwo());

        var info = registry.Get("gameDetection");

        Assert.NotNull(info);
        Assert.NotNull(info!.CustomSchedule);
        Assert.Equal("0 2 * * *", info.CustomSchedule!.Expression);
        Assert.NotEmpty(recorder.Schedules);
    }

    [Fact]
    public void ResetToDefaults_ClearsTheCustomScheduleOnTheOtherBaseToo()
    {
        using var service = new OtherBaseProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);
        registry.SetCustomSchedule("gameDetection", DailyAtTwo());

        registry.ResetToDefaults();

        Assert.Null(service.ConfiguredCustomSchedule);
        Assert.Empty(recorder.Schedules);
    }

    [Fact]
    public void SetCustomSchedule_OnAServiceWhoseScheduleIsPerService_IsRefused()
    {
        // Scheduled prefill's own interval is a fixed one-minute due-check poll, and its real schedules
        // live per platform. Driving that poll off a cron expression would only due-check at the cron
        // times, silently delaying every platform.
        using var service = new PerServiceScheduleProbeService();
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);

        var accepted = registry.SetCustomSchedule(nameof(PerServiceScheduleProbeService), DailyAtTwo());

        Assert.False(accepted);
        Assert.Null(service.ConfiguredCustomSchedule);
        Assert.Empty(recorder.Schedules);
    }

    [Fact]
    public void Get_ForAServiceOnACustomSchedule_CarriesTheScheduleToTheFrontend()
    {
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);
        registry.SetCustomSchedule(nameof(CustomScheduleProbeService), DailyAtTwo());

        var info = registry.Get(nameof(CustomScheduleProbeService));

        Assert.NotNull(info);
        Assert.NotNull(info!.CustomSchedule);
        Assert.Equal("0 2 * * *", info.CustomSchedule!.Expression);
        Assert.Equal("UTC", info.CustomSchedule.TimeZoneId);
    }

    [Fact]
    public void ResetToDefaults_ClearsTheCustomScheduleToo()
    {
        // Resetting only the interval would leave the schedule overriding the interval that was just
        // restored, so the one setting the user wanted reset is the one that survives.
        using var service = new CustomScheduleProbeService(TimeSpan.FromHours(6));
        var (state, recorder) = CreateScheduleState();
        var registry = CreateRegistry(state, service);
        registry.SetCustomSchedule(nameof(CustomScheduleProbeService), DailyAtTwo());

        registry.ResetToDefaults();

        Assert.Null(service.ConfiguredCustomSchedule);
        Assert.Empty(recorder.Schedules);
    }

    // ---- The endpoint refuses what the loop could not honour ----

    [Fact]
    public async Task SetCustomScheduleAsync_WithAScheduleThatCanNeverFire_Answers400AndStoresNothingAsync()
    {
        var (registry, recorder) = CreateRegistryStub();
        var controller = new ScheduleController(registry);

        var result = await controller.SetCustomScheduleAsync(
            "gameDetection",
            new UpdateScheduleCustomScheduleRequest { CustomSchedule = UnreachableSchedule() });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("never runs", Assert.IsType<string>(badRequest.Value), StringComparison.Ordinal);
        Assert.Equal(0, recorder.SetCalls);
    }

    [Fact]
    public async Task SetCustomScheduleAsync_WhenTheServiceCannotTakeOne_Answers400Async()
    {
        var (registry, recorder) = CreateRegistryStub();
        recorder.Accepts = false;
        var controller = new ScheduleController(registry);

        var result = await controller.SetCustomScheduleAsync(
            "gameDetection",
            new UpdateScheduleCustomScheduleRequest { CustomSchedule = DailyAtTwo() });

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(1, recorder.SetCalls);
    }

    [Fact]
    public async Task SetCustomScheduleAsync_WithNoSchedule_ClearsItAsync()
    {
        var (registry, recorder) = CreateRegistryStub();
        var controller = new ScheduleController(registry);

        var result = await controller.SetCustomScheduleAsync(
            "gameDetection",
            new UpdateScheduleCustomScheduleRequest { CustomSchedule = null });

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(1, recorder.SetCalls);
        Assert.Null(recorder.LastSchedule);
    }

    // ---- It survives the state file ----

    [Fact]
    public void SaveThenLoad_ServiceCustomSchedule_SurvivesTheStateFile()
    {
        using var root = new TempStateRoot();
        var first = root.CreateStateService();

        first.SetServiceCustomSchedule("gameDetection", new CustomSchedule
        {
            Expression = "0 3 * * 1",
            TimeZoneId = "Europe/Berlin",
            WindowStart = new TimeOnly(22, 0),
            WindowEnd = new TimeOnly(6, 0),
        });

        // A second instance reads the file back through the persisted shape, which is what proves both
        // mapping directions were wired and not just the in-memory model.
        var reloaded = root.CreateStateService().GetServiceCustomSchedule("gameDetection");

        Assert.NotNull(reloaded);
        Assert.Equal("0 3 * * 1", reloaded!.Expression);
        Assert.Equal("Europe/Berlin", reloaded.TimeZoneId);
        Assert.Equal(new TimeOnly(22, 0), reloaded.WindowStart);
        Assert.Equal(new TimeOnly(6, 0), reloaded.WindowEnd);
    }

    [Fact]
    public void ClearServiceCustomSchedule_RemovesItFromTheStateFile()
    {
        using var root = new TempStateRoot();
        var service = root.CreateStateService();
        service.SetServiceCustomSchedule("gameDetection", DailyAtTwo());

        service.ClearServiceCustomSchedule("gameDetection");

        Assert.Null(root.CreateStateService().GetServiceCustomSchedule("gameDetection"));
    }

    [Fact]
    public void GetServiceCustomSchedule_ForAServiceThatHasNone_IsNull()
    {
        using var root = new TempStateRoot();

        Assert.Null(root.CreateStateService().GetServiceCustomSchedule("gameDetection"));
    }

    // ---- helpers ----

    private static CustomSchedule DailyAtTwo() => new()
    {
        Expression = "0 2 * * *",
        TimeZoneId = "UTC",
    };

    // 15:00 daily can never start inside a 22:00-06:00 window, so this schedule has no next run.
    private static CustomSchedule UnreachableSchedule() => new()
    {
        Expression = "0 15 * * *",
        TimeZoneId = "UTC",
        WindowStart = new TimeOnly(22, 0),
        WindowEnd = new TimeOnly(6, 0),
    };

    /// <summary>
    /// Lets the loop settle into its sleep. NextRunUtc is published immediately before that sleep, so
    /// reading it any earlier races the mid-run value the run-end broadcast carries.
    /// </summary>
    private static Task SettleAsync() => Task.Delay(TimeSpan.FromMilliseconds(300));

    /// <summary>
    /// Starts the sibling-base probe, lets it reach its first sleep, then stops it. That loop skips its
    /// first execution by design, so the value under test is the one it commits to before sleeping.
    /// </summary>
    private static async Task StartAndSettleAsync(OtherBaseProbeService service)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await SettleAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task RunOnceAsync(CustomScheduleProbeService service)
    {
        await service.StartAsync(CancellationToken.None);
        try
        {
            await service.FirstRun.WaitAsync(Timeout);
            await SettleAsync();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static (IStateService State, RecordingScheduleStateProxy Recorder) CreateScheduleState()
    {
        var proxy = DispatchProxy.Create<IStateService, RecordingScheduleStateProxy>();
        return ((IStateService)proxy, (RecordingScheduleStateProxy)(object)proxy);
    }

    private static (IServiceScheduleRegistry Registry, RecordingRegistryProxy Recorder) CreateRegistryStub()
    {
        var proxy = DispatchProxy.Create<IServiceScheduleRegistry, RecordingRegistryProxy>();
        return ((IServiceScheduleRegistry)proxy, (RecordingRegistryProxy)(object)proxy);
    }

    private static ServiceScheduleRegistry CreateRegistry(IStateService state, IHostedService service)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy
            .Create<ISignalRNotificationService, NullReturningProxy>();
        return new ServiceScheduleRegistry(new[] { service }, state, notifications);
    }

    // ---- probes and fakes (hand-rolled; no mocking framework, matching the suite idiom) ----

    /// <summary>
    /// Configurable probe that records every run and signals when the loop reaches its sleep, so a
    /// test can read NextRunUtc at the point the loop has actually committed to it rather than racing
    /// the mid-run value.
    /// </summary>
    private sealed class CustomScheduleProbeService : ConfigurableScheduledService
    {
        private readonly TaskCompletionSource _firstRun =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _runCount;

        public CustomScheduleProbeService(TimeSpan interval)
            : this(interval, new WarningCountingLogger())
        {
        }

        private CustomScheduleProbeService(TimeSpan interval, WarningCountingLogger logger)
            : base(logger, interval)
        {
            Logger = logger;
        }

        public WarningCountingLogger Logger { get; }
        public Task FirstRun => _firstRun.Task;
        public int RunCount => Volatile.Read(ref _runCount);

        protected override string ServiceName => "CustomScheduleProbe";
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => true;

        public void ChangeInterval(TimeSpan interval) => UpdateInterval(interval);

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref _runCount);
            _firstRun.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A probe on the OTHER scheduling base, the one the eight maintenance schedules use. Keyed as an
    /// allowlisted service so the registry actually tracks it, and it does not run on startup so the
    /// loop's skip-first branch is the one under test.
    /// </summary>
    private sealed class OtherBaseProbeService : ScheduledBackgroundService
    {
        private readonly TimeSpan _interval;
        private int _runCount;

        public OtherBaseProbeService(TimeSpan interval)
            : this(interval, new WarningCountingLogger())
        {
        }

        private OtherBaseProbeService(TimeSpan interval, WarningCountingLogger logger)
            : base(logger, new ConfigurationBuilder().Build())
        {
            _interval = interval;
            Logger = logger;
        }

        public WarningCountingLogger Logger { get; }
        public int RunCount => Volatile.Read(ref _runCount);

        public override string ServiceKey => "gameDetection";
        protected override string ServiceName => "OtherBaseProbe";
        protected override TimeSpan Interval => _interval;
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken)
        {
            Interlocked.Increment(ref _runCount);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A configurable service whose real schedules live per nested service, the shape scheduled prefill
    /// has: its own interval is a fixed poll cadence and must not be driven by an expression.
    /// </summary>
    private sealed class PerServiceScheduleProbeService : ConfigurableScheduledService, IScheduleEnabledGate
    {
        public PerServiceScheduleProbeService()
            : base(NullLogger<PerServiceScheduleProbeService>.Instance, TimeSpan.FromMinutes(1))
        {
        }

        protected override string ServiceName => "PerServiceScheduleProbe";
        protected override TimeSpan StartupDelay => TimeSpan.Zero;
        public override bool DefaultRunOnStartup => false;

        public bool HasAnyServiceEnabled() => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class WarningCountingLogger : ILogger
    {
        private readonly List<string> _warnings = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
            {
                return;
            }

            lock (_warnings)
            {
                _warnings.Add(formatter(state, exception));
            }
        }

        public int CountWarningsContaining(string fragment)
        {
            lock (_warnings)
            {
                return _warnings.Count(message => message.Contains(fragment, StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// Records the custom-schedule writes and answers the prefill config with a real default, since
    /// ResetToDefaults reaches for it. Every other state call falls through to the shared proxy's type
    /// default, which is enough for the registry paths under test.
    /// </summary>
    private class RecordingScheduleStateProxy : NullReturningProxy
    {
        public Dictionary<string, CustomSchedule> Schedules { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IStateService.GetServiceCustomSchedule):
                    return Schedules.TryGetValue((string)args![0]!, out var stored) ? stored : null;

                case nameof(IStateService.SetServiceCustomSchedule):
                    Schedules[(string)args![0]!] = (CustomSchedule)args[1]!;
                    return null;

                case nameof(IStateService.ClearServiceCustomSchedule):
                    Schedules.Remove((string)args![0]!);
                    return null;

                case nameof(IStateService.GetScheduledPrefillConfig):
                    return ScheduledPrefillConfigFactory.CreateDefault();

                default:
                    return base.Invoke(targetMethod, args);
            }
        }
    }

    /// <summary>
    /// Answers the controller's lookup with a real schedule row and records what it was asked to store,
    /// so a test can tell "rejected before the registry" apart from "the registry refused it".
    /// </summary>
    private class RecordingRegistryProxy : NullReturningProxy
    {
        public bool Accepts { get; set; } = true;
        public int SetCalls { get; private set; }
        public CustomSchedule? LastSchedule { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IServiceScheduleRegistry.Get):
                    return new ServiceScheduleInfo { Key = (string)args![0]! };

                case nameof(IServiceScheduleRegistry.SetCustomSchedule):
                    SetCalls++;
                    LastSchedule = (CustomSchedule?)args![1];
                    return Accepts;

                default:
                    return base.Invoke(targetMethod, args);
            }
        }
    }

    private sealed class TempStateRoot : IDisposable
    {
        private readonly string _root;

        public TempStateRoot()
        {
            _root = Path.Combine(Path.GetTempPath(), "lcm-generic-schedule", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public StateService CreateStateService()
        {
            var pathResolver = new TempDirPathResolver(_root);
            var configuration = new ConfigurationBuilder().Build();
            var apiKeyService = new ApiKeyService(NullLogger<ApiKeyService>.Instance, configuration, pathResolver);
            var dataProtection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_root, "dp-keys")));
            var encryption = new SecureStateEncryptionService(
                dataProtection, apiKeyService, NullLogger<SecureStateEncryptionService>.Instance);
            var steamAuthStorage = new SteamAuthStorageService(
                NullLogger<SteamAuthStorageService>.Instance, pathResolver, encryption);

            return new StateService(
                NullLogger<StateService>.Instance, pathResolver, encryption, steamAuthStorage);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory that a file handle is still holding open is not worth failing a
                // green test over; the OS reclaims it.
            }
        }
    }

    private sealed class TempDirPathResolver : PathResolverBase
    {
        private readonly string _basePath;

        public TempDirPathResolver(string basePath) : base(NullLogger.Instance)
        {
            _basePath = basePath;
        }

        protected override string BasePath => _basePath;
        protected override string RustExecutableExtension => string.Empty;

        public override string ResolvePath(string relativePath) => relativePath;
        public override string NormalizePath(string path) => path;
        public override bool IsDockerSocketAvailable() => false;
    }
}
