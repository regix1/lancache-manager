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
    /// Returns the per-service configs in a stable run order: Steam, Epic, Xbox, Battle.net, Riot.
    /// </summary>
    public IReadOnlyList<ScheduledPrefillServiceConfigDto> GetServicesInRunOrder()
        => new[] { Steam, Epic, Xbox, BattleNet, Riot };

    /// <summary>
    /// Returns only enabled per-service configs in stable run order.
    /// </summary>
    public IReadOnlyList<ScheduledPrefillServiceConfigDto> GetEnabledServicesInRunOrder()
        => GetServicesInRunOrder().Where(s => s.Enabled).ToList();
}

/// <summary>
/// Single source of truth for constructing and validating <see cref="ScheduledPrefillConfigDto"/>.
/// Used by StateService at the read/write boundaries (default construction for missing config,
/// validation on load and save) so callers never scatter <c>??</c>/<c>||</c> repairs.
/// </summary>
public static class ScheduledPrefillConfigFactory
{
    public const int CurrentVersion = 1;
    public const int DefaultTopCount = 50;
    public const int MinFixedConcurrency = 1;
    public const int MaxFixedConcurrency = 256;

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
            Steam = CreateDefaultService(PrefillPlatform.Steam, enabled: false),
            Epic = CreateDefaultService(PrefillPlatform.Epic, enabled: false),
            Xbox = CreateDefaultService(PrefillPlatform.Xbox, enabled: false),
            BattleNet = CreateDefaultService(PrefillPlatform.BattleNet, enabled: true),
            Riot = CreateDefaultService(PrefillPlatform.Riot, enabled: true)
        };
    }

    private static ScheduledPrefillServiceConfigDto CreateDefaultService(PrefillPlatform serviceId, bool enabled)
    {
        return new ScheduledPrefillServiceConfigDto
        {
            ServiceId = serviceId,
            Enabled = enabled,
            Preset = ScheduledPrefillPreset.All,
            TopCount = null,
            SelectedAppIds = new List<string>(),
            OperatingSystems = new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows },
            Force = false,
            MaxConcurrency = new ScheduledPrefillMaxConcurrencyDto { Mode = ScheduledPrefillMaxConcurrencyMode.Auto }
        };
    }

    /// <summary>
    /// Validates a scheduled prefill config. Throws <see cref="ScheduledPrefillConfigValidationException"/>
    /// with an explicit message on the first failed rule. Returns the same instance for convenience.
    /// </summary>
    public static ScheduledPrefillConfigDto Validate(ScheduledPrefillConfigDto config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Version != CurrentVersion)
        {
            throw new ScheduledPrefillConfigValidationException(
                $"Unsupported scheduled prefill config version {config.Version}; expected {CurrentVersion}.");
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
