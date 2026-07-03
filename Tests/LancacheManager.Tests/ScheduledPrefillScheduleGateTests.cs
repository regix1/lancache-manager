using System.Reflection;
using LancacheManager.Core.Interfaces;
using LancacheManager.Core.Services;
using LancacheManager.Infrastructure.Services;
using LancacheManager.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the "Next run" countdown bug: <see cref="ScheduledPrefillService"/>'s outer poll loop runs
/// on a fixed 1-minute cadence regardless of whether any of its 5 per-service configs are enabled, so
/// its raw <c>ConfiguredInterval</c>/<c>NextRunUtc</c> always looked like a live, ticking schedule even
/// while the card showed "No services are enabled". <see cref="IScheduleEnabledGate"/> lets
/// <see cref="ServiceScheduleRegistry"/> report the schedule as paused (interval 0, no next-run) in
/// that case, reusing the same representation the Schedules UI already renders for any interval-0
/// service.
/// </summary>
public class ScheduledPrefillScheduleGateTests
{
    [Fact]
    public void HasAnyServiceEnabled_ReturnsTrue_WhenAtLeastOneServiceIsEnabled()
    {
        // ScheduledPrefillConfigFactory.CreateDefault() enables BattleNet + Riot by default.
        using var service = CreateService(ScheduledPrefillConfigFactory.CreateDefault());

        Assert.True(service.HasAnyServiceEnabled());
    }

    [Fact]
    public void HasAnyServiceEnabled_ReturnsFalse_WhenEveryServiceIsDisabled()
    {
        using var service = CreateService(AllDisabledConfig());

        Assert.False(service.HasAnyServiceEnabled());
    }

    [Fact]
    public void Registry_ReportsScheduledPrefillAsPaused_WhenEveryServiceIsDisabled()
    {
        using var service = CreateService(AllDisabledConfig());
        var registry = CreateRegistry(service);

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        // Paused representation: interval 0 (matches the UI's existing "disabled" branch for any
        // interval-0 schedule) and no next-run, instead of the outer 1-minute poll cadence.
        Assert.Equal(0d, info!.IntervalHours);
        Assert.Null(info.NextRunUtc);
    }

    [Fact]
    public void Registry_ReportsScheduledPrefillsOwnPollCadence_WhenAnyServiceIsEnabled()
    {
        using var service = CreateService(ScheduledPrefillConfigFactory.CreateDefault());
        var registry = CreateRegistry(service);

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        // Not the paused sentinel: the real (fixed 1-minute) outer poll cadence is reported.
        Assert.NotEqual(0d, info!.IntervalHours);
    }

    private static ScheduledPrefillConfigDto AllDisabledConfig()
    {
        var defaults = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = defaults.Version,
            MaxServiceRuntime = defaults.MaxServiceRuntime,
            StallTimeout = defaults.StallTimeout,
            Steam = Disabled(defaults.Steam),
            Epic = Disabled(defaults.Epic),
            Xbox = Disabled(defaults.Xbox),
            BattleNet = Disabled(defaults.BattleNet),
            Riot = Disabled(defaults.Riot)
        };
    }

    private static ScheduledPrefillServiceConfigDto Disabled(ScheduledPrefillServiceConfigDto service)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = false,
            IntervalHours = service.IntervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency
        };
    }

    private static ScheduledPrefillService CreateService(ScheduledPrefillConfigDto config)
    {
        var stateServiceProxy = DispatchProxy.Create<IStateService, FixedConfigStateServiceProxy>();
        ((FixedConfigStateServiceProxy)stateServiceProxy).Config = config;

        var services = new ServiceCollection();
        var provider = services.BuildServiceProvider();

        var logger = LoggerFactory.Create(_ => { }).CreateLogger<ScheduledPrefillService>();
        return new ScheduledPrefillService(
            logger,
            provider.GetRequiredService<IServiceScopeFactory>(),
            stateServiceProxy);
    }

    private static ServiceScheduleRegistry CreateRegistry(ScheduledPrefillService service)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();
        var stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();

        return new ServiceScheduleRegistry(new IHostedService[] { service }, stateService, notifications);
    }

    /// <summary>
    /// IStateService stub whose <see cref="IStateService.GetScheduledPrefillConfig"/> returns a fixed,
    /// caller-supplied config. Every other member falls back to its type default, mirroring the
    /// NullReturningProxy pattern used elsewhere in this test suite.
    /// </summary>
    private class FixedConfigStateServiceProxy : DispatchProxy
    {
        public ScheduledPrefillConfigDto? Config { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config;
            }

            var returnType = targetMethod?.ReturnType;

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType is not null && returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }

    /// <summary>Minimal do-nothing proxy for interfaces whose members are never exercised.</summary>
    private class NullReturningProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
            {
                throw new InvalidOperationException("Target method was null.");
            }

            var returnType = targetMethod.ReturnType;

            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsValueType && Nullable.GetUnderlyingType(returnType) is null)
            {
                return Activator.CreateInstance(returnType);
            }

            return null;
        }
    }
}
