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
/// service. When services ARE enabled, the registry surfaces the real per-service schedule (the soonest
/// per-service next-run and the most recent per-service last-run) instead of that outer poll cadence.
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
        var config = AllDisabledConfig();
        using var service = CreateService(config);

        // C2 fix: even with every service disabled the outer "Last run" must be the most recent REAL
        // per-service run (MAX over all services), never the 1-minute poll stamp that
        // ConfigurableScheduledService re-writes on every tick (which reads "just now" forever).
        var steamLastRun = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var epicLastRun = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc); // most recent
        var lastRuns = new Dictionary<string, DateTime>
        {
            [PrefillPlatform.Steam.ToString()] = steamLastRun,
            [PrefillPlatform.Epic.ToString()] = epicLastRun
        };

        var registry = CreateRegistry(service, config, lastRuns);

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        // Paused representation: interval 0 (matches the UI's existing "disabled" branch for any
        // interval-0 schedule) and no next-run, instead of the outer 1-minute poll cadence.
        Assert.Equal(0d, info!.IntervalHours);
        Assert.Null(info.NextRunUtc);
        // C2: last-run = per-service MAX, NOT the poll stamp.
        Assert.Equal(epicLastRun, info.LastRunUtc);
    }

    [Fact]
    public void Registry_ReportsNullLastRun_WhenPausedAndNoServiceEverRan()
    {
        var config = AllDisabledConfig();
        using var service = CreateService(config);

        // C2 fix: with no per-service last-run recorded at all, the per-service MAX is null, so the card
        // shows "Never run" instead of the poll stamp's "just now".
        var registry = CreateRegistry(service, config, new Dictionary<string, DateTime>());

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        Assert.Equal(0d, info!.IntervalHours);
        Assert.Null(info.LastRunUtc);
    }

    [Fact]
    public void Registry_ReportsStartupOnly_WhenOnlyEnabledServiceIsStartupOnly()
    {
        // C1 regression fix: a startup-only (-1) service is still enabled, so Run Now must stay usable.
        // Trace: only Xbox enabled, interval -1. Every enabled service is startup-only so no per-service
        // next-run exists (ComputeNextRunUtc(-1, _) == null). The outer card must NOT report interval 0
        // here: interval 0 dims the card AND disables Run Now (SchedulesSection isDimmed :190 /
        // disabled={isDisabled || isDimmed} :313). -1 keeps Run Now enabled and renders the honest
        // "Startup only" label (CountdownDisplay :48) with no fabricated countdown.
        var config = OnlyXboxStartupOnlyConfig();
        using var service = CreateService(config);
        var registry = CreateRegistry(service, config, new Dictionary<string, DateTime>());

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        // Run Now stays enabled: the outer card is NOT the interval-0 paused sentinel.
        Assert.NotEqual(0d, info!.IntervalHours);
        // Honest label for an enabled-but-non-recurring schedule: startup-only.
        Assert.Equal(-1d, info.IntervalHours);
        // No enabled service has a recurring next-run, so no countdown is invented.
        Assert.Null(info.NextRunUtc);
        // Nothing has run yet -> "Never run", never the poll stamp.
        Assert.Null(info.LastRunUtc);
    }

    [Fact]
    public void Registry_ReportsPerServiceSchedule_WhenServicesAreEnabledAndAnchored()
    {
        // Bug #3 fix: when services are enabled the outer card must surface the real per-service schedule,
        // not the fixed 1-minute poll cadence (which re-stamped "last run: just now / next run: ~1 min"
        // every tick). Default config enables BattleNet + Riot @ 24h; anchor both to known past times so
        // each has a real next-run.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        using var service = CreateService(config);

        var battleNetLastRun = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var riotLastRun = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc); // more recent
        var lastRuns = new Dictionary<string, DateTime>
        {
            [PrefillPlatform.BattleNet.ToString()] = battleNetLastRun,
            [PrefillPlatform.Riot.ToString()] = riotLastRun
        };

        var registry = CreateRegistry(service, config, lastRuns);

        var info = registry.Get("scheduledPrefill");

        Assert.NotNull(info);
        // Outer next-run = the SOONEST per-service next-run = min(battleNet+24h, riot+24h) = battleNet+24h.
        Assert.Equal(battleNetLastRun.AddHours(24d), info!.NextRunUtc);
        // Outer last-run = the MOST RECENT per-service last-run.
        Assert.Equal(riotLastRun, info.LastRunUtc);
        // Not the paused sentinel: a real recurring schedule still reports a non-zero interval.
        Assert.NotEqual(0d, info.IntervalHours);
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

    // Enabled (master toggle on) but startup-only (interval -1): "run once per process, never recurring",
    // so ComputeNextRunUtc(-1, _) is always null. This is the C1 scenario - reachable via the per-service
    // interval picker's "Startup only" option.
    private static ScheduledPrefillServiceConfigDto StartupOnly(ScheduledPrefillServiceConfigDto service)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = true,
            IntervalHours = -1d,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency
        };
    }

    private static ScheduledPrefillConfigDto OnlyXboxStartupOnlyConfig()
    {
        var defaults = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = defaults.Version,
            MaxServiceRuntime = defaults.MaxServiceRuntime,
            StallTimeout = defaults.StallTimeout,
            Steam = Disabled(defaults.Steam),
            Epic = Disabled(defaults.Epic),
            Xbox = StartupOnly(defaults.Xbox),
            BattleNet = Disabled(defaults.BattleNet),
            Riot = Disabled(defaults.Riot)
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

    private static ServiceScheduleRegistry CreateRegistry(
        ScheduledPrefillService service,
        ScheduledPrefillConfigDto? registryConfig = null,
        Dictionary<string, DateTime>? lastRuns = null)
    {
        var notifications = (ISignalRNotificationService)DispatchProxy.Create<ISignalRNotificationService, NullReturningProxy>();

        // Every outer-card mapping (paused, enabled-non-recurring, enabled-recurring) now reads the config
        // + per-service last-run map to compute the per-service MAX last-run and the soonest next-run, so
        // registry tests supply a config-returning stub. The bare NullReturningProxy path remains only for a
        // caller that exercises neither.
        IStateService stateService;
        if (registryConfig is null && lastRuns is null)
        {
            stateService = (IStateService)DispatchProxy.Create<IStateService, NullReturningProxy>();
        }
        else
        {
            var proxy = DispatchProxy.Create<IStateService, FixedConfigStateServiceProxy>();
            var typedProxy = (FixedConfigStateServiceProxy)proxy;
            typedProxy.Config = registryConfig;
            typedProxy.LastRuns = lastRuns ?? new Dictionary<string, DateTime>();
            stateService = (IStateService)proxy;
        }

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
        public Dictionary<string, DateTime> LastRuns { get; set; } = new();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillConfig))
            {
                return Config;
            }

            if (targetMethod?.Name == nameof(IStateService.GetScheduledPrefillServiceLastRun))
            {
                return args?[0] is string key && LastRuns.TryGetValue(key, out var lastRun)
                    ? lastRun
                    : (DateTime?)null;
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
