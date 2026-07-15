using System.Text.Json;
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
            Assert.True(service.ShowNotification);
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

    [Fact]
    public void Migrate_PreservesSilentNotificationSetting()
    {
        var v1 = WithBattleNetPreset(
            BuildV1Config(),
            ScheduledPrefillPreset.All,
            topCount: null,
            showNotification: false);

        var migrated = ScheduledPrefillConfigFactory.Migrate(v1, legacyGlobalIntervalHours: 24d);

        Assert.False(migrated.BattleNet.ShowNotification);
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
    public void Validate_ReconcilesPresetWithoutLosingSilentNotificationSetting()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var stale = WithBattleNetPreset(
            config,
            ScheduledPrefillPreset.Top,
            topCount: 50,
            showNotification: false);

        var validated = ScheduledPrefillConfigFactory.Validate(stale);

        Assert.Equal(ScheduledPrefillPreset.All, validated.BattleNet.Preset);
        Assert.False(validated.BattleNet.ShowNotification);
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

    [Fact]
    public void Validate_ReconcilesOperatingSystemsNotSupportedByService_ToEmpty_InsteadOfThrowing()
    {
        // Epic has no platform concept at all; this simulates a config saved before per-service OS
        // capability gating existed (or written directly via the API) with an OS selection its
        // daemon cannot act on.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var stale = WithEpicOperatingSystems(config, new List<ScheduledPrefillOperatingSystem>
        {
            ScheduledPrefillOperatingSystem.Windows
        });

        var validated = ScheduledPrefillConfigFactory.Validate(stale);

        Assert.Empty(validated.Epic.OperatingSystems);
    }

    [Fact]
    public void Validate_LeavesSteamOperatingSystemsUnchanged_AndReturnsSameInstance()
    {
        // Steam supports all three OS values, so nothing should be reconciled and Validate should be
        // a true no-op for OperatingSystems.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var withAllOs = WithSteamOperatingSystems(config, new List<ScheduledPrefillOperatingSystem>
        {
            ScheduledPrefillOperatingSystem.Windows, ScheduledPrefillOperatingSystem.Linux, ScheduledPrefillOperatingSystem.Macos
        });

        var validated = ScheduledPrefillConfigFactory.Validate(withAllOs);

        Assert.Same(withAllOs, validated);
        Assert.Equal(withAllOs.Steam.OperatingSystems, validated.Steam.OperatingSystems);
    }

    [Fact]
    public void SupportsOperatingSystemSelection_MatchesVerdicts()
    {
        Assert.True(ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(PrefillPlatform.Steam));
        Assert.False(ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(PrefillPlatform.Epic));
        Assert.False(ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(PrefillPlatform.Xbox));
        Assert.False(ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(PrefillPlatform.BattleNet));
        Assert.False(ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(PrefillPlatform.Riot));
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

    // ---- PersistenceMode: wire format, v2->v3 migration, copy-site threading, validation, and
    // GetEffectivePersistenceMode. ----

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart, "\"killOnRestart\"")]
    [InlineData(PersistenceMode.KeepAcrossRestart, "\"keepAcrossRestart\"")]
    [InlineData(PersistenceMode.FullPersistence, "\"fullPersistence\"")]
    public void PersistenceMode_SerializesToCamelCaseWireValue_AndRoundTrips(PersistenceMode mode, string expectedJson)
    {
        var json = JsonSerializer.Serialize(mode);

        Assert.Equal(expectedJson, json);
        Assert.Equal(mode, JsonSerializer.Deserialize<PersistenceMode>(json));
    }

    [Fact]
    public void CreateDefault_SeedsKeepAcrossRestartGlobally_AndNullPerServiceOverrides()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        Assert.Equal(PersistenceMode.KeepAcrossRestart, config.PersistenceMode);
        foreach (var service in config.GetServicesInRunOrder())
        {
            Assert.Null(service.PersistenceMode);
        }

        // Must pass its own validation (exercises the CreateDefaultService copy site).
        ScheduledPrefillConfigFactory.Validate(config);
    }

    [Fact]
    public void Migrate_V2Config_SeedsGlobalPersistenceModeAndLeavesPerServiceOverridesNull_PreservingEveryOtherField()
    {
        var v2 = BuildV2Config();

        var migrated = ScheduledPrefillConfigFactory.Migrate(v2, legacyGlobalIntervalHours: 999d);

        Assert.Equal(3, migrated.Version);
        Assert.Equal(PersistenceMode.KeepAcrossRestart, migrated.PersistenceMode);
        Assert.Equal(v2.MaxServiceRuntime, migrated.MaxServiceRuntime);
        Assert.Equal(v2.StallTimeout, migrated.StallTimeout);

        AssertServicePreservedExceptPersistenceMode(v2.Steam, migrated.Steam);
        AssertServicePreservedExceptPersistenceMode(v2.Epic, migrated.Epic);
        AssertServicePreservedExceptPersistenceMode(v2.Xbox, migrated.Xbox);
        AssertServicePreservedExceptPersistenceMode(v2.BattleNet, migrated.BattleNet);
        AssertServicePreservedExceptPersistenceMode(v2.Riot, migrated.Riot);

        // The migrated config is what gets persisted/served - it must pass validation.
        ScheduledPrefillConfigFactory.Validate(migrated);
    }

    [Fact]
    public void Migrate_ThreadsPerServicePersistenceModeOverrideThrough_WithIntervalCopySite()
    {
        // BuildV1Config is Version 1, so Migrate's v1->v2 stage (the only caller of the private
        // WithInterval copy site) is what rebuilds this service - proving it threads the field.
        var v1 = WithServicePersistenceMode(BuildV1Config(), PrefillPlatform.Steam, PersistenceMode.FullPersistence);

        var migrated = ScheduledPrefillConfigFactory.Migrate(v1, legacyGlobalIntervalHours: 6d);

        Assert.Equal(PersistenceMode.FullPersistence, migrated.Steam.PersistenceMode);
        ScheduledPrefillConfigFactory.Validate(migrated);
    }

    [Fact]
    public void Validate_ThreadsPerServicePersistenceModeOverrideThrough_ReconcilePresetAndOperatingSystemsCopySites()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        // BattleNet: force an unsupported preset so the private ReconcileServicePreset copy site
        // rebuilds it. Epic: force an unsupported OS selection so the private
        // ReconcileServiceOperatingSystems copy site rebuilds it. Both get a distinct override so a
        // dropped field can't hide behind a coincidentally-matching default.
        var stale = WithBattleNetPreset(config, ScheduledPrefillPreset.Top, topCount: 50);
        stale = WithEpicOperatingSystems(stale, new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows });
        stale = WithServicePersistenceMode(stale, PrefillPlatform.BattleNet, PersistenceMode.FullPersistence);
        stale = WithServicePersistenceMode(stale, PrefillPlatform.Epic, PersistenceMode.KillOnRestart);

        var validated = ScheduledPrefillConfigFactory.Validate(stale);

        // Sanity: both services actually went through their respective reconcile rebuild.
        Assert.Equal(ScheduledPrefillPreset.All, validated.BattleNet.Preset);
        Assert.Empty(validated.Epic.OperatingSystems);

        // The override must have survived the rebuild instead of silently resetting to null.
        Assert.Equal(PersistenceMode.FullPersistence, validated.BattleNet.PersistenceMode);
        Assert.Equal(PersistenceMode.KillOnRestart, validated.Epic.PersistenceMode);
    }

    [Fact]
    public void Validate_ThrowsWhenGlobalPersistenceModeIsNull()
    {
        var config = WithGlobalPersistenceMode(ScheduledPrefillConfigFactory.CreateDefault(), mode: null);

        Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));
    }

    [Fact]
    public void Validate_RejectsUndefinedGlobalPersistenceModeValue()
    {
        var config = WithGlobalPersistenceMode(ScheduledPrefillConfigFactory.CreateDefault(), (PersistenceMode)999);

        Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));
    }

    [Fact]
    public void Validate_RejectsUndefinedPerServicePersistenceModeOverride()
    {
        var config = WithServicePersistenceMode(ScheduledPrefillConfigFactory.CreateDefault(), PrefillPlatform.Steam, (PersistenceMode)999);

        Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));
    }

    [Fact]
    public void Validate_AcceptsNullPerServiceOverride_AsInheritGlobal()
    {
        var validated = ScheduledPrefillConfigFactory.Validate(ScheduledPrefillConfigFactory.CreateDefault());

        Assert.Null(validated.Steam.PersistenceMode);
    }

    [Theory]
    [InlineData(PersistenceMode.KillOnRestart)]
    [InlineData(PersistenceMode.KeepAcrossRestart)]
    [InlineData(PersistenceMode.FullPersistence)]
    public void Validate_AcceptsValidPerServicePersistenceModeOverride(PersistenceMode mode)
    {
        var config = WithServicePersistenceMode(ScheduledPrefillConfigFactory.CreateDefault(), PrefillPlatform.Steam, mode);

        var validated = ScheduledPrefillConfigFactory.Validate(config);

        Assert.Equal(mode, validated.Steam.PersistenceMode);
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public void GetEffectivePersistenceMode_ReturnsGlobal_WhenServiceHasNoOverride(PrefillPlatform serviceId)
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        Assert.Equal(PersistenceMode.KeepAcrossRestart, config.GetEffectivePersistenceMode(serviceId));
    }

    [Theory]
    [InlineData(PrefillPlatform.Steam)]
    [InlineData(PrefillPlatform.Epic)]
    [InlineData(PrefillPlatform.Xbox)]
    [InlineData(PrefillPlatform.BattleNet)]
    [InlineData(PrefillPlatform.Riot)]
    public void GetEffectivePersistenceMode_ReturnsOverride_WhenServiceHasOne(PrefillPlatform serviceId)
    {
        var config = WithServicePersistenceMode(ScheduledPrefillConfigFactory.CreateDefault(), serviceId, PersistenceMode.KillOnRestart);

        Assert.Equal(PersistenceMode.KillOnRestart, config.GetEffectivePersistenceMode(serviceId));
    }

    private static ScheduledPrefillConfigDto WithBattleNetPreset(
        ScheduledPrefillConfigDto config,
        ScheduledPrefillPreset preset,
        int? topCount,
        bool? showNotification = null)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.BattleNet.ServiceId,
                Enabled = config.BattleNet.Enabled,
                ShowNotification = showNotification ?? config.BattleNet.ShowNotification,
                IntervalHours = config.BattleNet.IntervalHours,
                Preset = preset,
                TopCount = topCount,
                SelectedAppIds = config.BattleNet.SelectedAppIds,
                OperatingSystems = config.BattleNet.OperatingSystems,
                Force = config.BattleNet.Force,
                MaxConcurrency = config.BattleNet.MaxConcurrency,
                PersistenceMode = config.BattleNet.PersistenceMode
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
            MaxConcurrency = original.MaxConcurrency,
            PersistenceMode = original.PersistenceMode
        };

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = service == PrefillPlatform.BattleNet ? Reconciled(config.BattleNet) : config.BattleNet,
            Riot = service == PrefillPlatform.Riot ? Reconciled(config.Riot) : config.Riot
        };
    }

    private static ScheduledPrefillConfigDto WithEpicOperatingSystems(
        ScheduledPrefillConfigDto config, List<ScheduledPrefillOperatingSystem> operatingSystems)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.Epic.ServiceId,
                Enabled = config.Epic.Enabled,
                IntervalHours = config.Epic.IntervalHours,
                Preset = config.Epic.Preset,
                TopCount = config.Epic.TopCount,
                SelectedAppIds = config.Epic.SelectedAppIds,
                OperatingSystems = operatingSystems,
                Force = config.Epic.Force,
                MaxConcurrency = config.Epic.MaxConcurrency,
                PersistenceMode = config.Epic.PersistenceMode
            },
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };
    }

    private static ScheduledPrefillConfigDto WithSteamOperatingSystems(
        ScheduledPrefillConfigDto config, List<ScheduledPrefillOperatingSystem> operatingSystems)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = new ScheduledPrefillServiceConfigDto
            {
                ServiceId = config.Steam.ServiceId,
                Enabled = config.Steam.Enabled,
                IntervalHours = config.Steam.IntervalHours,
                Preset = config.Steam.Preset,
                TopCount = config.Steam.TopCount,
                SelectedAppIds = config.Steam.SelectedAppIds,
                OperatingSystems = operatingSystems,
                Force = config.Steam.Force,
                MaxConcurrency = config.Steam.MaxConcurrency,
                PersistenceMode = config.Steam.PersistenceMode
            },
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
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
            PersistenceMode = config.PersistenceMode,
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
                MaxConcurrency = config.Steam.MaxConcurrency,
                PersistenceMode = config.Steam.PersistenceMode
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
            PersistenceMode = config.PersistenceMode,
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
                MaxConcurrency = config.Steam.MaxConcurrency,
                PersistenceMode = config.Steam.PersistenceMode
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

    /// <summary>
    /// Returns a copy of <paramref name="config"/> with only <paramref name="serviceId"/>'s
    /// per-service <c>PersistenceMode</c> override replaced (<c>null</c> = inherit global), every
    /// other field on every service - and the global <c>PersistenceMode</c> itself - preserved.
    /// General-purpose test helper covering all 5 services, used to exercise the copy-site
    /// threading and validation/effective-mode tests without a bespoke per-service variant.
    /// </summary>
    private static ScheduledPrefillConfigDto WithServicePersistenceMode(
        ScheduledPrefillConfigDto config, PrefillPlatform serviceId, PersistenceMode? mode)
    {
        ScheduledPrefillServiceConfigDto WithMode(ScheduledPrefillServiceConfigDto service) => new()
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
            ShowNotification = service.ShowNotification,
            IntervalHours = service.IntervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = mode
        };

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = serviceId == PrefillPlatform.Steam ? WithMode(config.Steam) : config.Steam,
            Epic = serviceId == PrefillPlatform.Epic ? WithMode(config.Epic) : config.Epic,
            Xbox = serviceId == PrefillPlatform.Xbox ? WithMode(config.Xbox) : config.Xbox,
            BattleNet = serviceId == PrefillPlatform.BattleNet ? WithMode(config.BattleNet) : config.BattleNet,
            Riot = serviceId == PrefillPlatform.Riot ? WithMode(config.Riot) : config.Riot
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="config"/> with only the global <c>PersistenceMode</c>
    /// replaced; every service (including each service's own override) is preserved untouched.
    /// </summary>
    private static ScheduledPrefillConfigDto WithGlobalPersistenceMode(ScheduledPrefillConfigDto config, PersistenceMode? mode)
    {
        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = mode,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };
    }

    /// <summary>
    /// Builds a realistic Version-2 config (post-IntervalHours-migration, pre-PersistenceMode)
    /// with distinguishable, non-default values on every field so the v2-&gt;v3 migration test can
    /// prove each field survives untouched. <c>PersistenceMode</c> is intentionally left unset
    /// (null) on the root and every service, exactly like a real pre-v3 state.json.
    /// </summary>
    private static ScheduledPrefillConfigDto BuildV2Config()
    {
        return new ScheduledPrefillConfigDto
        {
            Version = 2,
            MaxServiceRuntime = TimeSpan.FromHours(6),
            StallTimeout = TimeSpan.FromMinutes(15),
            Steam = ServiceV2(
                PrefillPlatform.Steam, enabled: true, intervalHours: 12d, preset: ScheduledPrefillPreset.Top,
                topCount: 25, selectedAppIds: new List<string> { "730" }, force: true, showNotification: false),
            Epic = ServiceV2(
                PrefillPlatform.Epic, enabled: false, intervalHours: 48d, preset: ScheduledPrefillPreset.All,
                topCount: null, selectedAppIds: new List<string>(), force: false, showNotification: true),
            Xbox = ServiceV2(
                PrefillPlatform.Xbox, enabled: true, intervalHours: -1d, preset: ScheduledPrefillPreset.Recent,
                topCount: null, selectedAppIds: new List<string>(), force: false, showNotification: true),
            BattleNet = ServiceV2(
                PrefillPlatform.BattleNet, enabled: true, intervalHours: 0d, preset: ScheduledPrefillPreset.All,
                topCount: null, selectedAppIds: new List<string> { "wow" }, force: false, showNotification: true),
            Riot = ServiceV2(
                PrefillPlatform.Riot, enabled: true, intervalHours: 24d, preset: ScheduledPrefillPreset.All,
                topCount: null, selectedAppIds: new List<string>(), force: false, showNotification: true)
        };
    }

    private static ScheduledPrefillServiceConfigDto ServiceV2(
        PrefillPlatform id,
        bool enabled,
        double intervalHours,
        ScheduledPrefillPreset preset,
        int? topCount,
        List<string> selectedAppIds,
        bool force,
        bool showNotification)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = id,
            Enabled = enabled,
            ShowNotification = showNotification,
            IntervalHours = intervalHours,
            Preset = preset,
            TopCount = topCount,
            SelectedAppIds = selectedAppIds,
            OperatingSystems = ScheduledPrefillConfigFactory.SupportsOperatingSystemSelection(id)
                ? new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows, ScheduledPrefillOperatingSystem.Linux }
                : new List<ScheduledPrefillOperatingSystem>(),
            Force = force,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Fixed, Value = 4 }
            // PersistenceMode intentionally omitted (null) - simulates a real pre-v3 config.
        };
    }

    /// <summary>
    /// Asserts every field that a v2-&gt;v3 migration must preserve unchanged, and that the new
    /// per-service <c>PersistenceMode</c> override was seeded null (inherit global) rather than
    /// some other default.
    /// </summary>
    private static void AssertServicePreservedExceptPersistenceMode(
        ScheduledPrefillServiceConfigDto original, ScheduledPrefillServiceConfigDto migrated)
    {
        Assert.Null(migrated.PersistenceMode);
        Assert.Equal(original.ServiceId, migrated.ServiceId);
        Assert.Equal(original.Enabled, migrated.Enabled);
        Assert.Equal(original.ShowNotification, migrated.ShowNotification);
        Assert.Equal(original.IntervalHours, migrated.IntervalHours);
        Assert.Equal(original.Preset, migrated.Preset);
        Assert.Equal(original.TopCount, migrated.TopCount);
        Assert.Equal(original.SelectedAppIds, migrated.SelectedAppIds);
        Assert.Equal(original.OperatingSystems, migrated.OperatingSystems);
        Assert.Equal(original.Force, migrated.Force);
        Assert.Equal(original.MaxConcurrency.Mode, migrated.MaxConcurrency.Mode);
        Assert.Equal(original.MaxConcurrency.Value, migrated.MaxConcurrency.Value);
    }
}
