using System.Reflection;
using System.Text.Json;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Infrastructure.Services.Base;
using LancacheManager.Infrastructure.Utilities;
using LancacheManager.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LancacheManager.Tests;

/// <summary>
/// One global Normal/Compact default decides how every notification without a style of its own
/// renders. An install that predates the setting reads Compact, a schedule's own style overrides it
/// and can be removed again, scheduled prefill's per-schedule styles never follow it, and a change
/// reaches every client once.
/// </summary>
public sealed class GlobalNotificationDisplayModeTests : IDisposable
{
    private readonly string _root;
    private readonly List<IDisposable> _services = [];

    public GlobalNotificationDisplayModeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-global-display-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // Every install that predates the setting starts from a state.json with no key for it, and that
    // install is owed Compact, not whichever member happens to sort first.
    [Fact]
    public void AFreshStateAndAStateFileWithoutTheSettingReadCondensed()
    {
        Assert.Equal(NotificationDisplayMode.Condensed, new AppState().GlobalNotificationDisplayMode);

        var statePath = Path.Combine(_root, nameof(IPathResolver.GetStateDirectory), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, """{"SetupCompleted":true}""");
        var state = StateTestMethods.CreateStateService(_root);

        // The file was read, so the answer below comes from it rather than from a fresh state.
        Assert.True(state.GetState().SetupCompleted);
        Assert.Equal(NotificationDisplayMode.Condensed, state.GetGlobalNotificationDisplayMode());
    }

    [Fact]
    public void TheSettingSurvivesARestart()
    {
        StateTestMethods.CreateStateService(_root).SetGlobalNotificationDisplayMode(NotificationDisplayMode.Full);

        // A second instance over the same directory is the restarted container reading state.json.
        var reader = StateTestMethods.CreateStateService(_root);

        Assert.Equal(NotificationDisplayMode.Full, reader.GetGlobalNotificationDisplayMode());
    }

    [Fact]
    public void EveryScheduleWithoutItsOwnStyleFollowsTheGlobalDefault()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var schedules =CreateSchedules(state, CreateNotifications());

        Assert.All(schedules.GetAll(), schedule =>
        {
            Assert.Equal(NotificationDisplayMode.Condensed, schedule.NotificationDisplayMode);
            Assert.False(schedule.NotificationDisplayModeOverridden);
        });

        state.SetGlobalNotificationDisplayMode(NotificationDisplayMode.Full);
        schedules.SetNotificationDisplayMode("logRotation", NotificationDisplayMode.Condensed);

        Assert.All(schedules.GetAll(), schedule =>
        {
            var own = schedule.Key == "logRotation";
            Assert.Equal(own ? NotificationDisplayMode.Condensed : NotificationDisplayMode.Full, schedule.NotificationDisplayMode);
            Assert.Equal(own, schedule.NotificationDisplayModeOverridden);
        });

        // "Default" removes the schedule's own style, so it follows the global again.
        schedules.ClearNotificationDisplayMode("logRotation");
        var cleared = schedules.Get("logRotation")!;
        Assert.Equal(NotificationDisplayMode.Full, cleared.NotificationDisplayMode);
        Assert.False(cleared.NotificationDisplayModeOverridden);
    }

    // Scheduled prefill keeps each schedule's own saved style, which defaults to Full, whatever the
    // global default says.
    [Fact]
    public void ScheduledPrefillSchedulesKeepTheirOwnStyle()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var schedules =CreateSchedules(state, CreateNotifications());

        var prefill = schedules.Get("scheduledPrefill")!;

        Assert.Equal(NotificationDisplayMode.Condensed, prefill.NotificationDisplayMode);
        Assert.NotEmpty(prefill.PlatformNotificationDisplayModes!);
        Assert.All(prefill.PlatformNotificationDisplayModes!.Values, mode => Assert.Equal(NotificationDisplayMode.Full, mode));
    }

    [Fact]
    public async Task ChangingTheGlobalDefaultPushesItOnceAndSendsTheSchedulesOnce()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var notifications = CreateNotifications();
        var controller = new ScheduleController(CreateSchedules(state, notifications));

        Assert.IsType<NoContentResult>(await controller.SetGlobalNotificationDisplayModeAsync(NotificationDisplayMode.Full));

        Assert.Equal(NotificationDisplayMode.Full, state.GetGlobalNotificationDisplayMode());
        var pushed = Assert.Single(Sent(notifications, SignalREvents.NotificationDisplayModeChanged));
        Assert.Equal(new GlobalNotificationDisplayMode(NotificationDisplayMode.Full), pushed);
        var schedules = Assert.IsAssignableFrom<IReadOnlyList<ServiceScheduleInfo>>(
            Assert.Single(Sent(notifications, SignalREvents.SchedulesUpdated)));
        Assert.All(schedules, schedule => Assert.Equal(NotificationDisplayMode.Full, schedule.NotificationDisplayMode));
    }

    [Fact]
    public void TheGlobalDefaultReadsAsOneCamelCaseValue()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var controller = new ScheduleController(CreateSchedules(state, CreateNotifications()));

        var body = Assert.IsType<OkObjectResult>(controller.GetGlobalNotificationDisplayMode().Result).Value;

        Assert.Equal(
            """{"mode":"condensed"}""",
            JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public async Task ResetToDefaultsReturnsTheGlobalDefaultToCondensedAndPushesIt()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var notifications = CreateNotifications();
        var schedules =CreateSchedules(state, notifications);
        state.SetGlobalNotificationDisplayMode(NotificationDisplayMode.Full);
        schedules.SetNotificationDisplayMode("logRotation", NotificationDisplayMode.Full);

        Assert.IsType<OkResult>(await new ScheduleController(schedules).ResetToDefaultsAsync());

        Assert.Equal(NotificationDisplayMode.Condensed, state.GetGlobalNotificationDisplayMode());
        Assert.False(schedules.Get("logRotation")!.NotificationDisplayModeOverridden);
        Assert.Equal(
            new GlobalNotificationDisplayMode(NotificationDisplayMode.Condensed),
            Assert.Single(Sent(notifications, SignalREvents.NotificationDisplayModeChanged)));
        Assert.Single(Sent(notifications, SignalREvents.SchedulesUpdated));
    }

    [Fact]
    public async Task ClearingAnUnknownScheduleAnswersNotFound()
    {
        var state = StateTestMethods.CreateStateService(_root);
        var notifications = CreateNotifications();
        var controller = new ScheduleController(CreateSchedules(state, notifications));

        Assert.IsType<NotFoundObjectResult>(await controller.ClearNotificationDisplayModeAsync("does-not-exist"));
        Assert.Empty(Sent(notifications, SignalREvents.SchedulesUpdated));

        Assert.IsType<NoContentResult>(await controller.ClearNotificationDisplayModeAsync("logRotation"));
        Assert.Single(Sent(notifications, SignalREvents.SchedulesUpdated));
    }

    private static RecordingNotificationProxy CreateNotifications()
        => (RecordingNotificationProxy)(object)DispatchProxy
            .Create<ISignalRNotificationService, RecordingNotificationProxy>();

    private static List<object?> Sent(RecordingNotificationProxy notifications, string eventName)
        => notifications.Invocations
            .Where(call => call.Method == nameof(ISignalRNotificationService.NotifyAllAsync) && (string?)call.Args[0] == eventName)
            .Select(call => call.Args[1])
            .ToList();

    private ServiceScheduleRegistry CreateSchedules(StateService state, RecordingNotificationProxy notifications)
    {
        var scheduled = new ProbeScheduledService();
        var configurable = new ProbeConfigurableService();
        var prefill = new ScheduledPrefillService(
            NullLogger<ScheduledPrefillService>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            state);
        _services.AddRange([scheduled, configurable, prefill]);

        var tracker = new UnifiedOperationTracker(
            new ProcessManager(NullLogger<ProcessManager>.Instance), NullLogger<UnifiedOperationTracker>.Instance);
        var schedules =new ServiceScheduleRegistry(
            [scheduled, configurable, prefill], state, (ISignalRNotificationService)(object)notifications, tracker);

        // The registry re-sends the schedules on process-wide run events that other test classes raise
        // under these same keys. Detached, so every send counted here is one this test caused.
        var onRunStateChanged = (Action<string>)Delegate.CreateDelegate(typeof(Action<string>), schedules,
            typeof(ServiceScheduleRegistry).GetMethod("OnServiceExecutionStateChangedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!);
        ScheduledBackgroundService.ServiceExecutionStateChanged -= onRunStateChanged;
        ConfigurableScheduledService.ServiceExecutionStateChanged -= onRunStateChanged;
        return schedules;
    }

    private sealed class ProbeScheduledService : ScheduledBackgroundService
    {
        public ProbeScheduledService()
            : base(NullLogger<ProbeScheduledService>.Instance, new ConfigurationBuilder().Build())
        {
        }

        public override string ServiceKey => "logRotation";
        protected override string ServiceName => "logRotation";
        protected override TimeSpan Interval => TimeSpan.FromHours(1);
        public override bool DefaultRunOnStartup => false;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    private sealed class ProbeConfigurableService : ConfigurableScheduledService
    {
        public ProbeConfigurableService()
            : base(NullLogger<ProbeConfigurableService>.Instance, TimeSpan.FromHours(1))
        {
        }

        public string ScheduleServiceKey => "depotMapping";
        protected override string ServiceName => ScheduleServiceKey;

        protected override Task ExecuteWorkAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }
}
