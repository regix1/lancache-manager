using System.Reflection;
using LancacheManager.Controllers;
using LancacheManager.Core.Interfaces;
using LancacheManager.Hubs;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using LancacheManager.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static LancacheManager.Core.Services.UserPreferencesService;
using static LancacheManager.Tests.StateTestMethods;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the clock an admin picks for guests. The three fields hold one choice between them, so they are
/// written and announced together: sent one at a time they cross each other, and a guest session created
/// between two of them copies a clock nobody chose.
/// </summary>
public sealed class DefaultGuestPreferencesTests : IDisposable
{
    private readonly string _root;

    public DefaultGuestPreferencesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lm-default-guest-clock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // D-1: one request carries the whole clock, and all three fields land. [3]
    [Fact]
    public async Task ClockRequest_StoresAllThreeFields()
    {
        var stateService = CreateStateService(_root);
        var (controller, _) = CreateController(stateService);

        var result = await controller.SetDefaultGuestClockAsync(new ClockPreferences
        {
            UseUtcTimezone = false,
            UseLocalTimezone = true,
            Use24HourFormat = false
        });

        Assert.IsType<OkObjectResult>(result);

        var state = stateService.GetState();
        Assert.False(state.DefaultGuestUseUtcTimezone);
        Assert.True(state.DefaultGuestUseLocalTimezone);
        Assert.False(state.DefaultGuestUse24HourFormat);
    }

    // D-2: UTC is a clock of its own, so choosing it settles the other two rather than leaving whatever was
    // there. Nothing an admin can send may leave the three describing no option at all. [3]
    [Fact]
    public async Task ChoosingUtc_SettlesTheOtherTwoBeforeAnythingIsStored()
    {
        var stateService = CreateStateService(_root);
        stateService.UpdateState(state =>
        {
            state.DefaultGuestUseLocalTimezone = true;
            state.DefaultGuestUse24HourFormat = false;
        });

        var (controller, _) = CreateController(stateService);

        await controller.SetDefaultGuestClockAsync(new ClockPreferences
        {
            UseUtcTimezone = true,
            UseLocalTimezone = true,
            Use24HourFormat = false
        });

        var state = stateService.GetState();
        Assert.True(state.DefaultGuestUseUtcTimezone);
        Assert.False(state.DefaultGuestUseLocalTimezone);
        Assert.True(state.DefaultGuestUse24HourFormat);
    }

    // D-3: one change, one announcement, carrying the clock that was replaced as well as the new one. A
    // listener holding a guest needs both to decide whether that guest still followed the default, and
    // three announcements is what made the answer depend on which listener ran first. [3]
    [Fact]
    public async Task ClockChange_AnnouncesItselfOnceWithBothClocks()
    {
        var stateService = CreateStateService(_root);
        stateService.UpdateState(state =>
        {
            state.DefaultGuestUseUtcTimezone = false;
            state.DefaultGuestUseLocalTimezone = true;
            state.DefaultGuestUse24HourFormat = false;
        });

        var (controller, notifications) = CreateController(stateService);

        await controller.SetDefaultGuestClockAsync(new ClockPreferences { UseUtcTimezone = true });

        var announced = Assert.Single(notifications.Events);
        Assert.Equal(SignalREvents.DefaultGuestPreferencesChanged, announced.EventName);

        Assert.Equal("clock", ReadPayload<string>(announced.Payload, "key"));

        var clock = ReadPayload<ClockPreferences>(announced.Payload, "clock");
        Assert.True(clock.UseUtcTimezone);
        Assert.False(clock.UseLocalTimezone);
        Assert.True(clock.Use24HourFormat);

        var previousClock = ReadPayload<ClockPreferences>(announced.Payload, "previousClock");
        Assert.False(previousClock.UseUtcTimezone);
        Assert.True(previousClock.UseLocalTimezone);
        Assert.False(previousClock.Use24HourFormat);
    }

    // D-4: the single-field route can only ever carry one of the three, which is how a half-applied clock
    // became visible and durable. Two admins changing the clock at once cannot reach it any more. [3]
    [Theory]
    [InlineData("useUtcTimezone")]
    [InlineData("useLocalTimezone")]
    [InlineData("use24HourFormat")]
    public async Task SingleFieldRoute_NoLongerAcceptsAClockField(string key)
    {
        var (controller, _) = CreateController(CreateStateService(_root));

        var result = await controller.SetDefaultGuestPreferenceAsync(
            key, new SetBoolPreferenceRequest { Value = true });

        Assert.IsType<BadRequestObjectResult>(result);
    }

    // D-5: the fields that genuinely stand alone still go through the single-field route unchanged.
    [Theory]
    [InlineData("sharpCorners")]
    [InlineData("disableTooltips")]
    [InlineData("showDatasourceLabels")]
    public async Task SingleFieldRoute_StillAcceptsTheStandaloneFields(string key)
    {
        var (controller, notifications) = CreateController(CreateStateService(_root));

        var result = await controller.SetDefaultGuestPreferenceAsync(
            key, new SetBoolPreferenceRequest { Value = true });

        Assert.IsType<OkObjectResult>(result);
        var announced = Assert.Single(notifications.Events);
        Assert.Equal(key, ReadPayload<string>(announced.Payload, "key"));
    }

    private static T ReadPayload<T>(object? payload, string name)
    {
        Assert.NotNull(payload);
        var property = payload!.GetType().GetProperty(name)
            ?? throw new InvalidOperationException($"The announced payload carries no {name}.");
        return Assert.IsType<T>(property.GetValue(payload));
    }

    private static (SystemController Controller, RecordingNotifications Notifications) CreateController(
        StateService stateService)
    {
        var notifications = DispatchProxy.Create<ISignalRNotificationService, RecordingNotifications>();
        var controller = new SystemController(
            stateService,
            new ConfigurationBuilder().Build(),
            NullLogger<SystemController>.Instance,
            pathResolver: null!,
            cacheClearingService: null!,
            steamKit2Service: null!,
            datasourceService: null!,
            notifications,
            userPreferencesService: null!,
            capabilityService: null!,
            nginxLogRotationService: null!,
            cacheManagementService: null!,
            dbContextFactory: null!);

        return (controller, (RecordingNotifications)(object)notifications);
    }

    /// <summary>
    /// Records the broadcasts the controller sends. Built as a proxy rather than an implementation because
    /// the notification interface carries a member per hub and none of the rest is exercised here.
    /// </summary>
}
