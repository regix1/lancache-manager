using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the v1 -> v2 per-service scheduling migration and the new <c>IntervalHours</c> validation
/// in <see cref="ScheduledPrefillConfigFactory"/>. v1 had a single global cadence in
/// <c>StateService.ServiceIntervals["scheduledPrefill"]</c>; v2 gives every service its own interval,
/// seeded from that legacy global value.
/// </summary>
public class ScheduledPrefillConfigFactoryTests
{
    [Fact]
    public void CreateDefault_SeedsDefaultIntervalAndIsCurrentVersionAndValid()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        Assert.Equal(ScheduledPrefillConfigFactory.CurrentVersion, config.Version);
        foreach (var service in config.GetServicesInRunOrder())
        {
            Assert.Equal(ScheduledPrefillConfigFactory.DefaultIntervalHours, service.IntervalHours);
        }

        // Default config must always pass its own validation.
        ScheduledPrefillConfigFactory.Validate(config);
    }

    [Fact]
    public void Migrate_V1Config_SeedsEveryServiceIntervalFromLegacyGlobal_AndBumpsVersion()
    {
        var v1 = BuildV1Config();

        var migrated = ScheduledPrefillConfigFactory.Migrate(v1, legacyGlobalIntervalHours: 72d);

        Assert.Equal(ScheduledPrefillConfigFactory.CurrentVersion, migrated.Version);
        foreach (var service in migrated.GetServicesInRunOrder())
        {
            Assert.Equal(72d, service.IntervalHours);
        }

        // Migration must preserve every other per-service setting.
        Assert.True(migrated.Steam.Enabled);
        Assert.False(migrated.Epic.Enabled);
        Assert.True(migrated.BattleNet.Enabled);
        Assert.Equal(v1.MaxServiceRuntime, migrated.MaxServiceRuntime);
        Assert.Equal(v1.StallTimeout, migrated.StallTimeout);

        // The migrated config must pass validation (it is what gets persisted/served).
        ScheduledPrefillConfigFactory.Validate(migrated);
    }

    [Fact]
    public void Migrate_V1Config_FallsBackToDefaultInterval_WhenLegacyMissing()
    {
        var migrated = ScheduledPrefillConfigFactory.Migrate(BuildV1Config(), legacyGlobalIntervalHours: null);

        foreach (var service in migrated.GetServicesInRunOrder())
        {
            Assert.Equal(ScheduledPrefillConfigFactory.DefaultIntervalHours, service.IntervalHours);
        }
    }

    [Fact]
    public void Migrate_V1Config_FallsBackToDefaultInterval_WhenLegacyInvalid()
    {
        // An out-of-range legacy value must not produce an invalid (un-persistable) migrated config.
        var migrated = ScheduledPrefillConfigFactory.Migrate(BuildV1Config(), legacyGlobalIntervalHours: -5d);

        foreach (var service in migrated.GetServicesInRunOrder())
        {
            Assert.Equal(ScheduledPrefillConfigFactory.DefaultIntervalHours, service.IntervalHours);
        }

        ScheduledPrefillConfigFactory.Validate(migrated);
    }

    [Theory]
    [InlineData(0d)]   // legacy global was paused
    [InlineData(-1d)]  // legacy global was startup-only
    public void Migrate_V1Config_PreservesValidSpecialLegacyValues(double legacy)
    {
        var migrated = ScheduledPrefillConfigFactory.Migrate(BuildV1Config(), legacyGlobalIntervalHours: legacy);

        foreach (var service in migrated.GetServicesInRunOrder())
        {
            Assert.Equal(legacy, service.IntervalHours);
        }
    }

    [Fact]
    public void Migrate_CurrentVersionConfig_IsNoOp()
    {
        var current = ScheduledPrefillConfigFactory.CreateDefault();

        var migrated = ScheduledPrefillConfigFactory.Migrate(current, legacyGlobalIntervalHours: 72d);

        Assert.Same(current, migrated);
    }

    [Theory]
    [InlineData(-1d, true)]
    [InlineData(0d, true)]
    [InlineData(0.5d, true)]
    [InlineData(24d, true)]
    [InlineData(ScheduledPrefillConfigFactory.MaxIntervalHours, true)]
    [InlineData(-2d, false)]
    [InlineData(-0.5d, false)]
    [InlineData(ScheduledPrefillConfigFactory.MaxIntervalHours + 1d, false)]
    public void IsIntervalHoursValid_EnforcesConvention(double intervalHours, bool expected)
    {
        Assert.Equal(expected, ScheduledPrefillConfigFactory.IsIntervalHoursValid(intervalHours));
    }

    [Fact]
    public void Validate_RejectsServiceWithOutOfRangeIntervalHours()
    {
        var config = BuildV1Config();
        config = ScheduledPrefillConfigFactory.Migrate(config, legacyGlobalIntervalHours: null); // bring to current version
        var broken = WithSteamInterval(config, intervalHours: -2d);

        Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(broken));
    }

    [Fact]
    public void Validate_ReconcilesPresetNoLongerSupportedByService_InsteadOfThrowing()
    {
        // BattleNet is All-only; this simulates a config saved before per-service preset capability
        // gating existed (or written directly via the API) with a preset it can no longer back.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var stale = WithBattleNetPreset(config, ScheduledPrefillPreset.Top, topCount: 50);

        var validated = ScheduledPrefillConfigFactory.Validate(stale);

        Assert.Equal(ScheduledPrefillPreset.All, validated.BattleNet.Preset);
        Assert.Null(validated.BattleNet.TopCount);
    }

    [Fact]
    public void Validate_LeavesSupportedPresetUnchanged_AndReturnsSameInstance()
    {
        // Steam supports Top, so nothing should be reconciled and Validate should be a true no-op.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var withTop = WithSteamPreset(config, ScheduledPrefillPreset.Top, topCount: 50);

        var validated = ScheduledPrefillConfigFactory.Validate(withTop);

        Assert.Same(withTop, validated);
        Assert.Equal(ScheduledPrefillPreset.Top, validated.Steam.Preset);
        Assert.Equal(50, validated.Steam.TopCount);
    }

    // ---- Anonymous-service (BattleNet/Riot) allowed-games parity: ReconcileUnsupportedPresets
    // must not clear SelectedAppIds when it resets an unsupported preset. Both services are
    // All-only, so a stale Top/Recent preset gets reset to All here, but the explicit games list
    // (which overrides the preset at runtime regardless of preset value, per
    // ScheduledPrefillService.cs's SelectedAppIds override) must survive untouched. ----

    [Theory]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public void Validate_ReconcilesPresetNoLongerSupportedByAnonymousService_PreservesSelectedAppIds(PrefillPlatform service)
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var selectedAppIds = new List<string> { "wow", "d3" };
        var stale = WithAnonymousServicePresetAndSelectedApps(config, service, ScheduledPrefillPreset.Top, topCount: 50, selectedAppIds);

        var validated = ScheduledPrefillConfigFactory.Validate(stale);

        var reconciled = service == PrefillPlatform.BattleNet ? validated.BattleNet : validated.Riot;
        Assert.Equal(ScheduledPrefillPreset.All, reconciled.Preset);
        Assert.Null(reconciled.TopCount);
        Assert.Equal(selectedAppIds, reconciled.SelectedAppIds);
    }

    private static ScheduledPrefillConfigDto WithBattleNetPreset(
        ScheduledPrefillConfigDto config, ScheduledPrefillPreset preset, int? topCount)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.BattleNet.ServiceId,
                Enabled = config.BattleNet.Enabled,
                IntervalHours = config.BattleNet.IntervalHours,
                Preset = preset,
                TopCount = topCount,
                SelectedAppIds = config.BattleNet.SelectedAppIds,
                OperatingSystems = config.BattleNet.OperatingSystems,
                Force = config.BattleNet.Force,
                MaxConcurrency = config.BattleNet.MaxConcurrency
            },
            Riot = config.Riot
        };
    }

    private static ScheduledPrefillConfigDto WithAnonymousServicePresetAndSelectedApps(
        ScheduledPrefillConfigDto config,
        PrefillPlatform service,
        ScheduledPrefillPreset preset,
        int? topCount,
        List<string> selectedAppIds)
    {
        ScheduledPrefillServiceConfigDto Reconciled(ScheduledPrefillServiceConfigDto original) => new()
        {
            ServiceId = original.ServiceId,
            Enabled = original.Enabled,
            IntervalHours = original.IntervalHours,
            Preset = preset,
            TopCount = topCount,
            SelectedAppIds = selectedAppIds,
            OperatingSystems = original.OperatingSystems,
            Force = original.Force,
            MaxConcurrency = original.MaxConcurrency
        };

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = service == PrefillPlatform.BattleNet ? Reconciled(config.BattleNet) : config.BattleNet,
            Riot = service == PrefillPlatform.Riot ? Reconciled(config.Riot) : config.Riot
        };
    }

    private static ScheduledPrefillConfigDto WithSteamPreset(
        ScheduledPrefillConfigDto config, ScheduledPrefillPreset preset, int? topCount)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            Steam = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.Steam.ServiceId,
                Enabled = config.Steam.Enabled,
                IntervalHours = config.Steam.IntervalHours,
                Preset = preset,
                TopCount = topCount,
                SelectedAppIds = config.Steam.SelectedAppIds,
                OperatingSystems = config.Steam.OperatingSystems,
                Force = config.Steam.Force,
                MaxConcurrency = config.Steam.MaxConcurrency
            },
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };
    }

    private static ScheduledPrefillConfigDto BuildV1Config()
    {
        return new ScheduledPrefillConfigDto
        {
            Version = 1,
            MaxServiceRuntime = TimeSpan.FromHours(12),
            StallTimeout = TimeSpan.FromMinutes(30),
            Steam = Service(PrefillPlatform.Steam, enabled: true),
            Epic = Service(PrefillPlatform.Epic, enabled: false),
            Xbox = Service(PrefillPlatform.Xbox, enabled: false),
            BattleNet = Service(PrefillPlatform.BattleNet, enabled: true),
            Riot = Service(PrefillPlatform.Riot, enabled: true)
        };
    }

    private static ScheduledPrefillConfigDto WithSteamInterval(ScheduledPrefillConfigDto config, double intervalHours)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            Steam = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.Steam.ServiceId,
                Enabled = config.Steam.Enabled,
                IntervalHours = intervalHours,
                Preset = config.Steam.Preset,
                TopCount = config.Steam.TopCount,
                SelectedAppIds = config.Steam.SelectedAppIds,
                OperatingSystems = config.Steam.OperatingSystems,
                Force = config.Steam.Force,
                MaxConcurrency = config.Steam.MaxConcurrency
            },
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };
    }

    private static ScheduledPrefillServiceConfigDto Service(PrefillPlatform id, bool enabled)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = id,
            Enabled = enabled,
            // Left at the DTO default; the migration overrides it from the legacy global value.
            Preset = ScheduledPrefillPreset.All,
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
    }
}
