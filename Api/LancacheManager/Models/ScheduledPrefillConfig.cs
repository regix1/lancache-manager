using System.Text.Json;
using System.Text.Json.Serialization;

namespace LancacheManager.Models;

/// <summary>
/// Per-service prefill preset chosen for a scheduled run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduledPrefillPreset>))]
public enum ScheduledPrefillPreset
{
    All,
    Recent,
    Top
}

/// <summary>
/// Target operating system filter for a scheduled prefill run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduledPrefillOperatingSystem>))]
public enum ScheduledPrefillOperatingSystem
{
    Windows,
    Linux,
    Macos
}

/// <summary>
/// Concurrency selection mode for a scheduled prefill run.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScheduledPrefillMaxConcurrencyMode>))]
public enum ScheduledPrefillMaxConcurrencyMode
{
    Auto,
    Fixed
}

/// <summary>
/// Controls what happens to a persistent prefill container and its saved login across a
/// manager restart. Serialized as camelCase strings on the wire ("killOnRestart",
/// "keepAcrossRestart", "fullPersistence") to match the frontend's JSON contract - see
/// <see cref="PersistenceModeJsonConverter"/> for why a dedicated converter (not the bare
/// <c>JsonStringEnumConverter&lt;TEnum&gt;</c> attribute used elsewhere in this file) is
/// required to actually get camelCase here.
/// </summary>
[JsonConverter(typeof(PersistenceModeJsonConverter))]
public enum PersistenceMode
{
    /// <summary>
    /// Today's behavior: on manager shutdown the container is stopped, its login is erased,
    /// and the container is removed.
    /// </summary>
    KillOnRestart,

    /// <summary>
    /// Default for new and migrated installs. On manager shutdown the container is left
    /// running with its login intact and is re-adopted on the next start. A container that
    /// died while the manager was down still gets today's stopped-container cleanup and a
    /// fresh login on its next create.
    /// </summary>
    KeepAcrossRestart,

    /// <summary>
    /// Like <see cref="KeepAcrossRestart"/>, but a container that died while the manager was
    /// down is also recreated automatically, and its saved login is preserved (the fresh-login
    /// guard is skipped) so the daemon self-authenticates from its named volume.
    /// </summary>
    FullPersistence
}

/// <summary>
/// Serializes <see cref="PersistenceMode"/> as camelCase strings ("killOnRestart",
/// "keepAcrossRestart", "fullPersistence"). The bare <c>JsonStringEnumConverter&lt;TEnum&gt;</c>
/// attribute (used elsewhere in this file for <see cref="ScheduledPrefillPreset"/> etc.) ignores
/// the globally-configured <c>PropertyNamingPolicy</c> and emits PascalCase member names instead
/// (confirmed by <c>ScheduledPrefillPreset</c>'s own frontend mirror, which is
/// <c>'All' | 'Recent' | 'Top'</c> - PascalCase, not camelCase); the identical gotcha is
/// documented on <see cref="SessionType"/>, which uses this same dedicated-converter pattern.
/// </summary>
internal sealed class PersistenceModeJsonConverter : JsonStringEnumConverter<PersistenceMode>
{
    public PersistenceModeJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// Strongly typed connection-concurrency setting. <see cref="Value"/> is only meaningful
/// (and required) when <see cref="Mode"/> is <see cref="ScheduledPrefillMaxConcurrencyMode.Fixed"/>.
/// </summary>
public sealed class ScheduledPrefillMaxConcurrencyDto
{
    public required ScheduledPrefillMaxConcurrencyMode Mode { get; init; }
    public int? Value { get; init; }
}

/// <summary>
/// Per-service scheduled prefill configuration. One instance exists per supported
/// <see cref="PrefillPlatform"/> on the parent <see cref="ScheduledPrefillConfigDto"/>.
/// </summary>
public sealed class ScheduledPrefillServiceConfigDto
{
    public required PrefillPlatform ServiceId { get; init; }
    public required bool Enabled { get; init; }

    /// <summary>
    /// Legacy visible/silent toggle. Kept only so pre-v4 saved configs still deserialize cleanly;
    /// new code MUST NOT branch on this value - use <see cref="NotificationMode"/> instead.
    /// <see cref="ScheduledPrefillConfigFactory.Migrate"/> performs a one-time migration that maps
    /// this into <see cref="NotificationMode"/> (true/absent -> All, false -> Silent).
    /// </summary>
    [Obsolete("Replaced by NotificationMode. Kept for legacy JSON deserialization.")]
    public bool ShowNotification { get; init; } = true;

    /// <summary>
    /// Whether this service's run progress is shown in the universal notification bar, and for
    /// which triggers. Null means "use the per-service default" (All) - only possible on a config
    /// that predates this field and has not yet been through <see cref="ScheduledPrefillConfigFactory.Migrate"/>.
    /// </summary>
    public NotificationMode? NotificationMode { get; init; }

    /// <summary>
    /// Per-service schedule cadence in hours, driving the independent due-check in
    /// <c>ScheduledPrefillService</c>. Follows the shared <c>ScheduleIntervalPicker</c> convention:
    /// <c>&gt; 0</c> = run every N hours, <c>0</c> = paused, <c>-1</c> = run once on startup only.
    /// NOT <c>required</c> so a pre-v2 state.json (which had no per-service interval) still
    /// deserializes; the v1→v2 migration in <see cref="ScheduledPrefillConfigFactory.Migrate"/>
    /// seeds it from the legacy global <c>ServiceIntervals["scheduledPrefill"]</c> value.
    /// </summary>
    public double IntervalHours { get; init; } = ScheduledPrefillConfigFactory.DefaultIntervalHours;
    public required ScheduledPrefillPreset Preset { get; init; }
    public int? TopCount { get; init; }

    /// <summary>
    /// Explicit daemon app ids to prefill for this service. When non-empty, these specific apps
    /// are prefilled and <see cref="Preset"/> (All/Recent/Top) is ignored at run time. When empty,
    /// the preset selection is used. Never null.
    /// </summary>
    public List<string> SelectedAppIds { get; init; } = new();
    public required List<ScheduledPrefillOperatingSystem> OperatingSystems { get; init; }
    public required bool Force { get; init; }
    public required ScheduledPrefillMaxConcurrencyDto MaxConcurrency { get; init; }

    /// <summary>
    /// Per-service override for what happens to this service's persistent container across a
    /// manager restart. <c>null</c> means "use the global
    /// <see cref="ScheduledPrefillConfigDto.PersistenceMode"/>". NOT required so a pre-v3 (or a
    /// v3 config that never set an override) config deserializes. Every DTO-rebuilding copy site
    /// in <see cref="ScheduledPrefillConfigFactory"/> (<c>WithInterval</c>,
    /// <c>CreateDefaultService</c>, <c>ReconcileServicePreset</c>,
    /// <c>ReconcileServiceOperatingSystems</c>) must thread this value through explicitly or a
    /// saved override silently resets on the next load/save.
    /// </summary>
    public PersistenceMode? PersistenceMode { get; init; }
}

/// <summary>
/// Root scheduled prefill configuration persisted in state.json. The schedule interval itself
/// is NOT stored here — it lives in <c>StateService.ServiceIntervals["scheduledPrefill"]</c> in
/// hours like every other <c>ConfigurableScheduledService</c>. This object only holds the
/// per-run runtime guards (<see cref="MaxServiceRuntime"/>/<see cref="StallTimeout"/>) and the
/// per-service settings.
/// </summary>
public sealed class ScheduledPrefillConfigDto
{
    public required int Version { get; init; }
    public required TimeSpan MaxServiceRuntime { get; init; }
    public required TimeSpan StallTimeout { get; init; }
    public required ScheduledPrefillServiceConfigDto Steam { get; init; }
    public required ScheduledPrefillServiceConfigDto Epic { get; init; }
    public required ScheduledPrefillServiceConfigDto Xbox { get; init; }
    public required ScheduledPrefillServiceConfigDto BattleNet { get; init; }
    public required ScheduledPrefillServiceConfigDto Riot { get; init; }

    /// <summary>
    /// Global default for what happens to a persistent container across a manager restart, used
    /// whenever a service has no <see cref="ScheduledPrefillServiceConfigDto.PersistenceMode"/>
    /// override. Nullable only so a pre-v3 state.json (which had no persistence-mode concept)
    /// deserializes; the v2→v3 migration in <see cref="ScheduledPrefillConfigFactory.Migrate"/>
    /// seeds it to <see cref="LancacheManager.Models.PersistenceMode.KeepAcrossRestart"/>.
    /// <see cref="ScheduledPrefillConfigFactory.Validate"/> throws if this is still null at
    /// <see cref="ScheduledPrefillConfigFactory.CurrentVersion"/> - there is no silent fallback
    /// for a required global setting.
    /// </summary>
    public PersistenceMode? PersistenceMode { get; init; }

    /// <summary>
    /// Returns the per-service configs in a stable run order: Steam, Epic, Xbox, Battle.net, Riot.
    /// </summary>
    public IReadOnlyList<ScheduledPrefillServiceConfigDto> GetServicesInRunOrder()
        => new[] { Steam, Epic, Xbox, BattleNet, Riot };

    /// <summary>
    /// Returns only enabled per-service configs in stable run order.
    /// </summary>
    public IReadOnlyList<ScheduledPrefillServiceConfigDto> GetEnabledServicesInRunOrder()
        => GetServicesInRunOrder().Where(s => s.Enabled).ToList();

    /// <summary>
    /// Returns the effective persistence mode for <paramref name="serviceId"/>: its own
    /// per-service override when set, otherwise the global <see cref="PersistenceMode"/>.
    /// Reused by shutdown (<c>PrefillDaemonServiceBase.StopAsync</c>), startup reconcile, and
    /// the fresh-login guard so all three read the same override-then-global precedence rule.
    /// Throws if the global mode is null, which <see cref="ScheduledPrefillConfigFactory.Validate"/>
    /// should already have rejected - this is a defensive fail-loud guard, not a silent default.
    /// </summary>
    public PersistenceMode GetEffectivePersistenceMode(PrefillPlatform serviceId)
    {
        var service = GetServicesInRunOrder().FirstOrDefault(s => s.ServiceId == serviceId)
            ?? throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown scheduled prefill service id.");

        if (service.PersistenceMode is { } overrideMode)
        {
            return overrideMode;
        }

        if (PersistenceMode is { } globalMode)
        {
            return globalMode;
        }

        throw new InvalidOperationException(
            $"Scheduled prefill config's global PersistenceMode is null for {serviceId}; ScheduledPrefillConfigFactory.Validate() must run before GetEffectivePersistenceMode is called.");
    }
}

/// <summary>
/// Single source of truth for constructing and validating <see cref="ScheduledPrefillConfigDto"/>.
/// Used by StateService at the read/write boundaries (default construction for missing config,
/// validation on load and save) so callers never scatter <c>??</c>/<c>||</c> repairs.
/// </summary>
public static class ScheduledPrefillConfigFactory
{
    // Bumped 1 -> 2 when per-service IntervalHours was added (see Migrate).
    // Bumped 2 -> 3 when global + per-service PersistenceMode was added (see Migrate).
    // Bumped 3 -> 4 when per-service ShowNotification (bool) was replaced by NotificationMode (see Migrate).
    public const int CurrentVersion = 4;
    public const int DefaultTopCount = 50;
    public const int MinFixedConcurrency = 1;
    public const int MaxFixedConcurrency = 256;

    /// <summary>Default per-service schedule cadence (hours) when none is configured / migrated.</summary>
    public const double DefaultIntervalHours = 24d;

    /// <summary>Upper bound (hours) for a recurring per-service interval (365 days).</summary>
    public const double MaxIntervalHours = 8760d;

    public static readonly TimeSpan DefaultMaxServiceRuntime = TimeSpan.FromHours(12);
    public static readonly TimeSpan MaxAllowedServiceRuntime = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Builds the default scheduled prefill configuration used when state.json has no
    /// scheduled prefill block yet (migration case). Anonymous services (Battle.net, Riot)
    /// default to enabled; account services default to disabled until login is configured.
    /// </summary>
    public static ScheduledPrefillConfigDto CreateDefault()
    {
        return new ScheduledPrefillConfigDto
        {
            Version = CurrentVersion,
            MaxServiceRuntime = DefaultMaxServiceRuntime,
            StallTimeout = DefaultStallTimeout,
            PersistenceMode = PersistenceMode.KeepAcrossRestart,
            Steam = CreateDefaultService(PrefillPlatform.Steam, enabled: false),
            Epic = CreateDefaultService(PrefillPlatform.Epic, enabled: false),
            Xbox = CreateDefaultService(PrefillPlatform.Xbox, enabled: false),
            BattleNet = CreateDefaultService(PrefillPlatform.BattleNet, enabled: true),
            Riot = CreateDefaultService(PrefillPlatform.Riot, enabled: true)
        };
    }

    /// <summary>
    /// Upgrades an older persisted config to <see cref="CurrentVersion"/> before validation, one
    /// version step at a time. v1 had no per-service <c>IntervalHours</c> (a single global cadence
    /// lived in <c>StateService.ServiceIntervals["scheduledPrefill"]</c>); the v1→v2 step seeds
    /// every service's interval from that legacy global value (sanitized; fallback
    /// <see cref="DefaultIntervalHours"/>) so a pre-feature schedule keeps firing on its old
    /// cadence, now independently per service. v2 had no <c>PersistenceMode</c> concept; the
    /// v2→v3 step seeds the global mode to <see cref="LancacheManager.Models.PersistenceMode.KeepAcrossRestart"/>
    /// (the new default) when it is null and leaves every per-service override null (inherit
    /// global). A config already at the current version is returned unchanged.
    /// </summary>
    public static ScheduledPrefillConfigDto Migrate(ScheduledPrefillConfigDto config, double? legacyGlobalIntervalHours)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Version >= CurrentVersion)
        {
            return config;
        }

        if (config.Version < 2)
        {
            var seededInterval = legacyGlobalIntervalHours is double legacy && IsIntervalHoursValid(legacy)
                ? legacy
                : DefaultIntervalHours;

            config = new ScheduledPrefillConfigDto
            {
                Version = 2,
                MaxServiceRuntime = config.MaxServiceRuntime,
                StallTimeout = config.StallTimeout,
                PersistenceMode = config.PersistenceMode,
                Steam = WithInterval(config.Steam, seededInterval),
                Epic = WithInterval(config.Epic, seededInterval),
                Xbox = WithInterval(config.Xbox, seededInterval),
                BattleNet = WithInterval(config.BattleNet, seededInterval),
                Riot = WithInterval(config.Riot, seededInterval)
            };
        }

        if (config.Version < 3)
        {
            config = new ScheduledPrefillConfigDto
            {
                Version = 3,
                MaxServiceRuntime = config.MaxServiceRuntime,
                StallTimeout = config.StallTimeout,
                PersistenceMode = config.PersistenceMode ?? PersistenceMode.KeepAcrossRestart,
                Steam = config.Steam,
                Epic = config.Epic,
                Xbox = config.Xbox,
                BattleNet = config.BattleNet,
                Riot = config.Riot
            };
        }

        if (config.Version < 4)
        {
            config = new ScheduledPrefillConfigDto
            {
                Version = CurrentVersion,
                MaxServiceRuntime = config.MaxServiceRuntime,
                StallTimeout = config.StallTimeout,
                PersistenceMode = config.PersistenceMode,
                Steam = WithMigratedNotificationMode(config.Steam),
                Epic = WithMigratedNotificationMode(config.Epic),
                Xbox = WithMigratedNotificationMode(config.Xbox),
                BattleNet = WithMigratedNotificationMode(config.BattleNet),
                Riot = WithMigratedNotificationMode(config.Riot)
            };
        }

        return config;
    }

    /// <summary>
    /// v3->v4 step of <see cref="Migrate"/>: seeds <see cref="ScheduledPrefillServiceConfigDto.NotificationMode"/>
    /// from the legacy <see cref="ScheduledPrefillServiceConfigDto.ShowNotification"/> bool when the new
    /// field is absent (true/absent -> All, false -> Silent). A config already carrying
    /// <see cref="ScheduledPrefillServiceConfigDto.NotificationMode"/> is left untouched.
    /// </summary>
    private static ScheduledPrefillServiceConfigDto WithMigratedNotificationMode(ScheduledPrefillServiceConfigDto service)
    {
        if (service.NotificationMode is not null)
        {
            return service;
        }

#pragma warning disable CS0618 // Type or member is obsolete - migration path only
        var migratedMode = service.ShowNotification ? NotificationMode.All : NotificationMode.Silent;
#pragma warning restore CS0618

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
#pragma warning disable CS0618 // migration keeps the legacy value intact so older builds reading this state still see it
            ShowNotification = service.ShowNotification,
#pragma warning restore CS0618
            NotificationMode = migratedMode,
            IntervalHours = service.IntervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="service"/> with <see cref="ScheduledPrefillServiceConfigDto.IntervalHours"/>
    /// replaced. Used by <see cref="Migrate"/> to seed the new per-service cadence while preserving
    /// every other setting (enabled / preset / selected apps / OS / concurrency / persistence mode).
    /// </summary>
    private static ScheduledPrefillServiceConfigDto WithInterval(ScheduledPrefillServiceConfigDto service, double intervalHours)
    {
        ArgumentNullException.ThrowIfNull(service);

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
#pragma warning disable CS0618 // legacy value must survive DTO rebuilds so Migrate's v3->v4 step can still read it
            ShowNotification = service.ShowNotification,
#pragma warning restore CS0618
            NotificationMode = service.NotificationMode,
            IntervalHours = intervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode
        };
    }

    /// <summary>
    /// Returns <paramref name="config"/> with every platform's <see cref="ScheduledPrefillServiceConfigDto.NotificationMode"/>
    /// forced back to the factory default (<see cref="NotificationMode.All"/>). Scheduled prefill's
    /// notification setting lives per-platform in this DTO, NOT in the base
    /// <c>ConfigurableScheduledService</c> override the Schedules-page "Reset to Defaults" action
    /// resets - that base-class reset is a no-op for this service, so the registry's reset path
    /// must call this explicitly or a platform left on Manual/Silent survives a reset unchanged.
    /// </summary>
    public static ScheduledPrefillConfigDto ResetNotificationModes(ScheduledPrefillConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = WithNotificationMode(config.Steam, NotificationMode.All),
            Epic = WithNotificationMode(config.Epic, NotificationMode.All),
            Xbox = WithNotificationMode(config.Xbox, NotificationMode.All),
            BattleNet = WithNotificationMode(config.BattleNet, NotificationMode.All),
            Riot = WithNotificationMode(config.Riot, NotificationMode.All)
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="service"/> with <see cref="ScheduledPrefillServiceConfigDto.NotificationMode"/>
    /// unconditionally replaced by <paramref name="mode"/>. Unlike <see cref="WithMigratedNotificationMode"/>
    /// (which only seeds an absent value), this always overwrites - used by <see cref="ResetNotificationModes"/>.
    /// </summary>
    private static ScheduledPrefillServiceConfigDto WithNotificationMode(ScheduledPrefillServiceConfigDto service, NotificationMode mode)
    {
        ArgumentNullException.ThrowIfNull(service);

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
#pragma warning disable CS0618 // legacy value must survive DTO rebuilds so Migrate's v3->v4 step can still read it
            ShowNotification = service.ShowNotification,
#pragma warning restore CS0618
            NotificationMode = mode,
            IntervalHours = service.IntervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode
        };
    }

    /// <summary>
    /// True when an interval value is acceptable: <c>-1</c> (run on startup only), <c>0</c> (paused),
    /// or a positive value up to <see cref="MaxIntervalHours"/>.
    /// </summary>
    public static bool IsIntervalHoursValid(double intervalHours)
        => intervalHours == -1d || intervalHours == 0d || (intervalHours > 0d && intervalHours <= MaxIntervalHours);

    private static ScheduledPrefillServiceConfigDto CreateDefaultService(PrefillPlatform serviceId, bool enabled)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = serviceId,
            Enabled = enabled,
            NotificationMode = NotificationMode.All,
            IntervalHours = DefaultIntervalHours,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = new List<string>(),
            // Must stay consistent with _supportedOperatingSystemsByService, or Validate() would
            // reconcile (reallocate) the factory's own default config instead of being a no-op.
            OperatingSystems = SupportsOperatingSystemSelection(serviceId)
                ? new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows }
                : new List<ScheduledPrefillOperatingSystem>(),
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto },
            PersistenceMode = null
        };
    }

    /// <summary>
    /// Presets each service's daemon can actually back with real per-user/catalog data. Mirrors
    /// <c>SCHEDULED_PREFILL_SUPPORTED_PRESETS</c> in
    /// <c>Web/src/components/features/management/schedules/scheduled-prefill/constants.ts</c> — keep
    /// both in sync if a daemon's capabilities change. BattleNet/Riot are anonymous catalog mirrors
    /// with no per-user history or popularity signal to sort by; Epic's API exposes cumulative
    /// playtime but no last-played timestamp, so "Recent" has nothing to order by.
    /// </summary>
    private static readonly Dictionary<PrefillPlatform, IReadOnlySet<ScheduledPrefillPreset>> _supportedPresetsByService =
        new()
        {
            [PrefillPlatform.Steam] = new HashSet<ScheduledPrefillPreset>
            {
                ScheduledPrefillPreset.All, ScheduledPrefillPreset.Recent, ScheduledPrefillPreset.Top
            },
            [PrefillPlatform.Epic] = new HashSet<ScheduledPrefillPreset>
            {
                ScheduledPrefillPreset.All, ScheduledPrefillPreset.Top
            },
            [PrefillPlatform.Xbox] = new HashSet<ScheduledPrefillPreset>
            {
                ScheduledPrefillPreset.All, ScheduledPrefillPreset.Recent, ScheduledPrefillPreset.Top
            },
            [PrefillPlatform.BattleNet] = new HashSet<ScheduledPrefillPreset> { ScheduledPrefillPreset.All },
            [PrefillPlatform.Riot] = new HashSet<ScheduledPrefillPreset> { ScheduledPrefillPreset.All }
        };

    /// <summary>
    /// Operating systems each service's daemon can actually filter downloads by. Mirrors
    /// <c>SCHEDULED_PREFILL_SUPPORTED_OPERATING_SYSTEMS</c> in
    /// <c>Web/src/components/features/management/schedules/scheduled-prefill/constants.ts</c> — keep
    /// both in sync if a daemon's capabilities change. Steam is the only daemon that filters depots
    /// by its own PICS <c>config.oslist</c> metadata; Epic/Xbox/BattleNet/Riot either hardcode a
    /// single platform or have no platform concept at all and silently ignore any OS filter sent to
    /// them, so they support none (unlike <see cref="_supportedPresetsByService"/>, there is no
    /// universally-safe non-empty fallback value here).
    /// </summary>
    private static readonly Dictionary<PrefillPlatform, IReadOnlySet<ScheduledPrefillOperatingSystem>> _supportedOperatingSystemsByService =
        new()
        {
            [PrefillPlatform.Steam] = new HashSet<ScheduledPrefillOperatingSystem>
            {
                ScheduledPrefillOperatingSystem.Windows, ScheduledPrefillOperatingSystem.Linux, ScheduledPrefillOperatingSystem.Macos
            },
            [PrefillPlatform.Epic] = new HashSet<ScheduledPrefillOperatingSystem>(),
            [PrefillPlatform.Xbox] = new HashSet<ScheduledPrefillOperatingSystem>(),
            [PrefillPlatform.BattleNet] = new HashSet<ScheduledPrefillOperatingSystem>(),
            [PrefillPlatform.Riot] = new HashSet<ScheduledPrefillOperatingSystem>()
        };

    /// <summary>
    /// True when <paramref name="serviceId"/>'s daemon actually supports filtering downloads by
    /// operating system. Shared by <see cref="ReconcileServiceOperatingSystems"/> (self-heals a
    /// persisted config on load) and <see cref="Core.Services.PrefillDaemonServiceBase.PrefillAsync"/>
    /// (the single runtime enforcement point that strips any OS filter before it reaches a daemon
    /// that would silently ignore it).
    /// </summary>
    public static bool SupportsOperatingSystemSelection(PrefillPlatform serviceId)
        => _supportedOperatingSystemsByService.TryGetValue(serviceId, out var supported) && supported.Count > 0;

    /// <summary>
    /// Coerces any service whose persisted <see cref="ScheduledPrefillServiceConfigDto.Preset"/> is no
    /// longer supported by that service (e.g. saved before per-service preset capability gating
    /// existed, or written directly via the API) back to <see cref="ScheduledPrefillPreset.All"/>,
    /// clearing <see cref="ScheduledPrefillServiceConfigDto.TopCount"/> to match. Called from
    /// <see cref="Validate"/> before its throwing rules run, so a stale value self-heals on the next
    /// read/save instead of rejecting the whole config. Returns <paramref name="config"/> unchanged
    /// when nothing needs reconciling.
    /// </summary>
    private static ScheduledPrefillConfigDto ReconcileUnsupportedPresets(ScheduledPrefillConfigDto config)
    {
        var steam = ReconcileServicePreset(config.Steam);
        var epic = ReconcileServicePreset(config.Epic);
        var xbox = ReconcileServicePreset(config.Xbox);
        var battleNet = ReconcileServicePreset(config.BattleNet);
        var riot = ReconcileServicePreset(config.Riot);

        if (ReferenceEquals(steam, config.Steam) && ReferenceEquals(epic, config.Epic) &&
            ReferenceEquals(xbox, config.Xbox) && ReferenceEquals(battleNet, config.BattleNet) &&
            ReferenceEquals(riot, config.Riot))
        {
            return config;
        }

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = steam,
            Epic = epic,
            Xbox = xbox,
            BattleNet = battleNet,
            Riot = riot
        };
    }

    private static ScheduledPrefillServiceConfigDto ReconcileServicePreset(ScheduledPrefillServiceConfigDto service)
    {
        if (!_supportedPresetsByService.TryGetValue(service.ServiceId, out var supportedPresets)
            || supportedPresets.Contains(service.Preset))
        {
            return service;
        }

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
#pragma warning disable CS0618 // legacy value must survive DTO rebuilds so Migrate's v3->v4 step can still read it
            ShowNotification = service.ShowNotification,
#pragma warning restore CS0618
            NotificationMode = service.NotificationMode,
            IntervalHours = service.IntervalHours,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode
        };
    }

    /// <summary>
    /// Coerces any service whose persisted <see cref="ScheduledPrefillServiceConfigDto.OperatingSystems"/>
    /// contains a value that service's daemon doesn't actually support (see
    /// <see cref="_supportedOperatingSystemsByService"/>) down to only the still-supported subset —
    /// an empty list for every service except Steam, since there is no lesser fallback value. Called
    /// from <see cref="Validate"/> alongside <see cref="ReconcileUnsupportedPresets"/>, before its
    /// throwing rules run. Returns <paramref name="config"/> unchanged when nothing needs reconciling.
    /// </summary>
    private static ScheduledPrefillConfigDto ReconcileUnsupportedOperatingSystems(ScheduledPrefillConfigDto config)
    {
        var steam = ReconcileServiceOperatingSystems(config.Steam);
        var epic = ReconcileServiceOperatingSystems(config.Epic);
        var xbox = ReconcileServiceOperatingSystems(config.Xbox);
        var battleNet = ReconcileServiceOperatingSystems(config.BattleNet);
        var riot = ReconcileServiceOperatingSystems(config.Riot);

        if (ReferenceEquals(steam, config.Steam) && ReferenceEquals(epic, config.Epic) &&
            ReferenceEquals(xbox, config.Xbox) && ReferenceEquals(battleNet, config.BattleNet) &&
            ReferenceEquals(riot, config.Riot))
        {
            return config;
        }

        return new ScheduledPrefillConfigDto
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = steam,
            Epic = epic,
            Xbox = xbox,
            BattleNet = battleNet,
            Riot = riot
        };
    }

    private static ScheduledPrefillServiceConfigDto ReconcileServiceOperatingSystems(ScheduledPrefillServiceConfigDto service)
    {
        var supportedOperatingSystems = _supportedOperatingSystemsByService.TryGetValue(service.ServiceId, out var supported)
            ? supported
            : new HashSet<ScheduledPrefillOperatingSystem>();

        if (service.OperatingSystems.All(supportedOperatingSystems.Contains))
        {
            return service;
        }

        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
#pragma warning disable CS0618 // legacy value must survive DTO rebuilds so Migrate's v3->v4 step can still read it
            ShowNotification = service.ShowNotification,
#pragma warning restore CS0618
            NotificationMode = service.NotificationMode,
            IntervalHours = service.IntervalHours,
            Preset = service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = service.OperatingSystems.Where(supportedOperatingSystems.Contains).ToList(),
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode
        };
    }

    /// <summary>
    /// Validates a scheduled prefill config. First reconciles any per-service preset or operating
    /// system selection that its own service no longer supports (see
    /// <see cref="ReconcileUnsupportedPresets"/>, <see cref="ReconcileUnsupportedOperatingSystems"/>),
    /// then throws <see cref="ScheduledPrefillConfigValidationException"/> with an explicit message
    /// on the first remaining failed rule. Returns the reconciled instance (the same instance when
    /// nothing needed reconciling).
    /// </summary>
    public static ScheduledPrefillConfigDto Validate(ScheduledPrefillConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        config = ReconcileUnsupportedPresets(config);
        config = ReconcileUnsupportedOperatingSystems(config);

        if (config.Version != CurrentVersion)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"Unsupported scheduled prefill config version {config.Version}; expected {CurrentVersion}.");
        }

        if (config.PersistenceMode is not { } globalPersistenceMode)
        {
            throw new ScheduledPrefillConfigValidationException(
                "PersistenceMode is required and must not be null (Migrate should have seeded a default; refusing to silently default a required global setting).");
        }

        if (!Enum.IsDefined(globalPersistenceMode))
        {
            throw new ScheduledPrefillConfigValidationException(
                $"PersistenceMode '{globalPersistenceMode}' is not a supported value.");
        }

        if (config.MaxServiceRuntime <= TimeSpan.Zero)
        {
            throw new ScheduledPrefillConfigValidationException(
                "MaxServiceRuntime must be positive.");
        }

        if (config.MaxServiceRuntime > MaxAllowedServiceRuntime)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"MaxServiceRuntime must not exceed {MaxAllowedServiceRuntime.TotalHours} hours.");
        }

        if (config.StallTimeout <= TimeSpan.Zero)
        {
            throw new ScheduledPrefillConfigValidationException(
                "StallTimeout must be positive.");
        }

        if (config.StallTimeout >= config.MaxServiceRuntime)
        {
            throw new ScheduledPrefillConfigValidationException(
                "StallTimeout must be less than MaxServiceRuntime.");
        }

        ValidateService(config.Steam, PrefillPlatform.Steam);
        ValidateService(config.Epic, PrefillPlatform.Epic);
        ValidateService(config.Xbox, PrefillPlatform.Xbox);
        ValidateService(config.BattleNet, PrefillPlatform.BattleNet);
        ValidateService(config.Riot, PrefillPlatform.Riot);

        return config;
    }

    private static void ValidateService(ScheduledPrefillServiceConfigDto? service, PrefillPlatform expectedServiceId)
    {
        if (service is null)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"Missing scheduled prefill config for {expectedServiceId}.");
        }

        if (service.ServiceId != expectedServiceId)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"Service config ServiceId '{service.ServiceId}' does not match its container slot '{expectedServiceId}'.");
        }

        // NotificationMode is a required field on a current-version (v4) config. Validate only ever
        // runs at CurrentVersion, so a null here is a current-schema payload that omitted the field,
        // NOT a legacy config (Migrate seeds those from the legacy ShowNotification flag before this
        // runs). Reject it rather than silently defaulting a required field.
        if (service.NotificationMode is not { } notificationMode)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} NotificationMode is required (Migrate seeds it for pre-v{CurrentVersion} configs; refusing to silently default a required field).");
        }

        if (!Enum.IsDefined(notificationMode))
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} NotificationMode '{notificationMode}' is not a supported value.");
        }

        if (!IsIntervalHoursValid(service.IntervalHours))
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} IntervalHours must be -1 (run on startup), 0 (paused), or between 0 (exclusive) and {MaxIntervalHours} hours.");
        }

        if (service.SelectedAppIds is null)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} SelectedAppIds must not be null (use an empty list to fall back to the preset).");
        }

        foreach (var appId in service.SelectedAppIds)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                throw new ScheduledPrefillConfigValidationException(
                    $"{expectedServiceId} SelectedAppIds must not contain empty entries.");
            }
        }

        if (service.SelectedAppIds.Distinct().Count() != service.SelectedAppIds.Count)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} SelectedAppIds must not contain duplicates.");
        }

        if (service.OperatingSystems is null)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} OperatingSystems must not be null.");
        }

        var distinctOsCount = service.OperatingSystems.Distinct().Count();
        if (distinctOsCount != service.OperatingSystems.Count)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} OperatingSystems must not contain duplicates.");
        }

        foreach (var os in service.OperatingSystems)
        {
            if (!Enum.IsDefined(os))
            {
                throw new ScheduledPrefillConfigValidationException(
                    $"{expectedServiceId} OperatingSystems contains an unsupported value '{os}'.");
            }
        }

        if (!Enum.IsDefined(service.Preset))
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} Preset '{service.Preset}' is not a supported value.");
        }

        if (service.Preset == ScheduledPrefillPreset.Top)
        {
            if (service.TopCount is null)
            {
                throw new ScheduledPrefillConfigValidationException(
                    $"{expectedServiceId} requires an explicit TopCount when Preset is Top.");
            }

            if (service.TopCount <= 0)
            {
                throw new ScheduledPrefillConfigValidationException(
                    $"{expectedServiceId} TopCount must be positive.");
            }
        }
        else if (service.TopCount is not null)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} TopCount must be null unless Preset is Top.");
        }

        if (service.PersistenceMode is { } persistenceMode && !Enum.IsDefined(persistenceMode))
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{expectedServiceId} PersistenceMode '{persistenceMode}' is not a supported value.");
        }

        ValidateMaxConcurrency(service.MaxConcurrency, expectedServiceId);
    }

    private static void ValidateMaxConcurrency(ScheduledPrefillMaxConcurrencyDto? concurrency, PrefillPlatform serviceId)
    {
        if (concurrency is null)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"{serviceId} MaxConcurrency must not be null.");
        }

        switch (concurrency.Mode)
        {
            case ScheduledPrefillMaxConcurrencyMode.Auto:
                if (concurrency.Value is not null)
                {
                    throw new ScheduledPrefillConfigValidationException(
                        $"{serviceId} MaxConcurrency.Value must be null when Mode is Auto.");
                }
                break;

            case ScheduledPrefillMaxConcurrencyMode.Fixed:
                if (concurrency.Value is null)
                {
                    throw new ScheduledPrefillConfigValidationException(
                        $"{serviceId} MaxConcurrency.Value is required when Mode is Fixed.");
                }

                if (concurrency.Value < MinFixedConcurrency || concurrency.Value > MaxFixedConcurrency)
                {
                    throw new ScheduledPrefillConfigValidationException(
                        $"{serviceId} MaxConcurrency.Value must be between {MinFixedConcurrency} and {MaxFixedConcurrency}.");
                }
                break;

            default:
                throw new ScheduledPrefillConfigValidationException(
                    $"{serviceId} MaxConcurrency.Mode '{concurrency.Mode}' is not a supported value.");
        }
    }
}

/// <summary>
/// Thrown by <see cref="ScheduledPrefillConfigFactory.Validate"/> when a scheduled prefill
/// configuration fails a validation rule. Carries an explicit, admin-facing message.
/// </summary>
public sealed class ScheduledPrefillConfigValidationException : Exception
{
    public ScheduledPrefillConfigValidationException(string message) : base(message)
    {
    }
}
