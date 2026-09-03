using System.Text.Json;
using System.Text.Json.Serialization;
using LancacheManager.Controllers;
using LancacheManager.Infrastructure.Services.Scheduling;
using LancacheManager.Models;

namespace LancacheManager.Tests;

/// <summary>
/// Covers the per-service <c>CustomSchedule</c> that v5 of the scheduled prefill config adds: its wire
/// shape, the six DTO-rebuilding copy sites that would otherwise reset it on the next load or save,
/// the v4 -> v5 migration, and the validation that refuses to persist a schedule nothing would ever
/// run on.
/// </summary>
public class ScheduledPrefillCustomScheduleTests
{
    // Mirrors the REST payload options in Program.cs (camelCase names, null fields omitted), so the
    // shape these tests assert on is the shape the frontend actually receives.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ---- Wire shape and round trips ----

    [Fact]
    public void SaveThenLoad_MidnightCrossingWindow_PreservesTheScheduleExactly()
    {
        var saved = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 */2 * * *", "Europe/Berlin", new TimeOnly(22, 0), new TimeOnly(6, 0)));

        var loaded = RoundTrip(saved, WireOptions);

        var schedule = Assert.IsType<CustomSchedule>(loaded.Steam.CustomSchedule);
        Assert.Equal("0 */2 * * *", schedule.Expression);
        Assert.Equal("Europe/Berlin", schedule.TimeZoneId);
        Assert.Equal(new TimeOnly(22, 0), schedule.WindowStart);
        Assert.Equal(new TimeOnly(6, 0), schedule.WindowEnd);
    }

    [Fact]
    public void SaveThenLoad_NoWindow_KeepsBothBoundsNull()
    {
        var saved = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 2 * * *", "UTC", windowStart: null, windowEnd: null));

        var loaded = RoundTrip(saved, WireOptions);

        var schedule = Assert.IsType<CustomSchedule>(loaded.Steam.CustomSchedule);
        Assert.Equal("0 2 * * *", schedule.Expression);
        Assert.Null(schedule.WindowStart);
        Assert.Null(schedule.WindowEnd);
    }

    [Fact]
    public void SaveThenLoad_ThroughTheStateFileShape_PreservesTheSchedule()
    {
        // state.json is written with the default options (PascalCase names, nulls included) rather than
        // the REST ones, so the persisted shape is exercised separately from the payload shape.
        var saved = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("30 3 * * 1", "UTC", new TimeOnly(1, 0), new TimeOnly(5, 0)));

        var loaded = RoundTrip(saved, new JsonSerializerOptions());

        var schedule = Assert.IsType<CustomSchedule>(loaded.Steam.CustomSchedule);
        Assert.Equal("30 3 * * 1", schedule.Expression);
        Assert.Equal(new TimeOnly(1, 0), schedule.WindowStart);
        Assert.Equal(new TimeOnly(5, 0), schedule.WindowEnd);
    }

    [Fact]
    public void Serialize_WindowBounds_AreWrittenAsHoursAndMinutes()
    {
        var config = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 */2 * * *", "Europe/Berlin", new TimeOnly(22, 0), new TimeOnly(6, 0)));

        var json = JsonSerializer.Serialize(config, WireOptions);

        // The built-in TimeOnly converter would write "22:00:00.0000000", which carries resolution a
        // window bound does not have and which the modal's time fields cannot read back.
        Assert.Contains("\"windowStart\":\"22:00\"", json);
        Assert.Contains("\"windowEnd\":\"06:00\"", json);
    }

    [Fact]
    public void Deserialize_ConfigWithNoCustomScheduleKey_LoadsWithTheFieldNull()
    {
        // A state.json written before v5 simply has no custom-schedule key at all. Omitting nulls on
        // the way out reproduces exactly that file, and it must still deserialize - which is why the
        // field is declared without `required`.
        var beforeTheField = JsonSerializer.Serialize(ScheduledPrefillConfigFactory.CreateDefault(), WireOptions);
        Assert.DoesNotContain("customSchedule", beforeTheField);

        var loaded = JsonSerializer.Deserialize<ScheduledPrefillConfigDto>(beforeTheField, WireOptions);

        Assert.NotNull(loaded);
        foreach (var service in loaded.GetServicesInRunOrder())
        {
            Assert.Null(service.CustomSchedule);
        }
    }

    [Fact]
    public void ScheduleRow_CarriesTheCustomSchedule_SoARowCanWordItsOwnTiming()
    {
        // IntervalHours alone cannot tell a paused service apart from one that keeps a paused interval
        // while a schedule drives it, so the row would otherwise read "Paused" for a service that runs
        // every night.
        var row = new ScheduledPrefillServiceScheduleDto
        {
            ServiceId = PrefillPlatform.Steam,
            IntervalHours = 0d,
            Enabled = true,
            IsRunning = false,
            LastRunUtc = null,
            NextRunUtc = new DateTime(2026, 1, 2, 2, 0, 0, DateTimeKind.Utc),
            CustomSchedule = DailySchedule
        };

        var json = JsonSerializer.Serialize(row, WireOptions);

        Assert.Contains("\"customSchedule\"", json);
        Assert.Contains("\"expression\":\"0 2 * * *\"", json);
    }

    // ---- The six per-service copy sites ----

    [Fact]
    public void CreateDefault_LeavesEveryServiceOnItsInterval()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        foreach (var service in config.GetServicesInRunOrder())
        {
            Assert.Null(service.CustomSchedule);
        }
    }

    [Fact]
    public void Migrate_V1Config_KeepsTheCustomSchedule()
    {
        // The v1 -> v2 step rebuilds every service to seed the per-service cadence, so a schedule that
        // was already saved has to survive that rebuild.
        var v1 = WithSteamSchedule(ConfigAtVersion(1), DailySchedule);

        var migrated = ScheduledPrefillConfigFactory.Migrate(v1, legacyGlobalIntervalHours: 72d);

        AssertIsDailySchedule(migrated.Steam.CustomSchedule);
        Assert.Equal(72d, migrated.Steam.IntervalHours);
    }

    [Fact]
    public void Migrate_V3ConfigWithNoNotificationMode_KeepsTheCustomSchedule()
    {
        // The v3 -> v4 step only rebuilds a service whose notification mode is still absent, so the
        // mode has to be left unset here or the rebuild this test is aimed at never runs.
        var config = ConfigAtVersion(3);
        var steam = Reconfigure(config.Steam, schedule: DailySchedule, clearNotificationMode: true);
        var v3 = ReplaceSteam(config, steam);

        var migrated = ScheduledPrefillConfigFactory.Migrate(v3, legacyGlobalIntervalHours: null);

        AssertIsDailySchedule(migrated.Steam.CustomSchedule);
        Assert.NotNull(migrated.Steam.NotificationMode);
    }

    [Fact]
    public void ResetNotificationModes_KeepsTheCustomSchedule()
    {
        // Reset to Defaults rebuilds every service to force its notification mode back. It resets how
        // a run is announced, not when it happens.
        var config = WithSteamSchedule(ScheduledPrefillConfigFactory.CreateDefault(), DailySchedule);

        var reset = ScheduledPrefillConfigFactory.ResetNotificationModes(config);

        AssertIsDailySchedule(reset.Steam.CustomSchedule);
        Assert.Equal(NotificationMode.All, reset.Steam.NotificationMode);
    }

    [Fact]
    public void Validate_UnsupportedPreset_ReconcilesAndKeepsTheCustomSchedule()
    {
        // Battle.net's daemon backs only the All preset, so a persisted Recent is coerced back and the
        // service record is rebuilt around it.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var battleNet = Reconfigure(config.BattleNet, schedule: DailySchedule, preset: ScheduledPrefillPreset.Recent);

        var validated = ScheduledPrefillConfigFactory.Validate(ReplaceBattleNet(config, battleNet));

        Assert.Equal(ScheduledPrefillPreset.All, validated.BattleNet.Preset);
        AssertIsDailySchedule(validated.BattleNet.CustomSchedule);
    }

    [Fact]
    public void Validate_UnsupportedOperatingSystem_ReconcilesAndKeepsTheCustomSchedule()
    {
        // Epic's daemon takes no OS selection at all, so a persisted Windows entry is stripped and the
        // service record is rebuilt around it.
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var epic = Reconfigure(
            config.Epic,
            schedule: DailySchedule,
            operatingSystems: new List<ScheduledPrefillOperatingSystem> { ScheduledPrefillOperatingSystem.Windows });

        var validated = ScheduledPrefillConfigFactory.Validate(ReplaceEpic(config, epic));

        Assert.Empty(validated.Epic.OperatingSystems);
        AssertIsDailySchedule(validated.Epic.CustomSchedule);
    }

    // ---- Version ----

    [Fact]
    public void CurrentVersion_IsTheVersionThatCarriesCustomSchedules()
    {
        Assert.Equal(5, ScheduledPrefillConfigFactory.CurrentVersion);
    }

    [Fact]
    public void Migrate_V4Config_IsStampedToCurrentVersion_AndEverySettingSurvives()
    {
        var v4 = ConfigAtVersion(4);

        var migrated = ScheduledPrefillConfigFactory.Migrate(v4, legacyGlobalIntervalHours: null);

        Assert.Equal(ScheduledPrefillConfigFactory.CurrentVersion, migrated.Version);
        Assert.Equal(v4.MaxServiceRuntime, migrated.MaxServiceRuntime);
        Assert.Equal(v4.StallTimeout, migrated.StallTimeout);
        Assert.Equal(v4.PersistenceMode, migrated.PersistenceMode);
        foreach (var service in migrated.GetServicesInRunOrder())
        {
            // A pre-v5 config has no schedule to seed, and null already means "run on the interval",
            // which is exactly what that config was doing.
            Assert.Null(service.CustomSchedule);
            Assert.Equal(ScheduledPrefillConfigFactory.DefaultIntervalHours, service.IntervalHours);
        }

        ScheduledPrefillConfigFactory.Validate(migrated);
    }

    // ---- Validation ----

    [Fact]
    public void Validate_ScheduleThatCanNeverRunInsideItsWindow_IsRejectedWithAnExplanation()
    {
        // 15:00 daily never falls between 22:00 and 06:00. Caught on save instead of leaving behind a
        // service that quietly never runs again.
        var config = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 15 * * *", "UTC", new TimeOnly(22, 0), new TimeOnly(6, 0)));

        var error = Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));

        Assert.Contains("Steam", error.Message);
        Assert.Contains("never runs", error.Message);
    }

    [Fact]
    public void Validate_ExpressionTheServerCannotRead_IsRejected()
    {
        var config = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("not a schedule", "UTC", windowStart: null, windowEnd: null));

        var error = Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));

        Assert.Contains("Steam", error.Message);
    }

    [Fact]
    public void Validate_WindowsTimeZoneName_IsRejected()
    {
        // It resolves on a Windows dev box and not in the Linux container, so a schedule saved with one
        // would mean two different things depending on where it ran.
        var config = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 2 * * *", "W. Europe Standard Time", windowStart: null, windowEnd: null));

        var error = Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));

        Assert.Contains("Steam", error.Message);
        Assert.Contains("Windows name", error.Message);
    }

    [Fact]
    public void Validate_HalfSetWindow_IsRejected()
    {
        var config = WithSteamSchedule(
            ScheduledPrefillConfigFactory.CreateDefault(),
            MakeSchedule("0 2 * * *", "UTC", new TimeOnly(22, 0), windowEnd: null));

        var error = Assert.Throws<ScheduledPrefillConfigValidationException>(
            () => ScheduledPrefillConfigFactory.Validate(config));

        Assert.Contains("Steam", error.Message);
    }

    [Theory]
    [InlineData(-1d)] // startup-only: the schedule is in charge, so the sentinel is not a conflict
    [InlineData(0d)]  // paused
    [InlineData(24d)] // ordinary recurring interval
    public void Validate_UsableScheduleOnAnyIntervalValue_IsAccepted(double intervalHours)
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        var steam = Reconfigure(config.Steam, schedule: DailySchedule, intervalHours: intervalHours);

        var validated = ScheduledPrefillConfigFactory.Validate(ReplaceSteam(config, steam));

        AssertIsDailySchedule(validated.Steam.CustomSchedule);
        // The interval is left exactly as it was, so clearing the schedule restores the old cadence.
        Assert.Equal(intervalHours, validated.Steam.IntervalHours);
    }

    [Fact]
    public void Validate_NoCustomSchedule_IsUnaffected()
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();

        var validated = ScheduledPrefillConfigFactory.Validate(config);

        Assert.Null(validated.Steam.CustomSchedule);
    }

    // ---- Builders ----

    private static CustomSchedule DailySchedule
        => MakeSchedule("0 2 * * *", "UTC", windowStart: null, windowEnd: null);

    private static CustomSchedule MakeSchedule(
        string expression, string timeZoneId, TimeOnly? windowStart, TimeOnly? windowEnd)
    {
        return new CustomSchedule
        {
            Expression = expression,
            TimeZoneId = timeZoneId,
            WindowStart = windowStart,
            WindowEnd = windowEnd
        };
    }

    private static void AssertIsDailySchedule(CustomSchedule? schedule)
    {
        var actual = Assert.IsType<CustomSchedule>(schedule);
        Assert.Equal("0 2 * * *", actual.Expression);
        Assert.Equal("UTC", actual.TimeZoneId);
        Assert.Null(actual.WindowStart);
        Assert.Null(actual.WindowEnd);
    }

    /// <summary>
    /// Serializes, deserializes and revalidates <paramref name="config"/> the way a save followed by a
    /// load does, and returns what came back.
    /// </summary>
    private static ScheduledPrefillConfigDto RoundTrip(ScheduledPrefillConfigDto config, JsonSerializerOptions options)
    {
        var validated = ScheduledPrefillConfigFactory.Validate(config);
        var json = JsonSerializer.Serialize(validated, options);
        var loaded = JsonSerializer.Deserialize<ScheduledPrefillConfigDto>(json, options);

        Assert.NotNull(loaded);
        return ScheduledPrefillConfigFactory.Validate(
            ScheduledPrefillConfigFactory.Migrate(loaded, legacyGlobalIntervalHours: null));
    }

    /// <summary>
    /// Returns a copy of <paramref name="service"/> with only the named settings replaced; a null
    /// argument leaves that setting as it was. Removing a value rather than replacing one needs an
    /// explicit flag, which is what <paramref name="clearNotificationMode"/> is for: the v3 -> v4
    /// migration only rebuilds a service whose mode is still absent.
    /// </summary>
    private static ScheduledPrefillServiceConfigDto Reconfigure(
        ScheduledPrefillServiceConfigDto service,
        CustomSchedule? schedule = null,
        double? intervalHours = null,
        ScheduledPrefillPreset? preset = null,
        List<ScheduledPrefillOperatingSystem>? operatingSystems = null,
        bool clearNotificationMode = false)
        => new()
        {
            ServiceId = service.ServiceId,
            Enabled = service.Enabled,
            NotificationMode = clearNotificationMode ? null : service.NotificationMode,
            IntervalHours = intervalHours ?? service.IntervalHours,
            Preset = preset ?? service.Preset,
            TopCount = service.TopCount,
            SelectedAppIds = service.SelectedAppIds,
            OperatingSystems = operatingSystems ?? service.OperatingSystems,
            Force = service.Force,
            MaxConcurrency = service.MaxConcurrency,
            PersistenceMode = service.PersistenceMode,
            CustomSchedule = schedule ?? service.CustomSchedule
        };

    private static ScheduledPrefillConfigDto ConfigAtVersion(int version)
    {
        var config = ScheduledPrefillConfigFactory.CreateDefault();
        return new ScheduledPrefillConfigDto
        {
            Version = version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };
    }

    private static ScheduledPrefillConfigDto WithSteamSchedule(ScheduledPrefillConfigDto config, CustomSchedule schedule)
        => ReplaceSteam(config, Reconfigure(config.Steam, schedule: schedule));

    private static ScheduledPrefillConfigDto ReplaceSteam(ScheduledPrefillConfigDto config, ScheduledPrefillServiceConfigDto steam)
        => new()
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };

    private static ScheduledPrefillConfigDto ReplaceEpic(ScheduledPrefillConfigDto config, ScheduledPrefillServiceConfigDto epic)
        => new()
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = epic,
            Xbox = config.Xbox,
            BattleNet = config.BattleNet,
            Riot = config.Riot
        };

    private static ScheduledPrefillConfigDto ReplaceBattleNet(ScheduledPrefillConfigDto config, ScheduledPrefillServiceConfigDto battleNet)
        => new()
        {
            Version = config.Version,
            MaxServiceRuntime = config.MaxServiceRuntime,
            StallTimeout = config.StallTimeout,
            PersistenceMode = config.PersistenceMode,
            Steam = config.Steam,
            Epic = config.Epic,
            Xbox = config.Xbox,
            BattleNet = battleNet,
            Riot = config.Riot
        };
}
