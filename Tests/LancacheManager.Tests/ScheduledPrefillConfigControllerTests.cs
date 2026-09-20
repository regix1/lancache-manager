using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Middleware;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

public sealed class ScheduledPrefillConfigControllerTests
{
    [Fact]
    public async Task NarrowWritesPreserveOtherServicesAndRootSettings()
    {
        using var test = new Harness();
        var stale = test.State.GetScheduledPrefillConfig();
        await test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.FullPersistence });
        test.State.SetScheduledPrefillConfig(stale);
        Assert.Equal(PersistenceMode.KeepAcrossRestart, test.State.GetScheduledPrefillConfig().PersistenceMode);

        await test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.FullPersistence });
        var before = test.State.GetScheduledPrefillConfig();
        var id = before.Steam.Schedules[0].Id;
        await test.Controller.SetEnabledAsync(PrefillPlatform.Steam, id, new() { Enabled = true });
        var saved = test.State.GetScheduledPrefillConfig();
        Assert.Equal(PersistenceMode.FullPersistence, saved.PersistenceMode);
        Assert.Equal(JsonSerializer.Serialize(before.Epic), JsonSerializer.Serialize(saved.Epic));
        Assert.Equal(before.MaxServiceRuntime, saved.MaxServiceRuntime);
        Assert.Equal(before.StallTimeout, saved.StallTimeout);
        Assert.Equal(before.Version, saved.Version);
        Assert.True(saved.Steam.Schedules[0].Enabled);
    }

    [Fact]
    public async Task ConcurrentEnabledAndTimingWritesUseTheLatestRecord()
    {
        using var test = new Harness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var id = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        test.State.BeforeWrite = () =>
        {
            test.State.BeforeWrite = null;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var enabled = Task.Run(() => test.Controller.SetEnabledAsync(PrefillPlatform.Steam, id, new() { Enabled = true }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var timing = Task.Run(() =>
        {
            started.Set();
            return test.Controller.SetTimingAsync(PrefillPlatform.Steam, id,
                new() { IntervalHours = 37, CustomSchedule = null });
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(timing.IsCompleted);
        release.Set();
        await Task.WhenAll(enabled, timing);
        var saved = test.State.GetScheduledPrefillConfig().Steam.Schedules[0];
        Assert.True(saved.Enabled);
        Assert.Equal(37, saved.IntervalHours);
        Assert.Equal(2, test.Broadcast.Calls);
    }

    [Fact]
    public async Task ConcurrentCreateDeleteAndAllEnabledCannotResurrectRecords()
    {
        using var test = new Harness();
        var original = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        var second = Schedule(Guid.NewGuid(), "Second");
        await test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, second);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        test.State.BeforeWrite = () =>
        {
            test.State.BeforeWrite = null;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var deleted = Task.Run(() => test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, original));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var third = Schedule(Guid.NewGuid(), "Third");
        var created = Task.Run(() => test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, third));
        var enabled = Task.Run(() => test.Controller.SetAllEnabledAsync(new() { Enabled = true }));
        var settings = Task.Run(() => test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.FullPersistence }));
        release.Set();
        await Task.WhenAll(deleted, created, enabled, settings);
        var saved = test.State.GetScheduledPrefillConfig();
        Assert.Equal(new[] { second.Id, third.Id }, saved.Steam.Schedules.Select(schedule => schedule.Id));
        Assert.True(saved.Steam.Schedules[0].Enabled);
        Assert.Equal(PersistenceMode.FullPersistence, saved.PersistenceMode);
        await test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, second.Id);
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, third.Id));
        Assert.Single(test.State.GetScheduledPrefillConfig().Steam.Schedules);
    }

    [Fact]
    public async Task PersistenceFailureLeavesConfigHistoriesAndFileUnchanged()
    {
        using var test = new Harness();
        var id = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        var key = id.ToString("N");
        var instant = DateTime.UtcNow.AddHours(-3);
        test.State.SetScheduledPrefillServiceLastRun(key, instant);
        test.State.SetScheduledPrefillServiceLastActualRun(key, instant);
        var before = JsonSerializer.Serialize(test.State.GetScheduledPrefillConfig());
        var bytes = File.ReadAllBytes(test.Context.StatePath);
        test.State.FailWrites = true;
        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => test.Controller.SetConfigAsync(
            WithRemoved(test.State.GetScheduledPrefillConfig(), id)));
        Assert.Equal("errors.state.saveFailed", error.StageKey);
        Assert.Equal(before, JsonSerializer.Serialize(test.State.GetScheduledPrefillConfig()));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastRun(key));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastActualRun(key));
        Assert.Equal(bytes, File.ReadAllBytes(test.Context.StatePath));
        Assert.Equal(0, test.Broadcast.Calls);
        Assert.Throws<ServiceUnavailableException>(() => test.State.SetScheduledPrefillServiceLastRun(key, DateTime.UtcNow));
        Assert.Throws<ServiceUnavailableException>(() => test.State.SetScheduledPrefillServiceLastActualRun(key, DateTime.UtcNow));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastRun(key));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastActualRun(key));
    }

    [Fact]
    public async Task LateCompletionCannotRecreateDeletedHistoryAndLegacyKeysRemainUsable()
    {
        using var test = new Harness();
        var id = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        var retained = test.State.GetScheduledPrefillConfig().Epic.Schedules[0].Id.ToString("N");
        var key = id.ToString("N");
        var instant = DateTime.UtcNow.AddHours(-5);
        test.State.SetScheduledPrefillServiceLastRun(key, instant);
        test.State.SetScheduledPrefillServiceLastActualRun(key, instant);
        test.State.SetScheduledPrefillServiceLastRun(retained, instant);
        test.State.SetScheduledPrefillServiceLastActualRun(retained, instant);
        test.State.SetScheduledPrefillServiceLastRun("Steam", instant);
        await test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id));
        test.State.SetScheduledPrefillServiceLastRun(key, DateTime.UtcNow);
        test.State.SetScheduledPrefillServiceLastActualRun(key, DateTime.UtcNow);
        Assert.Null(test.State.GetScheduledPrefillServiceLastRun(key));
        Assert.Null(test.State.GetScheduledPrefillServiceLastActualRun(key));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastRun(retained));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastActualRun(retained));
        Assert.Equal(instant, test.State.GetScheduledPrefillServiceLastRun("Steam"));
    }

    [Fact]
    public void ConfigReferencesAreDetachedBeforeAndAfterCommit()
    {
        using var test = new Harness();
        var input = test.State.GetScheduledPrefillConfig();
        var saved = test.State.UpdateScheduledPrefillConfig(_ => input);
        input.Steam.Schedules.Clear();
        saved.Epic.Schedules.Clear();
        var read = test.State.GetScheduledPrefillConfig();
        Assert.Single(read.Steam.Schedules);
        Assert.Single(read.Epic.Schedules);
        read.Xbox.Schedules.Clear();
        Assert.Single(test.State.GetScheduledPrefillConfig().Xbox.Schedules);
        Assert.Throws<ValidationException>(() => test.Runtime.UpdateConfig(config =>
        {
            config.Steam.Schedules.Clear();
            throw new ValidationException("Invalid transformation.");
        }));
        Assert.Single(test.State.GetScheduledPrefillConfig().Steam.Schedules);
    }

    [Fact]
    public async Task EverySuccessfulMutationBroadcastsOnceAndBroadcastFailureKeepsCommit()
    {
        using var test = new Harness();
        var record = Schedule(Guid.NewGuid(), "Second");
        await test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, record);
        await test.Controller.SetScheduleAsync(PrefillPlatform.Steam, record.Id, record);
        await test.Controller.SetEnabledAsync(PrefillPlatform.Steam, record.Id, new() { Enabled = true });
        await test.Controller.SetTimingAsync(PrefillPlatform.Steam, record.Id, new() { IntervalHours = 2, CustomSchedule = null });
        await test.Controller.SetAllEnabledAsync(new() { Enabled = false });
        await test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.FullPersistence });
        await test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, record.Id);
        Assert.Equal(7, test.Broadcast.Calls);
        Assert.IsType<NoContentResult>(await test.Controller.SetConfigAsync(test.State.GetScheduledPrefillConfig()));
        Assert.Equal(8, test.Broadcast.Calls);
        test.Broadcast.Fail = true;
        var response = await test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.KillOnRestart });
        var saved = Assert.IsType<ScheduledPrefillConfigDto>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(PersistenceMode.KillOnRestart, saved.PersistenceMode);
        Assert.Equal(PersistenceMode.KillOnRestart, test.State.GetScheduledPrefillConfig().PersistenceMode);
        Assert.Equal(9, test.Broadcast.Calls);
    }

    [Fact]
    public async Task InvalidMutationDoesNotSaveOrBroadcast()
    {
        using var test = new Harness();
        var config = test.State.GetScheduledPrefillConfig();
        var id = config.Steam.Schedules[0].Id;
        var bytes = File.ReadAllBytes(test.Context.StatePath);
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.CreateScheduleAsync((PrefillPlatform)999, Schedule(Guid.NewGuid(), "New")));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.SetScheduleAsync(PrefillPlatform.Steam, id, Schedule(Guid.NewGuid(), "New")));
        await Assert.ThrowsAsync<NotFoundException>(() => test.Controller.SetEnabledAsync(PrefillPlatform.Epic, id, new() { Enabled = true }));
        await Assert.ThrowsAsync<NotFoundException>(() => test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, Guid.NewGuid()));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.DeleteScheduleAsync(PrefillPlatform.Steam, id));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, Schedule(id, "Duplicate")));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, Schedule(Guid.NewGuid(), config.Steam.Schedules[0].Name)));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, Schedule(Guid.Empty, "New")));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.CreateScheduleAsync(PrefillPlatform.Steam, Schedule(Guid.NewGuid(), " ")));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.SetSettingsAsync(new() { Mode = (PersistenceMode)999 }));
        await Assert.ThrowsAsync<ValidationException>(() => test.Controller.SetTimingAsync(PrefillPlatform.Steam, id, new() { IntervalHours = -2, CustomSchedule = null }));
        Assert.Equal(bytes, File.ReadAllBytes(test.Context.StatePath));
        Assert.Equal(0, test.Broadcast.Calls);
    }

    [Fact]
    public async Task ReservationBlocksRemovalBeforeTrackerRegistration()
    {
        using var test = new Harness();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var id = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        test.Tracker.BeforeRegister = () =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var run = Task.Run(() => test.Runtime.TriggerServiceRun(PrefillPlatform.Steam, id));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        try
        {
            await Assert.ThrowsAsync<ConflictException>(() => test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id)));
            Assert.Equal(0, test.Broadcast.Calls);
            await test.Controller.SetEnabledAsync(PrefillPlatform.Steam, id, new() { Enabled = true });
        }
        finally { release.Set(); }
        Assert.NotNull(await run);
        await test.Runtime.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DeletedScheduleCannotBeAdmittedFromAStaleTick()
    {
        using var test = new Harness();
        var stale = test.State.GetScheduledPrefillConfig();
        var schedule = stale.GetSchedulesInRunOrder().First(schedule => schedule.ServiceId == PrefillPlatform.Riot);
        await test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), schedule.ScheduleId));
        Assert.Null(test.Runtime.TriggerServiceRun(PrefillPlatform.Riot, schedule.ScheduleId));
        var registrations = 0;
        test.Tracker.BeforeRegister = () => registrations++;
        var method = typeof(ScheduledPrefillService).GetMethod("RunDueServicesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(test.Runtime, [new List<ScheduledPrefillServiceConfigDto> { schedule }, stale,
            CancellationToken.None, RunTrigger.Scheduled])!;
        Assert.Equal(0, registrations);
    }

    [Fact]
    public async Task KnownDaemonRunsAndRecoveryBlockOnlyRemoval()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var test = new Harness(services => services.AddSingleton(fixture.Daemon));
        var id = test.State.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        var run = await fixture.StartAsync("10", id);
        await Assert.ThrowsAsync<ConflictException>(() => test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id)));
        Assert.Equal(0, test.Broadcast.Calls);
        await test.Controller.SetSettingsAsync(new() { Mode = PersistenceMode.FullPersistence });
        fixture.Client.Set(run, "completed", 100, "success");
        await fixture.RefreshAsync();
        fixture.Session.Recovering = true;
        await Assert.ThrowsAsync<ConflictException>(() => test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id)));
        await test.Controller.SetEnabledAsync(PrefillPlatform.Steam, id, new() { Enabled = false });
        fixture.Session.Recovering = false;
        fixture.Session.AdmissionClosed = true;
        await Assert.ThrowsAsync<ConflictException>(() => test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id)));
        fixture.Session.AdmissionClosed = false;
        await test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id));
        Assert.Empty(test.State.GetScheduledPrefillConfig().Steam.Schedules);
    }

    [Fact]
    public async Task KnownRestoredRunReservesTheSavedRecord()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var context = new ScheduledPrefillServiceTests.TempStateServiceContext();
        context.StateService.SetScheduledPrefillConfig(ScheduledPrefillConfigFactory.CreateDefault());
        var id = context.StateService.GetScheduledPrefillConfig().Steam.Schedules[0].Id;
        var run = await fixture.StartAsync("10", id);
        var tracker = new UnifiedOperationTracker(null!, NullLogger<UnifiedOperationTracker>.Instance);
        var notifications = DispatchProxy.Create<ISignalRNotificationService, ScheduleNotifications>();
        var capture = (ScheduleNotifications)(object)notifications;
        using var services = new ServiceCollection().AddSingleton(fixture.Daemon)
            .AddSingleton<IUnifiedOperationTracker>(tracker).AddSingleton(notifications).BuildServiceProvider();
        using var runtime = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(), context.StateService);
        var restore = typeof(ScheduledPrefillService).GetMethod("RestoreRuns", BindingFlags.Instance | BindingFlags.NonPublic)!;
        restore.Invoke(runtime, [context.StateService.GetScheduledPrefillConfig()]);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ConflictException>(() => runtime.UpdateConfig(config => WithRemoved(config, id)));
        Assert.NotNull(tracker.GetOperation(run.PrefillRunId));
        capture.Release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await runtime.StopAsync(timeout.Token);
    }

    [Fact]
    public async Task RemovedRecordCannotBeRestoredFromKnownDaemonState()
    {
        await using var fixture = await RunFixture.CreateAsync(persistent: true);
        using var test = new Harness(services => services.AddSingleton(fixture.Daemon));
        var config = test.State.GetScheduledPrefillConfig();
        var id = config.Steam.Schedules[0].Id;
        await test.Controller.SetConfigAsync(WithRemoved(test.State.GetScheduledPrefillConfig(), id));
        await fixture.StartAsync("10", id);
        var restore = typeof(ScheduledPrefillService).GetMethod("RestoreRuns", BindingFlags.Instance | BindingFlags.NonPublic)!;
        restore.Invoke(test.Runtime, [config]);
        var reservations = typeof(ScheduledPrefillService).GetField("_runningSchedules", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(test.Runtime)!;
        Assert.Equal(0, reservations.GetType().GetProperty("Count")!.GetValue(reservations));
    }

    internal static ScheduledPrefillSchedule Schedule(Guid id, string name) => new()
    {
        Id = id,
        Name = name,
        IntervalHours = 24,
        Preset = ScheduledPrefillPreset.All,
        OperatingSystems = [ScheduledPrefillOperatingSystem.Windows]
    };

    private static ScheduledPrefillConfigDto WithRemoved(ScheduledPrefillConfigDto config, Guid id)
    {
        foreach (var service in config.GetServicesInRunOrder())
            service.Schedules.RemoveAll(schedule => schedule.Id == id);
        return config;
    }

    private sealed class Harness : IDisposable
    {
        public ScheduledPrefillServiceTests.TempStateServiceContext Context { get; } = new();
        public ScheduledPrefillServiceTests.TestStateService State => Context.StateService;
        public ScheduledPrefillServiceTests.ActiveOperationsTrackerProxy Tracker { get; }
        public BroadcastProxy Broadcast { get; }
        public ScheduledPrefillService Runtime { get; }
        public ScheduledPrefillConfigController Controller { get; }
        private readonly ServiceProvider _services;

        public Harness(Action<ServiceCollection>? configure = null)
        {
            State.SetScheduledPrefillConfig(ScheduledPrefillConfigFactory.CreateDefault());
            Tracker = (ScheduledPrefillServiceTests.ActiveOperationsTrackerProxy)DispatchProxy
                .Create<IUnifiedOperationTracker, ScheduledPrefillServiceTests.ActiveOperationsTrackerProxy>();
            Broadcast = (BroadcastProxy)DispatchProxy.Create<IServiceScheduleRegistry, BroadcastProxy>();
            var services = new ServiceCollection();
            services.AddSingleton((IUnifiedOperationTracker)Tracker);
            services.AddSingleton((ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>());
            configure?.Invoke(services);
            _services = services.BuildServiceProvider();
            Runtime = new ScheduledPrefillService(NullLogger<ScheduledPrefillService>.Instance,
                _services.GetRequiredService<IServiceScopeFactory>(), State);
            Controller = new ScheduledPrefillConfigController(State, (IServiceScheduleRegistry)Broadcast,
                (IUnifiedOperationTracker)Tracker, Runtime, NullLogger<ScheduledPrefillConfigController>.Instance);
        }

        public void Dispose()
        {
            Runtime.Dispose();
            _services.Dispose();
            Context.Dispose();
        }
    }

    private class BroadcastProxy : DispatchProxy
    {
        public int Calls;
        public bool Fail { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IServiceScheduleRegistry.BroadcastSchedulesAsync)) return null;
            Interlocked.Increment(ref Calls);
            return Fail ? Task.FromException(new IOException("Broadcast unavailable.")) : Task.CompletedTask;
        }
    }
}
